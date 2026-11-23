using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The `status` command (docs/research/live-status/README.md, section 4.2): scopes --run and --here,
/// --since, the row order, the plain one-shot output, --json, the exit codes 0 and 2 and the promise
/// that it creates, changes and deletes nothing. Reader inputs are a fake /proc, a fake clock and a
/// boot id; the git root cases use a real `git init` in a temp directory.
/// </summary>
public sealed class StatusCommandTests : IDisposable
{
    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly FakeClock _clock = new(StatusDocs.Now);
    private readonly string _root;

    public StatusCommandTests() => _root = _dirs.Create("lw-status-cmd");

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private static DateTimeOffset Ago(TimeSpan by) => StatusDocs.Now - by;

    private static string Id(DateTimeOffset at, string name) => at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + name;

    private static string[] Sorted(string[] names) => [.. names.Order(StringComparer.Ordinal)];

    private async Task<(int Code, string Output)> RunAsync(string cwd, params string[] args)
    {
        await using StringWriter output = new();
        int code = await StatusCommand.RunAsync(args, output, new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot), cwd);
        return (code, output.ToString());
    }

    private async Task<(int Code, string Output)> HereAsync(params string[] args)
    {
        string[] all = ["--here", "--runs-root", _root, .. args];
        (int code, string output) = await RunAsync(_root, all);
        return (code, output);
    }

    private string LiveRun(string name, int launcherPid, TimeSpan? startedAgo = null, DateTimeOffset? lastEvent = null, JsonObject? tool = null)
    {
        _proc.Add(launcherPid, StatusDocs.LauncherTicks);
        DateTimeOffset started = Ago(startedAgo ?? TimeSpan.FromMinutes(5));
        return StatusDocs.WriteRun(_root, Id(started, name), StatusDocs.Make(name, launcherPid: launcherPid, worker: StatusDocs.Worker(),
            lastEventAt: lastEvent, startedAt: started, lastTool: tool));
    }

    private string EndedRun(string name, string status, TimeSpan endedAgo, string? summary = null)
    {
        DateTimeOffset at = Ago(endedAgo);
        return StatusDocs.WriteRun(_root, Id(at.AddMinutes(-10), name),
            StatusDocs.Make(name, phase: "done", launcherPid: 900, end: StatusDocs.End(status, at), startedAt: at.AddMinutes(-10)), summary);
    }

    private string OrphanedRun(string name, int workerPid)
    {
        _proc.Add(workerPid, StatusDocs.WorkerTicks);
        return StatusDocs.WriteRun(_root, Id(Ago(TimeSpan.FromMinutes(20)), name),
            StatusDocs.Make(name, launcherPid: 901, worker: StatusDocs.Worker(workerPid, workerPid, workerPid)));
    }

    private string LostRun(string name) =>
        StatusDocs.WriteRun(_root, Id(Ago(TimeSpan.FromMinutes(20)), name), StatusDocs.Make(name, boot: "boot-OLD", worker: StatusDocs.Worker()));

    private string LegacyRun(string name, TimeSpan startedAgo, string? summary = null) =>
        StatusDocs.WriteRun(_root, Id(Ago(startedAgo), name), doc: null, summary);

    private string UnreadableRun(string name)
    {
        string dir = StatusDocs.WriteRun(_root, Id(Ago(TimeSpan.FromMinutes(20)), name), doc: null);
        File.WriteAllText(Path.Combine(dir, "status.json"), "{ torn");
        return dir;
    }

    private static string[] Names(string json) =>
        [.. JsonNode.Parse(json)!["runs"]!.AsArray().Select(r => (r!["run"]?["name"] ?? r["name"])!.GetValue<string>())];

    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Status_with_runs_root_but_without_a_scope_is_a_usage_error_that_names_run_and_hereAsync()
    {
        (int code, string output) = await RunAsync(_root, "--runs-root", "x");

        Assert.Equal(2, code);
        Assert.Contains("--run", output, StringComparison.Ordinal);
        Assert.Contains("--here", output, StringComparison.Ordinal);
    }

    // Train 2: no scope means the caller's own runs; with no caller identity (no environment here) that is
    // exit 2 with the no-identity message, not a usage error that names --run and --here.
    [Theory]
    [InlineData("")]
    [InlineData("--json")]
    [InlineData("--since 1h")]
    public async Task Status_without_a_scope_and_without_a_caller_identity_is_exit_2_with_the_no_identity_messageAsync(string args)
    {
        (int code, string output) = await RunAsync(_root, Words(args));

        Assert.Equal(2, code);
        Assert.Equal("lean-worker status: cannot tell which session you are; pass --owner, --here or --all\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--owner")]
    [InlineData("--all")]
    [InlineData("--wait")]
    [InlineData("--watch")]
    [InlineData("--whoami")]
    public async Task An_unknown_flag_or_a_later_slices_flag_is_a_usage_error_that_names_run_and_hereAsync(string flag)
    {
        (int code, string output) = await RunAsync(_root, "--here", flag);

        Assert.Equal(2, code);
        Assert.Contains("--run", output, StringComparison.Ordinal);
        Assert.Contains("--here", output, StringComparison.Ordinal);
        Assert.Contains(flag, output, StringComparison.Ordinal);
    }

    // Train 2: --owner is a scope now (it lists, exit 0); --wait and --watch are a later train's and stay usage errors.
    [Theory]
    [InlineData("--wait")]
    [InlineData("--watch")]
    public async Task A_later_slices_scope_or_mode_alone_is_a_usage_error_tooAsync(string args)
    {
        (int code, string output) = await RunAsync(_root, Words(args));

        Assert.Equal(2, code);
        Assert.Contains("--run", output, StringComparison.Ordinal);
        Assert.Contains("--here", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_on_a_missing_dir_is_exit_2_and_names_the_dirAsync()
    {
        string missing = Path.Combine(_root, "no-such-run");

        (int code, string output) = await RunAsync(_root, "--run", missing);

        Assert.Equal(2, code);
        Assert.Contains(missing, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("--run")]
    [InlineData("--here --since")]
    [InlineData("--here --since soon")]
    [InlineData("--here --since -5m")]
    public async Task A_missing_or_bad_value_is_a_usage_errorAsync(string args)
    {
        (int code, _) = await RunAsync(_root, Words(args));

        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Run_and_here_together_are_a_usage_errorAsync()
    {
        (int code, _) = await RunAsync(_root, "--run", _root, "--here");

        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Run_lists_the_one_run_a_relative_dir_is_taken_from_the_callers_directoryAsync()
    {
        _ = LiveRun("fix-save-w1", 120);
        _ = EndedRun("decoy-run", "success", TimeSpan.FromMinutes(30));
        string runDir = Directory.GetDirectories(Path.Combine(_root, "runs"), "*fix-save-w1").Single();

        (int code, string output) = await RunAsync(Path.Combine(_root, "runs"), "--run", Path.GetFileName(runDir));

        Assert.Equal(0, code);
        string[] lines = Lines(output);
        Assert.Equal(2, lines.Length);
        Assert.Contains("fix-save-w1", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("decoy-run", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Here_reads_the_runs_root_it_is_givenAsync()
    {
        _ = LiveRun("in-root", 121);
        string other = _dirs.Create("lw-status-other");
        _ = StatusDocs.WriteRun(other, "20261122-110000-in-other", StatusDocs.Make("in-other", worker: StatusDocs.Worker()));

        (int code, string output) = await RunAsync(other, "--here", "--runs-root", _root, "--json");

        Assert.Equal(0, code);
        Assert.Equal(["in-root"], Names(output));
    }

    [Fact]
    public async Task Here_resolves_a_relative_runs_root_against_the_callers_directoryAsync()
    {
        _ = LiveRun("in-root", 122);

        (int code, string output) = await RunAsync(Path.GetDirectoryName(_root)!, "--here", "--runs-root", Path.GetFileName(_root), "--json");

        Assert.Equal(0, code);
        Assert.Equal(["in-root"], Names(output));
    }

    [Fact]
    public async Task Here_from_a_subdirectory_reads_the_git_roots_lean_worker_where_the_launcher_wroteAsync()
    {
        string repo = _dirs.Create("lw-status-repo");
        Assert.NotNull(await Git.RunAsync(repo, "init", "-q"));
        string deep = Path.Combine(repo, "src", "deep");
        _ = Directory.CreateDirectory(deep);
        _ = StatusDocs.WriteRun(Path.Combine(repo, ".lean-worker"), "20261122-110000-at-root", StatusDocs.Make("at-root", phase: "done", end: StatusDocs.End("success")));
        _ = StatusDocs.WriteRun(Path.Combine(deep, ".lean-worker"), "20261122-110000-beside-caller", StatusDocs.Make("beside-caller", phase: "done", end: StatusDocs.End("success")));

        (int code, string output) = await RunAsync(deep, "--here", "--json");

        Assert.Equal(0, code);
        Assert.Equal(["at-root"], Names(output));
    }

    [Fact]
    public async Task Here_in_a_repository_without_a_lean_worker_at_its_root_reads_the_one_beside_the_callerAsync()
    {
        string repo = _dirs.Create("lw-status-repo");
        Assert.NotNull(await Git.RunAsync(repo, "init", "-q"));
        string sub = Path.Combine(repo, "src");
        _ = Directory.CreateDirectory(sub);
        _ = StatusDocs.WriteRun(Path.Combine(sub, ".lean-worker"), "20261122-110000-beside-caller", StatusDocs.Make("beside-caller", phase: "done", end: StatusDocs.End("success")));

        (int code, string output) = await RunAsync(sub, "--here", "--json");

        Assert.Equal(0, code);
        Assert.Equal(["beside-caller"], Names(output));
    }

    [Fact]
    public async Task Here_outside_a_repository_reads_the_lean_worker_beside_the_callerAsync()
    {
        string plain = _dirs.Create("lw-status-plain");
        _ = StatusDocs.WriteRun(Path.Combine(plain, ".lean-worker"), "20261122-110000-beside-caller", StatusDocs.Make("beside-caller", phase: "done", end: StatusDocs.End("success")));

        (int code, string output) = await RunAsync(plain, "--here", "--json");

        Assert.Equal(0, code);
        Assert.Equal(["beside-caller"], Names(output));
    }

    [Fact]
    public async Task Here_with_no_runs_dir_lists_nothing_and_exits_0Async()
    {
        string empty = _dirs.Create("lw-status-empty");

        (int code, string output) = await RunAsync(empty, "--here", "--runs-root", empty);

        Assert.Equal(0, code);
        _ = Assert.Single(Lines(output));
    }

    [Fact]
    public async Task Since_hides_old_ended_and_legacy_rows_but_never_a_live_oneAsync()
    {
        _ = LiveRun("live-long-running", 123, startedAgo: TimeSpan.FromDays(5));
        _ = EndedRun("ended-recent", "success", TimeSpan.FromMinutes(30));
        _ = EndedRun("ended-3h", "success", TimeSpan.FromHours(3));
        _ = EndedRun("ended-2d", "error", TimeSpan.FromDays(2));
        _ = LegacyRun("legacy-recent", TimeSpan.FromMinutes(20), summary: "success");
        _ = LegacyRun("legacy-3h", TimeSpan.FromHours(3));

        (_, string oneHour) = await HereAsync("--since", "1h", "--json");
        (_, string fourHours) = await HereAsync("--since", "4h", "--json");
        (_, string byDefault) = await HereAsync("--json");
        (_, string tiny) = await HereAsync("--since", "1s", "--json");

        Assert.Equal(["ended-recent", "legacy-recent", "live-long-running"], Sorted(Names(oneHour)));
        Assert.Equal(["ended-3h", "ended-recent", "legacy-3h", "legacy-recent", "live-long-running"], Sorted(Names(fourHours)));
        Assert.Equal(["ended-3h", "ended-recent", "legacy-3h", "legacy-recent", "live-long-running"], Sorted(Names(byDefault)));
        Assert.Equal(["live-long-running"], Names(tiny));
    }

    [Fact]
    public async Task The_plain_header_says_how_many_rows_the_since_window_hidAsync()
    {
        _ = EndedRun("ended-2d", "success", TimeSpan.FromDays(2));

        (_, string output) = await HereAsync();

        Assert.Contains("not shown", Lines(output)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_are_ordered_attention_first_then_live_by_start_then_ended_newest_firstAsync()
    {
        _ = EndedRun("d-ended-older", "success", TimeSpan.FromHours(2));
        _ = LiveRun("a-live-late", 124, startedAgo: TimeSpan.FromMinutes(10));
        _ = EndedRun("c-ended-newer", "wrapped-up", TimeSpan.FromMinutes(30));
        _ = LiveRun("b-live-early", 125, startedAgo: TimeSpan.FromHours(1));
        _ = OrphanedRun("e-orphaned", 310);
        _ = EndedRun("f-failed", "error", TimeSpan.FromMinutes(5));
        _ = LiveRun("g-quiet", 126, startedAgo: TimeSpan.FromHours(2), lastEvent: Ago(TimeSpan.FromMinutes(41)));

        (int code, string json) = await HereAsync("--json");
        (_, string plain) = await HereAsync();

        Assert.Equal(0, code);
        string[] names = Names(json);
        Assert.Equal(["e-orphaned", "f-failed", "g-quiet"], Sorted(names[..3]));
        Assert.Equal(["b-live-early", "a-live-late", "c-ended-newer", "d-ended-older"], names[3..]);
        string[] lines = Lines(plain);
        Assert.Equal(8, lines.Length);
        string[] plainNames = [.. lines[1..].Select(l => names.Single(n => l.Contains(n, StringComparison.Ordinal)))];
        Assert.Equal(names, plainNames);
    }

    [Fact]
    public async Task The_plain_row_carries_state_name_runtime_model_calls_cost_elapsed_and_the_last_toolAsync()
    {
        _ = LiveRun("fix-save-w1", 127, tool: StatusDocs.Tool("bash", "completed", Ago(TimeSpan.FromSeconds(4))));

        (int code, string output) = await HereAsync();

        Assert.Equal(0, code);
        // Train 2: --here adds the OWNER column after the name; an unowned run shows "-" there.
        Assert.Matches(@"(?m)^running\s+fix-save-w1\s+-\s+oc\s+MiniMax-M3\s+12 calls\s+\$0\.41/\$3\.00\s+3m41s\s+last bash 4s ago$", output);
    }

    [Fact]
    public async Task The_plain_output_has_one_header_one_line_per_run_and_no_escape_byteAsync()
    {
        _ = LiveRun("live", 128);
        _ = EndedRun("ended", "success", TimeSpan.FromMinutes(30));
        _ = EndedRun("failed", "error", TimeSpan.FromMinutes(30));
        _ = OrphanedRun("orphaned", 311);
        _ = LostRun("lost");
        _ = LegacyRun("legacy", TimeSpan.FromMinutes(30));
        _ = UnreadableRun("unreadable");

        (int code, string output) = await HereAsync();

        Assert.Equal(0, code);
        Assert.DoesNotContain('\u001b', output);
        string[] lines = Lines(output);
        Assert.Equal(8, lines.Length);
        Assert.StartsWith("lean-worker status:", lines[0], StringComparison.Ordinal);
        Assert.Contains("1 live", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_name_is_never_cutAsync()
    {
        string name = string.Concat(Enumerable.Repeat("a-very-long-run-name-", 3)) + "end";
        _ = LiveRun(name, 129);

        (_, string output) = await HereAsync();

        Assert.Contains(name, Lines(output)[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_is_one_document_with_the_list_schema_and_each_run_with_its_derived_state_and_livenessAsync()
    {
        _ = LiveRun("live", 130);
        _ = EndedRun("ended", "success", TimeSpan.FromMinutes(30));
        _ = OrphanedRun("orphaned", 312);
        _ = LostRun("lost");
        _ = UnreadableRun("unreadable");

        (int code, string output) = await HereAsync("--json");

        Assert.Equal(0, code);
        JsonObject doc = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("lean-worker.status-list/1", doc["schema"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:00.000Z", doc["generated_at"]!.GetValue<string>());
        Dictionary<string, (string State, string Liveness)> byName = doc["runs"]!.AsArray().ToDictionary(
            r => (r!["run"]?["name"] ?? r["name"])!.GetValue<string>(),
            r => (r!["state"]!.GetValue<string>(), r["liveness"]!.GetValue<string>()),
            StringComparer.Ordinal);
        Assert.Equal(("running", "alive"), byName["live"]);
        Assert.Equal(("success", "gone"), byName["ended"]);
        Assert.Equal(("orphaned", "gone"), byName["orphaned"]);
        Assert.Equal(("lost", "gone"), byName["lost"]);
        Assert.Equal(("unreadable", "unknown"), byName["unreadable"]);
    }

    [Fact]
    public async Task A_json_run_is_its_status_json_content_plus_the_derived_fieldsAsync()
    {
        _ = LiveRun("live", 131);

        (_, string output) = await HereAsync("--json");

        JsonObject run = JsonNode.Parse(output)!["runs"]![0]!.AsObject();
        Assert.Equal("lean-worker.status/1", run["schema"]!.GetValue<string>());
        Assert.Equal(7, run["seq"]!.GetValue<long>());
        Assert.Equal("MiniMax-M3", run["spec"]!["model"]!.GetValue<string>());
        Assert.Equal(12, run["live"]!["api_calls"]!.GetValue<int>());
        Assert.Equal(131, run["launcher"]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_legacy_json_row_has_the_directory_only_fieldsAsync()
    {
        string dir = LegacyRun("old-run", TimeSpan.FromMinutes(30), summary: "error");

        (_, string output) = await HereAsync("--json");

        JsonObject run = JsonNode.Parse(output)!["runs"]![0]!.AsObject();
        Assert.Equal("legacy", run["state"]!.GetValue<string>());
        Assert.Equal("unknown", run["liveness"]!.GetValue<string>());
        Assert.Equal(dir, run["dir"]!.GetValue<string>());
        Assert.Equal(Path.GetFileName(dir), run["id"]!.GetValue<string>());
        Assert.Equal("old-run", run["name"]!.GetValue<string>());
        Assert.Equal("error", run["summary_status"]!.GetValue<string>());
        Assert.Equal("2026-11-22T11:30:00.000Z", run["started_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_with_json_lists_that_run_onlyAsync()
    {
        string dir = LiveRun("live", 132);
        _ = EndedRun("ended", "success", TimeSpan.FromMinutes(30));

        (int code, string output) = await RunAsync(_root, "--run", dir, "--json");

        Assert.Equal(0, code);
        Assert.Equal(["live"], Names(output));
    }

    [Fact]
    public async Task Runs_that_failed_are_still_listed_with_exit_0_in_both_modesAsync()
    {
        _ = EndedRun("failed", "error", TimeSpan.FromMinutes(5));
        _ = EndedRun("crashed", "crashed", TimeSpan.FromMinutes(5));
        _ = OrphanedRun("orphaned", 313);
        _ = LostRun("lost");
        _ = UnreadableRun("unreadable");

        (int plainCode, _) = await HereAsync();
        (int jsonCode, _) = await HereAsync("--json");

        Assert.Equal(0, plainCode);
        Assert.Equal(0, jsonCode);
    }

    [Fact]
    public async Task Reading_creates_changes_and_deletes_nothingAsync()
    {
        string live = LiveRun("live", 133);
        _ = EndedRun("ended", "success", TimeSpan.FromMinutes(30), summary: "success");
        _ = OrphanedRun("orphaned", 314);
        _ = LostRun("lost");
        _ = LegacyRun("legacy", TimeSpan.FromMinutes(30), summary: "success");
        string unreadable = UnreadableRun("unreadable");
        string[] before = Tree(_root, _proc.Root);

        _ = await HereAsync();
        _ = await HereAsync("--json");
        _ = await HereAsync("--since", "1s");
        _ = await RunAsync(_root, "--run", live);
        _ = await RunAsync(_root, "--run", unreadable, "--json");
        _ = await RunAsync(_root, "--bogus");

        Assert.Equal(before, Tree(_root, _proc.Root));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("90m", 5400)]
    [InlineData("24h", 86400)]
    [InlineData("7d", 604800)]
    public void A_duration_is_a_whole_number_and_a_unit(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), StatusCommand.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("5")]
    [InlineData("5x")]
    [InlineData("-5m")]
    [InlineData("1.5h")]
    [InlineData("abc")]
    public void Anything_else_is_not_a_duration(string text) =>
        _ = Assert.Throws<LaunchException>(() => StatusCommand.ParseDuration(text));

    [Fact]
    public void Parse_reads_the_flags_of_both_scopes()
    {
        StatusCommand.StatusArgs run = StatusCommand.Parse(["--run", "some/dir", "--json"]);
        StatusCommand.StatusArgs here = StatusCommand.Parse(["--here", "--runs-root", "r", "--since", "2h"]);

        Assert.Equal(("some/dir", false, true), (run.Run, run.Here, run.Json));
        Assert.Equal((null, true, "r", "2h", false), (here.Run, here.Here, here.RunsRoot, here.Since, here.Json));
    }

    /// <summary>
    /// Every file and directory under the roots with its size, write time and content, sorted.
    /// </summary>
    private static string[] Tree(params string[] roots)
    {
        List<string> entries = [];
        foreach (string root in roots)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                entries.Add(Directory.Exists(path) ? "D " + path : FileEntry(path));
            }
        }

        return [.. entries.Order(StringComparer.Ordinal)];
    }

    private static string FileEntry(string path) =>
        string.Create(CultureInfo.InvariantCulture, $"F {path} {new FileInfo(path).Length} {File.GetLastWriteTimeUtc(path).Ticks} {File.ReadAllText(path)}");
}
