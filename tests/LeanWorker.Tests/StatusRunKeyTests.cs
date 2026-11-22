using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Fix round 1 of issue #146, points 5 and 6 (docs/research/live-status/README.md, sections 4.2 and 4.4): a run whose
/// owner keys hold <c>run:&lt;id&gt;</c> (a detached unit launched with <c>--owner run:&lt;id&gt;</c>) is matched and
/// nested like one whose parent_run is that id, with no parent_run of its own; and LEAN_WORKER_OWNER, once it passes the
/// key check, is one more key of the caller of <c>status</c>.
/// </summary>
public sealed class StatusRunKeyTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Ago(int minutes) => TimeSpan.FromMinutes(minutes);

    private static string IdOf(string name, int minutesAgo) => StatusIndexRig.Id(StatusDocs.Now - Ago(minutesAgo), name);

    private async Task<List<JsonObject>> JsonRowsAsync(Func<string, string?> env, params string[] args)
    {
        (int code, string json) = await _rig.CallAsync(env, [.. args, "--json"]);
        Assert.Equal(0, code);
        return StatusIndexRig.JsonRows(json);
    }

    private static string[] Names(List<JsonObject> rows) => [.. rows.Select(r => (r["run"]?["name"] ?? r["name"])!.GetValue<string>())];

    [Fact]
    public async Task A_run_with_a_run_key_and_no_parent_run_is_matched_with_its_run_and_listed_nested_under_it_down_to_the_grandchildAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", _mine, startedAgo: Ago(30));
        _ = _rig.Add("other-run", _mine, startedAgo: Ago(25));
        StatusIndexRig.Added child = _rig.Add("child-run", ["run:" + parent.Id], parent: null, startedAgo: Ago(20));
        _ = _rig.Add("grandchild-run", ["run:" + child.Id], parent: null, startedAgo: Ago(10));

        List<JsonObject> rows = await JsonRowsAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(["parent-run", "child-run", "grandchild-run", "other-run"], Names(rows));
        Assert.Equal([0, 1, 2, 0], [.. rows.Select(r => r["depth"]?.GetValue<int>() ?? 0)]);
        Assert.Null(rows[1]["run"]!["parent_run"]);
    }

    [Fact]
    public async Task The_plain_listing_indents_a_run_key_child_under_its_runAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", _mine, startedAgo: Ago(30));
        _ = _rig.Add("child-run", ["run:" + parent.Id], startedAgo: Ago(20));

        (_, string plain) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"));

        string[] rows = StatusIndexRig.Rows(plain);
        Assert.Equal(2, rows.Length);
        Assert.Matches(@"^running\s+parent-run\s", rows[0]);
        Assert.Matches(@"^running\s{4,}child-run\s", rows[1]);
        Assert.StartsWith("lean-worker status: owner cc:sess-A, 2 live", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_key_whose_run_is_not_in_scope_does_not_matchAsync()
    {
        _ = _rig.Add("mine-run", _mine, startedAgo: Ago(30));
        StatusIndexRig.Added foreign = _rig.Add("foreign-run", ["claude-session:sess-B"], startedAgo: Ago(30));
        _ = _rig.Add("under-foreign", ["run:" + foreign.Id], startedAgo: Ago(20));
        _ = _rig.Add("under-nothing", ["run:20250101-000000-no-such-run"], startedAgo: Ago(20));

        List<JsonObject> mine = await JsonRowsAsync(StatusIndexRig.Session("sess-A"));
        List<JsonObject> all = await JsonRowsAsync(StatusIndexRig.NoCaller, "--all");

        Assert.Equal(["mine-run"], Names(mine));
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task A_caller_inside_a_worker_gets_the_run_key_children_that_have_no_parent_runAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", _mine, startedAgo: Ago(30));
        _ = _rig.Add("detached-unit", ["run:" + parent.Id], startedAgo: Ago(20));
        _ = _rig.Add("session-sibling", _mine, startedAgo: Ago(25));

        List<JsonObject> rows = await JsonRowsAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_RUN_ID", parent.Id)));

        Assert.Equal(["detached-unit"], Names(rows));
    }

    [Fact]
    public async Task A_cycle_through_run_keys_does_not_loop_and_lists_each_run_onceAsync()
    {
        string a = IdOf("cyc-a", 30);
        string b = IdOf("cyc-b", 20);
        _ = _rig.Add("cyc-a", [.. _mine, "run:" + b], startedAgo: Ago(30), id: a);
        _ = _rig.Add("cyc-b", ["run:" + a], startedAgo: Ago(20), id: b);
        string self = IdOf("self-key", 10);
        _ = _rig.Add("self-key", [.. _mine, "run:" + self], startedAgo: Ago(10), id: self);

        List<JsonObject> rows = await JsonRowsAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(["cyc-a", "cyc-b", "self-key"], [.. Names(rows).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task A_cycle_through_run_keys_that_no_key_of_the_caller_reaches_is_not_matchedAsync()
    {
        string c = IdOf("cyc-c", 30);
        string d = IdOf("cyc-d", 20);
        _ = _rig.Add("cyc-c", ["run:" + d], startedAgo: Ago(30), id: c);
        _ = _rig.Add("cyc-d", ["run:" + c], startedAgo: Ago(20), id: d);
        _ = _rig.Add("mine-run", _mine, startedAgo: Ago(10));

        List<JsonObject> rows = await JsonRowsAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(["mine-run"], Names(rows));
    }

    [Fact]
    public async Task A_plain_status_with_only_LEAN_WORKER_OWNER_lists_the_runs_launched_with_itAsync()
    {
        _ = _rig.Add("wrapped-one", ["wrapper-7"], startedAgo: Ago(20));
        _ = _rig.Add("wrapped-two", ["wrapper-7", "claude-session:sess-Z"], startedAgo: Ago(10));
        _ = _rig.Add("someone-elses", ["wrapper-8"], startedAgo: Ago(15));

        (int code, string plain) = await _rig.CallAsync(StatusIndexRig.Env(("LEAN_WORKER_OWNER", "wrapper-7")));

        Assert.Equal(0, code);
        Assert.StartsWith("lean-worker status: owner wrapper-7, 2 live", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
        string[] rows = StatusIndexRig.Rows(plain);
        Assert.Equal(2, rows.Length);
        Assert.DoesNotContain(rows, r => r.Contains("someone-elses", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LEAN_WORKER_OWNER_is_one_more_key_next_to_the_session_and_inside_a_runAsync()
    {
        _ = _rig.Add("by-session", _mine, startedAgo: Ago(20));
        _ = _rig.Add("by-wrapper", ["wrapper-7"], startedAgo: Ago(10));
        StatusIndexRig.Added parent = _rig.Add("parent-run", ["claude-session:sess-Z"], startedAgo: Ago(40));
        _ = _rig.Add("under-parent", ["run:" + parent.Id], parent: parent.Id, startedAgo: Ago(30));

        List<JsonObject> outside = await JsonRowsAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_OWNER", "wrapper-7")));
        List<JsonObject> inside = await JsonRowsAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_OWNER", "wrapper-7"), ("LEAN_WORKER_RUN_ID", parent.Id)));

        Assert.Equal(["by-session", "by-wrapper"], [.. Names(outside).Order(StringComparer.Ordinal)]);
        Assert.Equal(["by-wrapper", "under-parent"], [.. Names(inside).Order(StringComparer.Ordinal)]);
    }

    public static TheoryData<string> InvalidKeys() =>
        ["has space", "semi;colon", "new\nline", new string('k', OwnerKeys.MaxLength + 1), "bell\a"];

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public async Task An_invalid_LEAN_WORKER_OWNER_is_ignored_and_without_another_identity_the_caller_is_unknownAsync(string bad)
    {
        _ = _rig.Add("by-bad-key", [bad], startedAgo: Ago(10));
        _ = _rig.Add("by-session", _mine, startedAgo: Ago(10));

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Env(("LEAN_WORKER_OWNER", bad)));
        List<JsonObject> withSession = await JsonRowsAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_OWNER", bad)));

        Assert.Equal(2, code);
        Assert.Equal("lean-worker status: " + StatusCommand.NoCaller + "\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(["by-session"], Names(withSession));
    }

    [Fact]
    public async Task Whoami_lists_LEAN_WORKER_OWNER_after_the_session_keys_and_after_the_run_keyAsync()
    {
        _rig.Proc.Add(50, 777);

        (int onlyCode, string only) = await _rig.CallAsync(StatusIndexRig.Env(("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami");
        (_, string session) = await _rig.CallAsync(StatusIndexRig.Session("sess-A", ("CLAUDE_PID", "50"), ("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami");
        (_, string run) = await _rig.CallAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_RUN_ID", "20261122-115000-parent"), ("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami");

        Assert.Equal(0, onlyCode);
        Assert.Equal("wrapper-7\n", only);
        Assert.Equal("claude-session:sess-A\npid:50@777\nwrapper-7\n", session);
        Assert.Equal("run:20261122-115000-parent\nwrapper-7\n", run);
    }

    [Fact]
    public async Task Whoami_key_falls_back_to_LEAN_WORKER_OWNER_lastAsync()
    {
        _rig.Proc.Add(50, 777);

        (int code, string only) = await _rig.CallAsync(StatusIndexRig.Env(("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami", "--key");
        (_, string session) = await _rig.CallAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami", "--key");
        (_, string pid) = await _rig.CallAsync(StatusIndexRig.Env(("CLAUDE_PID", "50"), ("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami", "--key");
        (_, string run) = await _rig.CallAsync(StatusIndexRig.Session("sess-A", ("LEAN_WORKER_RUN_ID", "20261122-115000-parent"), ("LEAN_WORKER_OWNER", "wrapper-7")), "--whoami", "--key");

        Assert.Equal(0, code);
        Assert.Equal("wrapper-7\n", only);
        Assert.Equal("claude-session:sess-A\n", session);
        Assert.Equal("pid:50@777\n", pid);
        Assert.Equal("run:20261122-115000-parent\n", run);
    }

    [Theory]
    [InlineData("--whoami")]
    [InlineData("--whoami --key")]
    public async Task Whoami_with_only_an_invalid_LEAN_WORKER_OWNER_is_exit_2Async(string args)
    {
        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Env(("LEAN_WORKER_OWNER", "has space")), args.Split(' '));

        Assert.Equal(2, code);
        Assert.Equal("lean-worker status: " + StatusCommand.NoCaller + "\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
