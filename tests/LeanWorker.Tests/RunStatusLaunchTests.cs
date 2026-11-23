using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// status.json in a whole launch (Launcher.RunAsync against a stub worker): what the finished file
/// records next to the summary, the kill switch, that stdout, summary.json and the ledger row are the
/// same with the writer on and off, and the failure path after the summary. Process-global state
/// (PATH, the kill switch, the working directory) is why this class sits in the launcher collection.
/// </summary>
[Collection("launcher-process-state")]
public sealed class RunStatusLaunchTests : IDisposable
{
    private const string UsageStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":3,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0},"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{}}]}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":2,"permission_denials":[],"usage":{"output_tokens_details":{"thinking_tokens":7}}}'
        """;

    private const string FirstCallLine = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        """;

    /// <summary>
    /// A worker that reports its first call and then blocks opening the FIFO until the test opens the other end,
    /// so the worker phase stays on screen exactly as long as the test wants it, not for a number of seconds.
    /// </summary>
    private static string HeldStream(string fifo) => FirstCallLine + "\nread _ < '" + fifo + "' || true\n";

    private static readonly string[] _withoutStatus = ["command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md"];

    private readonly TempDirs _dirs = new();
    private readonly string? _oldKillSwitch = Environment.GetEnvironmentVariable(RunStatus.KillSwitch);

    // No owner variable of the machine's own session reaches a launch of this class.
    private readonly EnvScope _env = EnvScope.Clean();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, _oldKillSwitch);
        _env.Dispose();
        _dirs.Dispose();
    }

    private async Task<(int Code, string Stdout, string Root)> RunAsync(string stream, string? killSwitch)
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, killSwitch);
        string root = _dirs.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, stream);
        return (code, stdout, root);
    }

    [Fact]
    public async Task A_finished_run_leaves_a_done_status_that_agrees_with_its_summaryAsync()
    {
        (int code, string stdout, _) = await RunAsync(UsageStream, killSwitch: null);

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject summary = RunAsyncGolden.Summary(runDir);
        JsonObject status = StatusFileAssert.Read(runDir);
        Assert.Equal(0, code);
        Assert.Equal("done", status["phase"]!.GetValue<string>());
        Assert.True(status["seq"]!.GetValue<long>() >= 4);
        JsonObject end = status["end"]!.AsObject();
        Assert.Equal(summary["status"]!.GetValue<string>(), end["status"]!.GetValue<string>());
        Assert.Equal(code, end["exit_code"]!.GetValue<int>());
        Assert.Equal("summary.json", end["summary"]!.GetValue<string>());
        Assert.Equal(runDir, status["run"]!["dir"]!.GetValue<string>());
        Assert.Equal("golden", status["run"]!["name"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", status["spec"]!["model"]!.GetValue<string>());
        Assert.Equal(Environment.ProcessId, status["launcher"]!["pid"]!.GetValue<int>());
        Assert.True(status["worker"]!["pid"]!.GetValue<int>() > 0);
        // Train 2: the owner is captured at launch; with no owner variable and no --owner it is the "none" owner.
        JsonObject owner = status["owner"]!.AsObject();
        Assert.Equal("none", owner["source"]!.GetValue<string>());
        Assert.Empty(owner["keys"]!.AsArray());
        Assert.Null(status["run"]!["parent_run"]);
    }

    [Fact]
    public async Task The_final_counters_are_the_summarys_counters_and_the_last_tool_is_the_streamsAsync()
    {
        (_, string stdout, _) = await RunAsync(UsageStream, killSwitch: null);

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject summary = RunAsyncGolden.Summary(runDir);
        JsonObject live = StatusFileAssert.Read(runDir)["live"]!.AsObject();
        Assert.Equal(summary["api_calls"]!.GetValue<int>(), live["api_calls"]!.GetValue<int>());
        Assert.Equal(summary["num_turns"]!.GetValue<long>(), live["turns"]!.GetValue<long>());
        Assert.True(JsonNode.DeepEquals(summary["tokens"], live["tokens"]), live["tokens"]!.ToJsonString());
        Assert.True(Math.Abs(summary["total_cost_usd"]!.GetValue<decimal>() - live["cost_usd"]!.GetValue<decimal>()) < 0.0001m);
        Assert.Equal("Bash", live["last_tool"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task While_the_worker_runs_the_file_shows_the_worker_phase_its_pid_and_the_first_callAsync()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, value: null);
        string root = _dirs.NewRoot();
        string fifo = Path.Combine(_dirs.Create("lw-hold"), "release");
        await Fifo.MakeAsync(fifo);
        Task<(int Code, string Stdout)> run = RunAsyncGolden.RunAsync(root, HeldStream(fifo));
        try
        {
            // The worker cannot end before the FIFO is opened for writing, which only the finally below does.
            JsonObject? seen = null;
            for (int i = 0; i < 600 && seen is null && !run.IsCompleted; i++)
            {
                seen = ReadWhenFirstCallIsIn(root);
                if (seen is null)
                {
                    _ = await Task.WhenAny(run, Task.Delay(50, TestContext.Current.CancellationToken));
                }
            }

            Assert.NotNull(seen);
            Assert.Equal("worker", seen["phase"]!.GetValue<string>());
            Assert.Null(seen["end"]);
            Assert.True(seen["worker"]!["pid"]!.GetValue<int>() > 0);
            Assert.Equal(Environment.ProcessId, seen["launcher"]!["pid"]!.GetValue<int>());
            Assert.True(seen["live"]!["cost_usd"]!.GetValue<decimal>() > 0m);
        }
        finally
        {
            // Releases the held worker whether the assertions passed or not: the write end opens (the worker is
            // waiting on the read end) and closes at once.
            await using FileStream? release = await Fifo.WaitForReaderAsync(fifo, TimeSpan.FromSeconds(30));
        }

        (_, string stdout) = await run.WaitAsync(TimeSpan.FromSeconds(60), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal("done", StatusFileAssert.Read(RunAsyncGolden.RunDirFrom(stdout))["phase"]!.GetValue<string>());
    }

    /// <summary>
    /// The status document of the run in the runs root once it shows a call; null before. A read
    /// that fails to parse is not caught: the file is replaced atomically, so it must always parse.
    /// </summary>
    private static JsonObject? ReadWhenFirstCallIsIn(string root)
    {
        string runs = Path.Combine(root, "runs");
        string? path = Directory.Exists(runs) ? Directory.GetDirectories(runs).Select(d => Path.Combine(d, "status.json")).FirstOrDefault(p => File.Exists(p)) : null;
        if (path is null)
        {
            return null;
        }

        JsonObject doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return doc["live"]!["api_calls"]!.GetValue<int>() >= 1 ? doc : null;
    }

    [Fact]
    public async Task The_kill_switch_off_leaves_the_old_file_list_and_no_status_fileAsync()
    {
        (int code, string stdout, _) = await RunAsync(UsageStream, killSwitch: "off");

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        Assert.Equal(0, code);
        RunAsyncGolden.AssertFiles(runDir, _withoutStatus);
    }

    [Theory]
    [InlineData(RunAsyncGolden.SuccessStream)]
    [InlineData(RunAsyncGolden.ErrorStream)]
    [InlineData(UsageStream)]
    public async Task Stdout_summary_and_the_ledger_row_are_the_same_with_the_writer_on_and_offAsync(string stream)
    {
        (int onCode, string onOut, string onRoot) = await RunAsync(stream, killSwitch: null);
        (int offCode, string offOut, string offRoot) = await RunAsync(stream, killSwitch: "off");

        Assert.Equal(offCode, onCode);
        Assert.Equal(RunAsyncGolden.Normalize(offOut, offRoot), RunAsyncGolden.Normalize(onOut, onRoot));
        string onDir = RunAsyncGolden.RunDirFrom(onOut);
        string offDir = RunAsyncGolden.RunDirFrom(offOut);
        Assert.Equal(Comparable(offRoot, RunAsyncGolden.Summary(offDir)), Comparable(onRoot, RunAsyncGolden.Summary(onDir)));
        Assert.Equal(Comparable(offRoot, LedgerRow(offRoot)), Comparable(onRoot, LedgerRow(onRoot)));
        string[] withStatus = [.. _withoutStatus.Append("status.json").Order(StringComparer.Ordinal)];
        Assert.Equal(withStatus, FileNames(onDir));
        Assert.Equal(_withoutStatus, FileNames(offDir));
    }

    [Fact]
    public async Task A_failure_after_the_summary_leaves_a_failed_status_that_says_so_and_never_a_done_oneAsync()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, value: null);
        string root = _dirs.NewRoot();
        // The ledger append comes after summary.json and report.md; a directory in its place makes it throw.
        _ = Directory.CreateDirectory(Path.Combine(root, "runs.jsonl"));

        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsyncGolden.RunAsync(root, UsageStream));

        string runDir = Directory.GetDirectories(Path.Combine(root, "runs")).Single();
        JsonObject summary = RunAsyncGolden.Summary(runDir);
        JsonObject status = StatusFileAssert.Read(runDir);
        Assert.Equal("failed", status["phase"]!.GetValue<string>());
        JsonObject end = status["end"]!.AsObject();
        Assert.Equal("launch-failed", end["status"]!.GetValue<string>());
        Assert.True(end["after_summary"]!.GetValue<bool>());
        Assert.Equal(summary["status"]!.GetValue<string>(), end["summary_status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(end["error"]!.GetValue<string>()));
    }

    [Fact]
    public void Open_writes_the_starting_phase_when_the_kill_switch_is_not_set()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, value: null);
        string runDir = _dirs.Create("lw-open");
        using StateDirScope state = new();

        RunStatus? status = Open(runDir);

        Assert.NotNull(status);
        JsonObject doc = StatusFileAssert.Read(runDir);
        Assert.Equal("starting", doc["phase"]!.GetValue<string>());
        Assert.Equal(1, doc["seq"]!.GetValue<long>());
        Assert.Equal(Environment.ProcessId, doc["launcher"]!["pid"]!.GetValue<int>());
        // Open registers the signal handler; the run's last write unregisters it.
        status.Finish("success", 0);
    }

    [Fact]
    public void Open_returns_nothing_and_writes_nothing_when_the_kill_switch_is_off()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, "off");
        string runDir = _dirs.Create("lw-open");
        using StateDirScope state = new();

        RunStatus? status = Open(runDir);

        Assert.Null(status);
        Assert.Empty(Directory.GetFileSystemEntries(runDir));
    }

    private static RunStatus? Open(string runDir) =>
        RunStatus.Open(runDir, "job", "chain", 1, profile: null, "claude", "anthropic", "claude-haiku-4-5", 2m, wrapUpUsd: null, 60, DateTimeOffset.UtcNow, worktree: null);

    private static string[] FileNames(string runDir) =>
        [.. Directory.GetFiles(runDir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    private static JsonObject LedgerRow(string root) =>
        JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(root, "runs.jsonl"))))!.AsObject();

    /// <summary>
    /// A summary or ledger row without what differs between any two runs (the time and the duration),
    /// its paths and durations normalised the way the golden tests do.
    /// </summary>
    private static string Comparable(string root, JsonObject row)
    {
        JsonObject copy = row.DeepClone().AsObject();
        _ = copy.Remove("timestamp");
        _ = copy.Remove("duration_ms");
        copy["evidence_lines"] = new JsonArray([.. copy["evidence_lines"]!.AsArray().Select(l => (JsonNode)RunAsyncGolden.Normalize(l!.GetValue<string>(), root))]);
        return RunAsyncGolden.Normalize(copy.ToJsonString(), root);
    }
}
