using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The launcher as a real process that inherited a signal as ignored (docs/research/live-status/README.md, section 5,
/// issue #146 fix round 1, point 1): the launcher is started by a shell that ran <c>trap '' HUP</c> (or TERM), gets the
/// signal while its worker waits, and the run then goes on to its end. Each case runs twice, with the writer on and with
/// LEAN_WORKER_STATUS=off, and the two ends are compared: exit code, printed block and summary. Every process and
/// process group this class starts is ended by it, under hard time limits.
/// </summary>
[Collection("launcher-process-state")]
public sealed class LauncherIgnoredSignalProcessTests : IAsyncDisposable
{
    private const string UsageLine = """printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'""";

    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _window = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// One launch with an ignored signal sent to it: what the file and the processes showed in the window after the
    /// signal, after the next status write, and how the run ended.
    /// </summary>
    private sealed record Arm(
        int ExitCode,
        string Block,
        string Summary,
        string SummaryStatus,
        bool SurvivedWindow,
        bool WorkerAliveInWindow,
        string LauncherSigIgn,
        string WorkerSigIgn,
        string[] EndsInWindow,
        string? EndAfterNextWrite,
        string? FinalEnd);

    // The launcher under a shell that ignores the signal: the ignore is inherited through exec, as nohup's is.
    private static ProcessStartInfo Ignoring(ProcessStartInfo inner, string signal)
    {
        ProcessStartInfo outer = new("sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = inner.WorkingDirectory,
        };
        outer.ArgumentList.Add("-c");
        outer.ArgumentList.Add("trap '' " + signal + "; exec \"$@\"");
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

    private async Task<Arm> RunArmAsync(string signal, bool statusOn)
    {
        string root = _dirs.NewRoot();
        string control = _dirs.Create("lw-ign-ctl");
        string marker = Path.Combine(control, "worker.pid");
        string first = Path.Combine(control, "release1");
        string second = Path.Combine(control, "release2");
        string body = "cat >/dev/null\necho $$ > '" + marker + ".tmp'\nmv '" + marker + ".tmp' '" + marker + "'\n"
            + "waitfor() { i=0; while [ ! -f \"$1\" ] && [ $i -lt 1200 ]; do sleep 0.1; i=$((i+1)); done; }\n"
            + "waitfor '" + first + "'\n" + UsageLine + "\nwaitfor '" + second + "'\n" + RunAsyncGolden.SuccessStream + "\n";
        string stub = await LauncherProcess.StubAsync(_dirs, "claude", body);
        ProcessStartInfo inner = LauncherProcess.StartInfo(
            ["--task", Path.Combine(root, "task.md"), "--runs-root", root, "--model", "anthropic/claude-haiku-4-5", "--mode", "bare", "--name", "ign"],
            _dirs.Create("lw-ign-cwd"), _dirs.Create("lw-ign-state"), ("PATH", stub + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")),
            ("ANTHROPIC_API_KEY", "dummy-test-key"), (RunStatus.KillSwitch, statusOn ? null : "off"));
        Process launcher = Process.Start(Ignoring(inner, signal))!;
        _launchers.Add(launcher);
        Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = launcher.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        int worker = await WaitForWorkerAsync(marker, root, statusOn);
        _workerPids.Add(worker);
        string launcherSigIgn = SigIgn(launcher.Id);
        string workerSigIgn = SigIgn(worker);
        await LauncherProcess.SignalAsync(signal, launcher.Id);

        // The window after the signal: the worker is silent, so no status write follows the signal by itself.
        List<string> ends = [];
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < _window)
        {
            ends.Add(statusOn ? EndOf(root) ?? "-" : "-");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        bool survived = !launcher.HasExited;
        bool workerAlive = Alive(worker);

        // The worker speaks: a stream line that changes a displayed field makes the next status write.
        await File.WriteAllTextAsync(first, "go", TestContext.Current.CancellationToken);
        string? afterNextWrite = survived && statusOn ? await EndAfterNextWriteAsync(root) : null;
        await File.WriteAllTextAsync(second, "go", TestContext.Current.CancellationToken);
        await launcher.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);
        string output = await stdout.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        _ = await stderr.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        // A launcher that did not survive the signal has no summary: the arm says "none" then.
        string runDir = Directory.GetDirectories(Path.Combine(root, "runs")).Single();
        JsonObject? summary = File.Exists(Path.Combine(runDir, "summary.json")) ? RunAsyncGolden.Summary(runDir) : null;
        return new Arm(launcher.ExitCode, LaunchCompare.Block(root, output), summary is null ? string.Empty : LaunchCompare.Comparable(root, summary),
            summary?["status"]?.GetValue<string>() ?? "none", survived, workerAlive, launcherSigIgn, workerSigIgn, [.. ends], afterNextWrite, statusOn ? EndOf(root) : null);
    }

    // After the worker's next line: the end the file shows once the launcher wrote again (null: none), waited for at most 10 s.
    private static async Task<string?> EndAfterNextWriteAsync(string root)
    {
        for (int i = 0; i < 200 && WaitingForTheWrite(root); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return EndOf(root);
    }

    // The launcher has not yet written the worker's line (no call counted) or its file still has an end.
    private static bool WaitingForTheWrite(string root) => EndOf(root) is not null || ApiCalls(root) is 0;

    private static async Task<int> WaitForWorkerAsync(string marker, string root, bool statusOn)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < _limit)
        {
            if (File.Exists(marker) && (!statusOn || (StatusOf(root) is { } doc && doc["phase"]!.GetValue<string>() is "worker")))
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

        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "status.json")))!.AsObject();
    }

    // The status of the end the file shows now; null while it has none.
    private static string? EndOf(string root) => StatusOf(root)?["end"]?["status"]?.GetValue<string>();

    private static int ApiCalls(string root) => StatusOf(root)?["live"]?["api_calls"]?.GetValue<int>() ?? 0;

    private static string SigIgn(int pid)
    {
        string line = File.ReadAllLines("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/status").First(l => l.StartsWith("SigIgn:", StringComparison.Ordinal));
        return line["SigIgn:".Length..].Trim();
    }

    private static bool Ignores(string sigIgn, int signal) =>
        (ulong.Parse(sigIgn, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) & (1UL << (signal - 1))) != 0;

    private static bool Alive(int pid)
    {
        string stat = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        return File.Exists(stat) && !File.ReadAllText(stat).Contains(") Z", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launcher_that_inherited_SIGHUP_as_ignored_keeps_running_records_nothing_and_ends_as_with_the_writer_offAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX signals and a POSIX shell");

        Arm off = await RunArmAsync("HUP", statusOn: false);
        Arm on = await RunArmAsync("HUP", statusOn: true);

        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"ignored-signal HUP: writer off: survived {off.SurvivedWindow}, exit {off.ExitCode}; writer on: survived {on.SurvivedWindow}, exit {on.ExitCode}, ends seen in the window {string.Join(',', on.EndsInWindow.Distinct(StringComparer.Ordinal))}"));
        // The ignore came through: the launcher and (as on master) its worker still ignore SIGHUP, in both arms.
        Assert.All(new[] { off, on }, a => Assert.True(Ignores(a.LauncherSigIgn, 1), "launcher SigIgn " + a.LauncherSigIgn));
        Assert.All(new[] { off, on }, a => Assert.True(Ignores(a.WorkerSigIgn, 1), "worker SigIgn " + a.WorkerSigIgn));
        Assert.Equal(off.WorkerSigIgn, on.WorkerSigIgn);
        // The launcher and the worker keep running.
        Assert.All(new[] { off, on }, a => Assert.True(a.SurvivedWindow));
        Assert.All(new[] { off, on }, a => Assert.True(a.WorkerAliveInWindow));
        // The file never says launcher-signalled, not for an instant, and never has an end before the real one.
        Assert.DoesNotContain("launcher-signalled", on.EndsInWindow, StringComparer.Ordinal);
        Assert.All(on.EndsInWindow, e => Assert.Equal("-", e));
        Assert.Null(on.EndAfterNextWrite);
        // The run ends as it does with the writer off.
        Assert.Equal(0, off.ExitCode);
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Equal(off.Block, on.Block);
        Assert.Equal(off.Summary, on.Summary);
        Assert.Equal(off.SummaryStatus, on.SummaryStatus);
        Assert.Equal(on.SummaryStatus, on.FinalEnd);
    }

    [Fact]
    public async Task A_launcher_that_inherited_SIGTERM_as_ignored_ends_as_with_the_writer_off_and_leaves_no_lasting_signal_recordAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX signals and a POSIX shell");

        Arm off = await RunArmAsync("TERM", statusOn: false);
        Arm on = await RunArmAsync("TERM", statusOn: true);

        string seen = string.Join(',', on.EndsInWindow.Distinct(StringComparer.Ordinal));
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"ignored-signal TERM: writer off: survived {off.SurvivedWindow}, exit {off.ExitCode}, launcher SigIgn {off.LauncherSigIgn}, worker SigIgn {off.WorkerSigIgn}"));
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"ignored-signal TERM: writer on: survived {on.SurvivedWindow}, exit {on.ExitCode}, launcher SigIgn {on.LauncherSigIgn}, worker SigIgn {on.WorkerSigIgn}, ends seen while the worker was silent [{seen}], after the next write {on.EndAfterNextWrite ?? "none"}"));
        // The same outcome in both arms: the launcher survives or not alike, the worker's dispositions are the same.
        Assert.Equal(off.SurvivedWindow, on.SurvivedWindow);
        Assert.Equal(off.WorkerAliveInWindow, on.WorkerAliveInWindow);
        Assert.Equal(off.WorkerSigIgn, on.WorkerSigIgn);
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Equal(off.Block, on.Block);
        Assert.Equal(off.Summary, on.Summary);
        Assert.Equal(off.SummaryStatus, on.SummaryStatus);
        if (!on.SurvivedWindow)
        {
            return;
        }

        // A run that goes on has no signal record once the launcher writes again, and its end is the normal one.
        Assert.Null(on.EndAfterNextWrite);
        Assert.Equal(on.SummaryStatus, on.FinalEnd);
    }
}
