using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

[Collection("launcher-process-state")]
public class RunAsyncCharacterizationTests
{
    private const string SuccessStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}'
        printf '%s\n' '{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":3,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":2,"permission_denials":[{"tool_name":"Bash"}],"usage":{"output_tokens_details":{"thinking_tokens":7}}}'
        """;

    private const string ErrorStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":100,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":10}}}'
        printf '%s\n' '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"it broke","session_id":"s2","total_cost_usd":0.0002,"num_turns":1,"permission_denials":[]}'
        """;

    private const string CrashScript = """
        i=1
        while [ $i -le 60 ]; do echo "err line $i" >&2; i=$((i+1)); done
        exit 3
        """;

    private const string BudgetScript = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        sleep 30
        """;

    private const string LongReportStream = """
        printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"ABCDEFGHIJKLMNOPQRST","session_id":"s3","total_cost_usd":0,"num_turns":1,"permission_denials":[]}'
        """;

    [Fact]
    public async Task Success_prints_the_golden_block_and_writes_summary_ledger_and_filesAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, SuccessStream);

        Assert.Equal(0, code);
        Assert.Equal(RunAsyncGolden.LinesWithEvidence(stdout,
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   success  (subtype=success, reason=completed, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     2 turns, 1 API calls, <DUR>",
            "cost:     $0.0022 (list price; metered; runtime reported $0.0100)",
            "budget:   $2, wrap-up off",
            "tokens:   input 1,000 | cache write 0 | cache read 9,200 | output 50 (thinking 7)",
            "context:  first call 10,200 (cache read 90%) | peak 10,200",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "WARNING:  1 permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.",
            "--- worker report ---",
            "DONE"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);

        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(RunAsyncGolden.BareCommand(root), RunAsyncGolden.Normalize(await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken), root).Replace("<ROOT>", root, StringComparison.Ordinal));
        Assert.Equal(RunAsyncGolden.ReportWithEvidence("DONE", s), await File.ReadAllTextAsync(Path.Combine(runDir, "report.md"), TestContext.Current.CancellationToken));
        Assert.Equal("do nothing", await File.ReadAllTextAsync(Path.Combine(runDir, "task.md"), TestContext.Current.CancellationToken));
        Assert.Equal(RunAsyncGolden.Notes, await File.ReadAllTextAsync(Path.Combine(runDir, "system.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_result_exits_1_with_status_errorAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, ErrorStream);

        Assert.Equal(1, code);
        Assert.Equal(RunAsyncGolden.LinesWithEvidence(stdout,
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   error  (subtype=error_during_execution, reason=, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     1 turns, 1 API calls, <DUR>",
            "cost:     $0.0002 (list price; metered)",
            "budget:   $2, wrap-up off",
            "tokens:   input 100 | cache write 0 | cache read 0 | output 10 (thinking 0)",
            "context:  first call 100 (cache read 0%) | peak 100",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "it broke"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("error", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(RunAsyncGolden.ReportWithEvidence("it broke", s), await File.ReadAllTextAsync(Path.Combine(runDir, "report.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_result_and_a_non_zero_exit_is_crashed_and_echoes_exactly_40_stderr_linesAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, CrashScript);

        Assert.Equal(1, code);
        List<string> expected =
        [
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   crashed  (subtype=, reason=, exit=3)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     0 turns, 0 API calls, <DUR>",
            "cost:     $0.0000 (list price; metered)",
            "budget:   $2, wrap-up off",
            "tokens:   input 0 | cache write 0 | cache read 0 | output 0 (thinking 0)",
            "context:  first call 0 | peak 0",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "(no report text)",
            "--- stderr (first 40 lines) ---",
        ];
        expected.AddRange(Enumerable.Range(1, 40).Select(i => $"err line {i.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(RunAsyncGolden.LinesWithEvidence(stdout, [.. expected]), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("crashed", s["status"]!.GetValue<string>());
        Assert.Equal(3, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(60, (await File.ReadAllLinesAsync(Path.Combine(runDir, "stderr.txt"), TestContext.Current.CancellationToken)).Length);
        Assert.Equal(RunAsyncGolden.ReportWithEvidence(string.Empty, s), await File.ReadAllTextAsync(Path.Combine(runDir, "report.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exceeding_the_budget_stops_the_worker_with_status_budget_exceededAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, BudgetScript, o => o.MaxBudgetUsd = 0.01m);

        Assert.Equal(1, code);
        Assert.Equal(RunAsyncGolden.LinesWithEvidence(stdout,
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   budget-exceeded  (subtype=, reason=, exit=137)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     0 turns, 1 API calls, <DUR>",
            "cost:     $1.0000 (list price; metered)",
            "budget:   $0.01, wrap-up off, STOPPED at the budget",
            "tokens:   input 1,000,000 | cache write 0 | cache read 0 | output 0 (thinking 0)",
            "context:  first call 1,000,000 (cache read 0%) | peak 1,000,000",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "(no report text)"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("budget-exceeded", s["status"]!.GetValue<string>());
        Assert.Equal(137, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(RunAsyncGolden.BareCommand(root, "0.01"), await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_long_report_is_truncated_to_report_max_chars_with_a_pointer_to_report_mdAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, LongReportStream, o => o.ReportMaxChars = 10);

        Assert.Equal(0, code);
        Assert.Equal(RunAsyncGolden.LinesWithEvidence(stdout,
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   success  (subtype=success, reason=, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     1 turns, 0 API calls, <DUR>",
            "cost:     $0.0000 (list price; metered)",
            "budget:   $2, wrap-up off",
            "tokens:   input 0 | cache write 0 | cache read 0 | output 0 (thinking 0)",
            "context:  first call 0 | peak 0",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "--- worker report ---",
            "ABCDEFGHIJ",
            "[truncated; full report: <RUN:golden>/report.md]"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
        Assert.Equal(RunAsyncGolden.ReportWithEvidence("ABCDEFGHIJKLMNOPQRST", s), await File.ReadAllTextAsync(Path.Combine(runDir, "report.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Continue_from_writes_the_previous_task_and_report_into_task_md_and_inherits_the_nameAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (_, string firstOut) = await RunAsyncGolden.RunAsync(root, ErrorStream);
        string firstDir = RunAsyncGolden.RunDirFrom(firstOut);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TimeProvider.System, TestContext.Current.CancellationToken); // run dirs are keyed by second-resolution timestamp

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, SuccessStream, o => { o.TaskFile = null; o.Name = null; o.ContinueFrom = firstDir; });

        Assert.Equal(0, code);
        Assert.Equal(RunAsyncGolden.LinesWithEvidence(stdout,
            "LEAN-WORKER RESULT",
            "run:      <RUN:golden>",
            "status:   success  (subtype=success, reason=completed, exit=0)",
            "model:    anthropic/claude-haiku-4-5, effort medium, profile (none)",
            "runtime:  claude, mode bare, hooks off (bare), cache 5m",
            "work:     2 turns, 1 API calls, <DUR>",
            "cost:     $0.0022 (list price; metered; runtime reported $0.0100)",
            "budget:   $2, wrap-up off",
            "tokens:   input 1,000 | cache write 0 | cache read 9,200 | output 50 (thinking 7)",
            "context:  first call 10,200 (cache read 90%) | peak 10,200",
            "note:     " + RunAsyncGolden.BareWrapUpNote,
            "WARNING:  1 permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.",
            "--- worker report ---",
            "DONE"), RunAsyncGolden.Normalize(stdout, root));

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        Assert.NotEqual(firstDir, runDir, StringComparer.Ordinal);
        string nl = Environment.NewLine;
        Assert.Equal(
            "do nothing" + nl + nl + "## Continuation (lean-worker)" + nl + nl +
            "A previous worker on this task stopped before finishing (status: error). Its report is below. " +
            "Check the current state first (for example `git status` and `git diff --stat`) and do not redo finished work." +
            nl + nl + "it broke" + nl,
            await File.ReadAllTextAsync(Path.Combine(runDir, "task.md"), TestContext.Current.CancellationToken));

        JsonObject s = RunAsyncGolden.Summary(runDir);
        RunAsyncGolden.AssertSummaryKeys(s);
        Assert.Equal("golden", s["name"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(firstDir), s["continued_from"]!.GetValue<string>());
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, s["exit_code"]!.GetValue<int>());
        Assert.Equal("bare", s["mode"]!.GetValue<string>());
        Assert.Equal("metered", s["billing"]!.GetValue<string>());
        Assert.Equal("off (bare)", s["hooks"]!.GetValue<string>());
        Assert.Equal("5m", s["cache_ttl"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", s["model"]!.GetValue<string>());
        Assert.Equal("anthropic", s["provider"]!.GetValue<string>());
        Assert.False(s["wrapped_up"]!.GetValue<bool>());
        Assert.Null(s["escalate_to"]);
        Assert.Equal([RunAsyncGolden.BareWrapUpNote], RunAsyncGolden.Notes_(s));
        string[] rows = await File.ReadAllLinesAsync(Path.Combine(root, "runs.jsonl"), TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Length);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(rows[1]), s), rows[1]);
        RunAsyncGolden.AssertFiles(runDir, "command.txt", "report.md", "stderr.txt", "stream.jsonl", "summary.json", "system.md", "task.md");
    }

    [Fact]
    public async Task Missing_task_and_continue_from_is_a_launch_errorAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(root, "exit 0", o => o.TaskFile = null));
        Assert.Equal("--task <file> or --continue-from <run-dir> is required", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Invalid_effort_is_a_launch_errorAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(root, "exit 0", o => o.Effort = "ultra"));
        Assert.Equal("invalid effort 'ultra'", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Invalid_cache_ttl_is_a_launch_errorAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(root, "exit 0", o => o.CacheTtl = "2h"));
        Assert.Equal("invalid cache TTL '2h' (5m | 1h | default)", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Wrap_up_at_one_is_a_launch_errorAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(root, "exit 0", o => o.WrapUpAt = 1m));
        Assert.Equal("invalid wrap-up share 1 (0 = off, else below 1)", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task Mode_bare_without_a_key_is_a_launch_errorAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(root, "exit 0", apiKey: null));
        Assert.Equal(
            "--mode bare needs ANTHROPIC_API_KEY (or --claude-settings with an apiKeyHelper) and an Anthropic model: --bare never reads " +
            "OAuth or the keychain. With a subscription login (e.g. Enterprise), use --mode lean or leave --mode auto.",
            ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }
}
