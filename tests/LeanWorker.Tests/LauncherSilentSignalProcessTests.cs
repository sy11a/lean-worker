using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Issue #146, fix round 3, rule (a): a launcher that inherited SIGTERM as ignored (a shell that ran <c>trap '' TERM</c>;
/// the runtime hides that from the SigIgn check) records the signal and survives it; with a SILENT worker no stream line
/// follows, so the file must stop showing <c>launcher-signalled</c> by itself within a few seconds (the hold's timer), and
/// the run ends as with LEAN_WORKER_STATUS=off. Every process and process group this class starts is killed by it, and
/// every wait is bounded.
/// </summary>
[Collection("launcher-process-state")]
public sealed class LauncherSilentSignalProcessTests : IAsyncDisposable
{
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(60);

    // The record may last the hold (2 s) plus the timer's and the scheduler's slack; the test waits far longer than that.
    private static readonly TimeSpan _clears = TimeSpan.FromSeconds(10);

    private readonly TempDirs _dirs = new();
    private readonly List<int> _workerPids = [];
    private readonly List<Process> _launchers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (int pid in _workerPids)
        {
            await LauncherProcess.SignalAsync("KILL", -pid);
            await LauncherProcess.SignalAsync("KILL", pid);
        }

        foreach (Process launcher in _launchers)
        {
            try
            {
                if (!launcher.HasExited)
                {
                    launcher.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            launcher.Dispose();
        }

        _dirs.Dispose();
    }

    private sealed record Arm(int ExitCode, string Block, string Summary, string SummaryStatus, bool Survived, bool SawRecord, TimeSpan? RecordGoneAfter, string? PhaseWhenGone, string? FinalEnd);

    private static ProcessStartInfo IgnoringTerm(ProcessStartInfo inner)
    {
        ProcessStartInfo outer = new("sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = inner.WorkingDirectory,
        };
        outer.ArgumentList.Add("-c");
        outer.ArgumentList.Add("trap '' TERM; exec \"$@\"");
        outer.ArgumentList.Add("sh");
        outer.ArgumentList.Add(inner.FileName);
        foreach (string arg in inner.ArgumentList)
        {
            outer.ArgumentList.Add(arg);
        }

        outer.Environment.Clear();
        foreach (KeyValuePair<string, string?> variable in inner.Environment)
        {
            outer.Environment[variable.Key] = variable.Value;
        }

        return outer;
    }

    private async Task<Arm> RunArmAsync(bool statusOn)
    {
        string root = _dirs.NewRoot();
        string control = _dirs.Create("lw-silent-ctl");
        string marker = Path.Combine(control, "worker.pid");
        string release = Path.Combine(control, "release");
        // A silent worker: it prints nothing until released, then a normal successful stream.
        string body = "cat >/dev/null\necho $$ > '" + marker + ".tmp'\nmv '" + marker + ".tmp' '" + marker + "'\n"
            + "i=0; while [ ! -f '" + release + "' ] && [ $i -lt 1200 ]; do sleep 0.1; i=$((i+1)); done\n" + RunAsyncGolden.SuccessStream + "\n";
        string stub = await LauncherProcess.StubAsync(_dirs, "claude", body);
        ProcessStartInfo inner = LauncherProcess.StartInfo(
            ["--task", Path.Combine(root, "task.md"), "--runs-root", root, "--model", "anthropic/claude-haiku-4-5", "--mode", "bare", "--name", "silent"],
            _dirs.Create("lw-silent-cwd"), _dirs.Create("lw-silent-state"), ("PATH", stub + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")),
            ("ANTHROPIC_API_KEY", "dummy-test-key"), (RunStatus.KillSwitch, statusOn ? null : "off"));
        Process launcher = Process.Start(IgnoringTerm(inner))!;
        _launchers.Add(launcher);
        Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = launcher.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        int worker = await WaitForWorkerAsync(marker, root, statusOn);
        _workerPids.Add(worker);
        await LauncherProcess.SignalAsync("TERM", launcher.Id);

        // The worker stays silent: nothing but the hold's timer can change the file after the signal.
        Seen seen = statusOn ? await WatchRecordAsync(root, launcher) : new Seen(SawRecord: false, GoneAfter: null, PhaseWhenGone: null);
        bool survived = !launcher.HasExited;
        await File.WriteAllTextAsync(release, "go", TestContext.Current.CancellationToken);
        await launcher.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);
        string output = await stdout.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        _ = await stderr.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        string runDir = Directory.GetDirectories(Path.Combine(root, "runs")).Single();
        JsonObject? summary = File.Exists(Path.Combine(runDir, "summary.json")) ? RunAsyncGolden.Summary(runDir) : null;
        return new Arm(launcher.ExitCode, LaunchCompare.Block(root, output), summary is null ? string.Empty : LaunchCompare.Comparable(root, summary),
            summary?["status"]?.GetValue<string>() ?? "none", survived, seen.SawRecord, seen.GoneAfter, seen.PhaseWhenGone,
            statusOn ? StatusOf(root)?["end"]?["status"]?.GetValue<string>() : null);
    }

    private sealed record Seen(bool SawRecord, TimeSpan? GoneAfter, string? PhaseWhenGone);

    // Polls the file (at most _clears, and only while the launcher runs) until the signal record that was there is gone.
    private static async Task<Seen> WatchRecordAsync(string root, Process launcher)
    {
        bool sawRecord = false;
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < _clears && !launcher.HasExited)
        {
            JsonObject? doc = StatusOf(root);
            bool record = doc?["end"]?["status"]?.GetValue<string>() is "launcher-signalled";
            sawRecord |= record;
            if (sawRecord && !record)
            {
                return new Seen(SawRecord: true, watch.Elapsed, doc?["phase"]?.GetValue<string>());
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return new Seen(sawRecord, GoneAfter: null, PhaseWhenGone: null);
    }

    private static async Task<int> WaitForWorkerAsync(string marker, string root, bool statusOn)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < _limit)
        {
            if (File.Exists(marker) && (!statusOn || StatusOf(root)?["phase"]?.GetValue<string>() is "worker"))
            {
                return int.Parse((await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Trim(), CultureInfo.InvariantCulture);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the worker did not start within " + _limit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        return 0;
    }

    private static JsonObject? StatusOf(string root)
    {
        string runs = Path.Combine(root, "runs");
        if (!Directory.Exists(runs) || Directory.GetDirectories(runs).FirstOrDefault() is not { } dir || !File.Exists(Path.Combine(dir, "status.json")))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "status.json")))!.AsObject();
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_launcher_that_survives_an_ignored_SIGTERM_with_a_silent_worker_stops_showing_launcher_signalled_within_seconds_and_ends_as_with_the_writer_offAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX signals and a POSIX shell");

        Arm off = await RunArmAsync(statusOn: false);
        Arm on = await RunArmAsync(statusOn: true);

        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"silent-worker TERM: writer off: survived {off.Survived}, exit {off.ExitCode}; writer on: survived {on.Survived}, exit {on.ExitCode}, record seen {on.SawRecord}, gone after {on.RecordGoneAfter?.TotalSeconds:F2} s, phase then {on.PhaseWhenGone}, final end {on.FinalEnd}"));
        // The run ends the same way in both arms.
        Assert.Equal(off.Survived, on.Survived);
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Equal(off.Block, on.Block);
        Assert.Equal(off.Summary, on.Summary);
        Assert.Equal(off.SummaryStatus, on.SummaryStatus);
        if (!on.Survived)
        {
            return;
        }

        // The launcher is still running, so the file must not go on saying it was signalled: the record was written, and it
        // goes by itself (the worker was silent, so only the timer wrote) to the live phase and no end.
        Assert.True(on.SawRecord, "the signal was never recorded, so the test proves nothing");
        TimeSpan gone = Assert.NotNull(on.RecordGoneAfter);
        Assert.InRange(gone, TimeSpan.Zero, _clears);
        Assert.Equal("worker", on.PhaseWhenGone);
        Assert.NotEqual("launcher-signalled", on.FinalEnd, StringComparer.Ordinal);
        Assert.Equal(on.SummaryStatus, on.FinalEnd);
    }
}
