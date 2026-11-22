using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A status problem never touches the run, through the real launch path (Launcher.RunAsync against a
/// stub worker): a stream with hostile lines, a status file that cannot be written and token counts
/// that overflow end with the same exit code, stdout, summary.json and ledger row as with
/// LEAN_WORKER_STATUS=off, and the worker is not stopped because of the status path. At the budget
/// the stop happens before the status write of the line that reached it. Process-global state (PATH,
/// the kill switch, the working directory) is why this class sits in the launcher collection.
/// </summary>
[Collection("launcher-process-state")]
public sealed class RunStatusHostileLaunchTests : IDisposable
{
    private const string Start = """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}""";

    private const string Result = """{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":2,"permission_denials":[],"usage":{"output_tokens_details":{"thinking_tokens":7}}}""";

    private static readonly string[] _hostile =
    [
        """{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":3,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0},"content":[{"type":"tool_use","id":"toolu_\udc00","name":"Ba\ud800sh","input":{}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"\ud800","is_error":true}]}}""",
        """{"type":"assistant","message":[1,2,3]}""",
        """{"type":"assistant","message":{"content":[null,5,"s",[],{"type":5},{"type":"tool_use","name":[1],"id":{}}]}}""",
        """{"type":"assistant","message":{"content":{"a":1}}}""",
        """{"type":"system","subtype":["api_retry"]}""",
    ];

    private readonly TempDirs _dirs = new();
    private readonly string? _oldKillSwitch = Environment.GetEnvironmentVariable(RunStatus.KillSwitch);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, _oldKillSwitch);
        _dirs.Dispose();
    }

    private static string Script(params string[] lines) =>
        string.Join('\n', lines.Select(l => "printf '%s\\n' '" + l + "'")) + "\n";

    /// <summary>
    /// A worker that puts a directory in the place of status.json (once the launcher has written
    /// it), so every later status write fails, and then streams a normal run.
    /// </summary>
    private static string BreakStatus(string root)
    {
        return "for d in '" + root + "'/runs/*/; do until [ -d \"${d}status.json\" ]; do rm -f \"${d}status.json\"; mkdir \"${d}status.json\" 2>/dev/null; sleep 0.05; done; done\n"
            + Script(Start, Result);
    }

    /// <summary>
    /// What a launch ended with: its exit code and normalised stdout, or the type of the exception it threw.
    /// </summary>
    private sealed record Ended(string Result, string Root, string? Stdout, int StatusNotes);

    /// <summary>
    /// The one note the writer leaves when a status update failed (it is part of the summary and of the
    /// printed result block, as a "note:" line): the only thing allowed to differ from a run without status.
    /// </summary>
    private const string StatusNote = "status.json could not be";

    private static bool IsStatusNote(string line) => line.Contains(StatusNote, StringComparison.Ordinal);

    private async Task<Ended> RunAsync(Func<string, string> script, string? killSwitch, Action<Options>? configure = null)
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, killSwitch);
        string root = _dirs.NewRoot();
        (int Code, string Stdout) ended = (0, string.Empty);
        Exception? threw = await Record.ExceptionAsync(async () => ended = await RunAsyncGolden.RunAsync(root, script(root), configure));
        if (threw is not null)
        {
            return new Ended("threw " + threw.GetType().Name, root, Stdout: null, StatusNotes: 0);
        }

        string[] lines = RunAsyncGolden.Normalize(ended.Stdout, root).Split('\n');
        return new Ended(ended.Code.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join('\n', lines.Where(l => !IsStatusNote(l))), root, ended.Stdout, lines.Count(l => IsStatusNote(l)));
    }

    private static JsonObject LedgerRow(string root) =>
        JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(root, "runs.jsonl"))))!.AsObject();

    private static JsonArray Without(JsonNode? array, string root) =>
        new([.. array!.AsArray().Select(l => l!.GetValue<string>()).Where(l => !IsStatusNote(l)).Select(l => (JsonNode)RunAsyncGolden.Normalize(l, root))]);

    private static string Comparable(string root, JsonObject row)
    {
        JsonObject copy = row.DeepClone().AsObject();
        _ = copy.Remove("timestamp");
        _ = copy.Remove("duration_ms");
        copy["evidence_lines"] = Without(copy["evidence_lines"], root);
        copy["notes"] = Without(copy["notes"], root);
        return RunAsyncGolden.Normalize(copy.ToJsonString(), root);
    }

    /// <summary>
    /// The two launches agree on everything the run is: exit code, stdout, summary.json and the ledger row.
    /// </summary>
    private static void AssertSameRun(Ended on, Ended off)
    {
        Assert.Equal(off.Result, on.Result);
        if (on.Stdout is null)
        {
            return;
        }

        string onDir = RunAsyncGolden.RunDirFrom(on.Stdout);
        string offDir = RunAsyncGolden.RunDirFrom(off.Stdout!);
        Assert.Equal(Comparable(off.Root, RunAsyncGolden.Summary(offDir)), Comparable(on.Root, RunAsyncGolden.Summary(onDir)));
        Assert.Equal(Comparable(off.Root, LedgerRow(off.Root)), Comparable(on.Root, LedgerRow(on.Root)));
    }

    [Fact]
    public async Task A_worker_stream_with_hostile_lines_ends_like_the_same_run_with_the_writer_off_and_the_worker_is_not_stoppedAsync()
    {
        string stream = Script([Start, .. _hostile, Result]);

        Ended on = await RunAsync(_ => stream, killSwitch: null);
        Ended off = await RunAsync(_ => stream, killSwitch: "off");

        AssertSameRun(on, off);
        Assert.Equal(1, on.StatusNotes);
        Assert.Equal(0, off.StatusNotes);
        JsonObject summary = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(on.Stdout!));
        Assert.Equal("success", summary["status"]!.GetValue<string>());
        Assert.Equal(0, summary["exit_code"]!.GetValue<int>());
        Assert.StartsWith("0\n", on.Result, StringComparison.Ordinal);
        Assert.Contains("DONE", await File.ReadAllTextAsync(Path.Combine(RunAsyncGolden.RunDirFrom(on.Stdout!), "report.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(summary["status"]!.GetValue<string>(), StatusFileAssert.Read(RunAsyncGolden.RunDirFrom(on.Stdout!))["end"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_status_file_that_cannot_be_written_leaves_the_run_as_it_is_with_the_writer_offAsync()
    {
        Ended on = await RunAsync(root => BreakStatus(root), killSwitch: null);
        Ended off = await RunAsync(root => BreakStatus(root), killSwitch: "off");

        AssertSameRun(on, off);
        Assert.Equal(1, on.StatusNotes);
        Assert.Equal(0, off.StatusNotes);
        string runDir = RunAsyncGolden.RunDirFrom(on.Stdout!);
        Assert.True(Directory.Exists(Path.Combine(runDir, "status.json")));
        Assert.Equal("success", RunAsyncGolden.Summary(runDir)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Token_counts_whose_sum_overflows_end_the_run_the_way_they_do_with_the_writer_offAsync()
    {
        const string Big = """{"type":"stream_event","event":{"type":"message_start","message":{"id":"IDX","model":"claude-haiku-4-5","usage":{"input_tokens":9223372036854775807,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}""";
        string stream = Script(Big.Replace("IDX", "m1", StringComparison.Ordinal), Big.Replace("IDX", "m2", StringComparison.Ordinal), Result);

        Ended on = await RunAsync(_ => stream, killSwitch: null, o => o.MaxBudgetUsd = 1_000_000_000_000m);
        Ended off = await RunAsync(_ => stream, killSwitch: "off", o => o.MaxBudgetUsd = 1_000_000_000_000m);

        AssertSameRun(on, off);
    }

    [Fact]
    public async Task At_the_budget_the_worker_is_stopped_before_the_status_write_of_the_line_that_reached_itAsync()
    {
        Environment.SetEnvironmentVariable(RunStatus.KillSwitch, value: null);
        string root = _dirs.NewRoot();
        // The worker leaves a process behind that ignores SIGTERM and has left the tree (so the stop's
        // tree kill misses it): the launcher then waits about five seconds for it before it writes the
        // finishing phase, and in that window the file shows what was written before the stop and
        // nothing after it. The usage line reaches the budget, so the stop comes at once.
        string script = "( trap '' TERM; sleep 8 & )\n"
            + Script("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}""")
            + "sleep 30\n";
        Task<(int Code, string Stdout)> run = RunAsyncGolden.RunAsync(root, script, o => o.MaxBudgetUsd = 0.01m);

        int mostCalls = -1;
        long workerPhaseSince = 0;
        TimeSpan workerPhase = TimeSpan.Zero;
        while (!run.IsCompleted)
        {
            if (ReadStatus(root) is { } doc && doc["phase"]!.GetValue<string>() is "worker" && doc["worker"] is JsonObject)
            {
                mostCalls = Math.Max(mostCalls, doc["live"]!["api_calls"]!.GetValue<int>());
                workerPhaseSince = workerPhaseSince is 0 ? TimeProvider.System.GetTimestamp() : workerPhaseSince;
                workerPhase = TimeProvider.System.GetElapsedTime(workerPhaseSince);
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        (int code, string stdout) = await run;

        Assert.Equal(0, mostCalls);
        Assert.True(workerPhase > TimeSpan.FromSeconds(2), "the worker phase was not on view long enough to cover the stop: " + workerPhase.ToString("c", CultureInfo.InvariantCulture));
        Assert.Equal(1, code);
        JsonObject final = StatusFileAssert.Read(RunAsyncGolden.RunDirFrom(stdout));
        Assert.Equal("done", final["phase"]!.GetValue<string>());
        Assert.Equal(1, final["live"]!["api_calls"]!.GetValue<int>());
        Assert.Equal("budget-exceeded", final["end"]!["status"]!.GetValue<string>());
    }

    /// <summary>
    /// The status document of the run in the runs root; null before there is one.
    /// </summary>
    private static JsonObject? ReadStatus(string root)
    {
        string runs = Path.Combine(root, "runs");
        string? path = Directory.Exists(runs) ? Directory.GetDirectories(runs).Select(d => Path.Combine(d, "status.json")).FirstOrDefault(p => File.Exists(p)) : null;
        return path is null ? null : JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
