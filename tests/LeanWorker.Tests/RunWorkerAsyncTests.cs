using Xunit;

namespace LeanWorker.Tests;

[Collection("launcher-process-state")]
public class RunWorkerAsyncTests
{
    private static Prepared ShellPrepared(string script) =>
        new("/bin/sh", ["-c", script], [], "sh", ScratchDirectory: null);

    private static (string StreamPath, string StderrPath) Paths()
    {
        string dir = Directory.CreateTempSubdirectory("lw-runworker").FullName;
        return (Path.Combine(dir, "stream.jsonl"), Path.Combine(dir, "stderr.log"));
    }

    [Fact]
    public async Task Cap_hit_on_the_first_line_stops_the_worker_well_before_the_sleepAsync()
    {
        (string streamPath, string stderrPath) = Paths();
        Prepared prep = ShellPrepared("echo a; sleep 30");

        DateTime start = DateTime.UtcNow;
        (int exitCode, bool timedOut, bool capKilled) = await Launcher.RunWorkerAsync(
            prep, stdin: string.Empty, streamPath, stderrPath, timeoutMinutes: 5,
            record: _ => true, onLine: _ => true);
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.True(capKilled);
        Assert.False(timedOut);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
    }

    [Fact]
    public async Task Expected_metering_failure_fails_closed_and_stops_the_workerAsync()
    {
        (string streamPath, string stderrPath) = Paths();
        Prepared prep = ShellPrepared("echo a; sleep 30");

        (int exitCode, bool timedOut, bool capKilled) = await Launcher.RunWorkerAsync(
            prep, stdin: string.Empty, streamPath, stderrPath, timeoutMinutes: 5,
            record: _ => throw new IOException("disk full"), onLine: _ => false);

        Assert.True(capKilled);
        string launcherLog = await File.ReadAllTextAsync(stderrPath + ".launcher", TestContext.Current.CancellationToken);
        Assert.Contains("metering failed, worker stopped", launcherLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_failure_propagates_and_kills_the_workerAsync()
    {
        (string streamPath, string stderrPath) = Paths();
        Prepared prep = ShellPrepared("echo a; sleep 30");

        DateTime start = DateTime.UtcNow;
        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(async () => await Launcher.RunWorkerAsync(
            prep, stdin: string.Empty, streamPath, stderrPath, timeoutMinutes: 5,
            record: _ => true, onLine: _ => throw new NotSupportedException("boom")));
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.Equal("boom", ex.Message);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
    }

    [Fact]
    public async Task Lines_before_the_stop_are_still_recorded_to_the_stream_fileAsync()
    {
        (string streamPath, string stderrPath) = Paths();
        Prepared prep = ShellPrepared("printf 'a\\nb\\nc\\n'; sleep 30");

        (int exitCode, bool timedOut, bool capKilled) = await Launcher.RunWorkerAsync(
            prep, stdin: string.Empty, streamPath, stderrPath, timeoutMinutes: 5,
            record: _ => true, onLine: line => line is "a");

        Assert.True(capKilled);
        string[] lines = await File.ReadAllLinesAsync(streamPath, TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b", "c"], lines);
    }
}
