using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The launcher as a real process on a stub worker that sleeps, killed with SIGTERM and SIGHUP (docs/research/
/// live-status/README.md, section 5; slice 5): the process ends the way it does with LEAN_WORKER_STATUS=off (the
/// handler only records, the default action proceeds), the status file says launcher-signalled, and the reader shows
/// the run as orphaned while the worker's group lives and as failed (signalled) after. Every process and process
/// group this test starts is ended by it, under hard time limits.
/// </summary>
[Collection("launcher-process-state")]
public sealed class LauncherSignalProcessTests : IAsyncDisposable
{
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(60);

    private readonly TempDirs _dirs = new();
    private readonly List<int> _workerPids = [];
    private readonly List<Process> _launchers = [];

    public async ValueTask DisposeAsync()
    {
        // The worker of a launcher that was killed is not its child any more: its group is ended here.
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
    /// One launcher run to the point of its death by a signal.
    /// </summary>
    private sealed record Arm(int ExitCode, string RunDir, int WorkerPid, string Stdout, string Stderr);

    private async Task<Arm> RunUntilSignalledAsync(string signal, bool statusOn)
    {
        string root = _dirs.NewRoot();
        string state = _dirs.Create("lw-sig-state");
        string marker = Path.Combine(_dirs.Create("lw-sig-marker"), "worker.pid");
        string stub = await LauncherProcess.StubAsync(_dirs, "claude", "cat >/dev/null\necho $$ > '" + marker + ".tmp'\nmv '" + marker + ".tmp' '" + marker + "'\nexec sleep 300\n");
        string path = stub + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        ProcessStartInfo psi = LauncherProcess.StartInfo(
            ["--task", Path.Combine(root, "task.md"), "--runs-root", root, "--model", "anthropic/claude-haiku-4-5", "--mode", "bare", "--name", "sig"],
            _dirs.Create("lw-sig-cwd"), state, ("PATH", path), ("ANTHROPIC_API_KEY", "dummy-test-key"), (RunStatus.KillSwitch, statusOn ? null : "off"));
        Process launcher = Process.Start(psi)!;
        _launchers.Add(launcher);
        Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = launcher.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        int worker = await WaitForWorkerAsync(marker, root, statusOn);
        _workerPids.Add(worker);
        await LauncherProcess.SignalAsync(signal, launcher.Id);
        await launcher.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);
        string runDir = Directory.GetDirectories(Path.Combine(root, "runs")).Single();
        return new Arm(launcher.ExitCode, runDir, worker, await stdout.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken),
            await stderr.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken));
    }

    // The worker is running: its stub wrote its pid and, when the writer is on, the status file shows the worker phase.
    private static async Task<int> WaitForWorkerAsync(string marker, string root, bool statusOn)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < _limit)
        {
            if (File.Exists(marker) && (!statusOn || WorkerPhaseWritten(root)))
            {
                return int.Parse((await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Trim(), CultureInfo.InvariantCulture);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the worker did not start within " + _limit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        return 0;
    }

    private static bool WorkerPhaseWritten(string root)
    {
        string runs = Path.Combine(root, "runs");
        if (!Directory.Exists(runs) || Directory.GetDirectories(runs).FirstOrDefault() is not { } dir || !File.Exists(Path.Combine(dir, "status.json")))
        {
            return false;
        }

        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "status.json")))!["phase"]!.GetValue<string>() is "worker";
    }

    private static StatusRow ReadNow(string runDir) => StatusReader.Host().Read(runDir);

    [Theory]
    [InlineData("TERM", "SIGTERM")]
    [InlineData("HUP", "SIGHUP")]
    public async Task A_signalled_launcher_ends_as_it_does_with_the_writer_off_and_the_status_file_and_the_reader_say_soAsync(string signal, string name)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX signals and a POSIX shell");

        Arm off = await RunUntilSignalledAsync(signal, statusOn: false);
        Arm on = await RunUntilSignalledAsync(signal, statusOn: true);

        // 1. The default action proceeds exactly as without the handler: the same ending.
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"signal-test {signal}: exit code with the writer off {off.ExitCode}, on {on.ExitCode}"));
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.NotEqual(0, on.ExitCode);
        Assert.False(File.Exists(Path.Combine(off.RunDir, "status.json")));

        // 2. The status file recorded the signal before the launcher died.
        JsonObject status = StatusFileAssert.Read(on.RunDir);
        Assert.Equal("failed", status["phase"]!.GetValue<string>());
        Assert.Equal("launcher-signalled", status["end"]!["status"]!.GetValue<string>());
        Assert.Equal(name, status["end"]!["signal"]!.GetValue<string>());
        Assert.Equal(on.WorkerPid, status["worker"]!["pid"]!.GetValue<int>());

        // The worker was left running (issue #121 is about that): the reader says orphaned while its group lives.
        StatusRow orphaned = ReadNow(on.RunDir);
        Assert.Equal("orphaned", orphaned.State);
        Assert.Contains("signalled (" + name + ")", orphaned.Detail, StringComparison.Ordinal);

        // After the worker is gone the run is a failed run that was signalled.
        await LauncherProcess.SignalAsync("KILL", -on.WorkerPid);
        await LauncherProcess.SignalAsync("KILL", on.WorkerPid);
        StatusRow failed = await AfterTheWorkerEndsAsync(on.RunDir, on.WorkerPid);
        Assert.Equal("failed", failed.State);
        Assert.Equal("(signalled, " + name + ")", failed.Detail);
    }

    // Whether the process is there and not a zombie (the reader does not count a zombie either).
    private static bool Alive(int pid)
    {
        string stat = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        return File.Exists(stat) && !File.ReadAllText(stat).Contains(") Z", StringComparison.Ordinal);
    }

    // The row once the worker is gone (the reader says orphaned until then), after waiting for that for at most 20 s.
    private static async Task<StatusRow> AfterTheWorkerEndsAsync(string runDir, int worker)
    {
        for (int i = 0; i < 200 && Alive(worker); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return ReadNow(runDir);
    }
}
