using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The index scopes of `status` (docs/research/live-status/README.md, sections 4.2 and 4.4): the default scope
/// (the caller's children by key intersection), --owner, --all, --whoami, the no-identity exit, the --since defaults
/// of each scope, the header and the OWNER column. Runs and pointers are hand-made (StatusIndexRig).
/// </summary>
public sealed class StatusIndexScopeTests : IDisposable
{
    private const string NoCaller = "lean-worker status: cannot tell which session you are; pass --owner, --here or --all\n";

    private static readonly TimeSpan _old = TimeSpan.FromDays(3);

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task<string[]> NamesAsync(Func<string, string?> env, params string[] args)
    {
        (int code, string json) = await _rig.CallAsync(env, [.. args, "--json"]);
        Assert.Equal(0, code);
        return [.. StatusIndexRig.JsonNames(json).Order(StringComparer.Ordinal)];
    }

    private async Task<string[]> NamesAsync(params string[] args)
    {
        string[] names = await NamesAsync(StatusIndexRig.NoCaller, args);
        return names;
    }

    [Fact]
    public async Task The_default_scope_lists_the_runs_whose_owner_keys_meet_the_callers_keysAsync()
    {
        _rig.Proc.Add(50, 777);
        _ = _rig.Add("by-session", ["claude-session:sess-A"]);
        _ = _rig.Add("by-pid-after-clear", ["claude-session:sess-OLD", "pid:50@777"]);
        _ = _rig.Add("someone-elses", ["claude-session:sess-B", "pid:51@1"]);
        _ = _rig.Add("recycled-pid", ["pid:50@111"]);
        _ = _rig.Add("unowned");

        string[] names = await NamesAsync(StatusIndexRig.Session("sess-A", ("CLAUDE_PID", "50")));

        Assert.Equal(["by-pid-after-clear", "by-session"], names);
    }

    [Fact]
    public async Task Two_sessions_in_one_repository_each_see_only_their_own_runs_and_here_shows_both_with_an_owner_columnAsync()
    {
        _ = _rig.Add("a-one", ["claude-session:sess-A"]);
        _ = _rig.Add("a-two", ["claude-session:sess-A"]);
        _ = _rig.Add("b-one", ["claude-session:sess-B"]);

        string[] seenByA = await NamesAsync(StatusIndexRig.Session("sess-A"));
        string[] seenByB = await NamesAsync(StatusIndexRig.Session("sess-B"));
        (int code, string here) = await _rig.CallAsync("--here", "--runs-root", _rig.Root);

        Assert.Equal(["a-one", "a-two"], seenByA);
        Assert.Equal(["b-one"], seenByB);
        Assert.Equal(0, code);
        string[] rows = StatusIndexRig.Rows(here);
        Assert.Equal(3, rows.Length);
        Assert.Matches(@"a-one\s+cc:sess-A\s", rows.Single(r => r.Contains("a-one", StringComparison.Ordinal)));
        Assert.Matches(@"b-one\s+cc:sess-B\s", rows.Single(r => r.Contains("b-one", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_caller_whose_keys_meet_no_pointer_gets_an_empty_listing_and_exit_0Async()
    {
        _ = _rig.Add("someone-elses", ["claude-session:sess-B"]);

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(0, code);
        Assert.Equal(["lean-worker status: owner cc:sess-A, 0 live, 0 ended, 0 need attention"], StatusIndexRig.Lines(output));
    }

    [Fact]
    public async Task The_owner_header_names_the_session_and_its_relay_name_and_counts_live_ended_and_attention_rowsAsync()
    {
        _ = _rig.Add("live-one", ["claude-session:sess-A"]);
        _ = _rig.Add("done-one", ["claude-session:sess-A"], endedAgo: TimeSpan.FromMinutes(30));
        _ = _rig.Add("failed-one", ["claude-session:sess-A"], endedAgo: TimeSpan.FromMinutes(40), status: "error");

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Session("sess-A", ("MSG_RELAY_NAME", "track-1a")));

        Assert.Equal(0, code);
        Assert.Equal("lean-worker status: owner cc:sess-A (track-1a), 1 live, 2 ended, 1 needs attention", StatusIndexRig.Lines(output)[0]);
        _ = TerminalAssert.AssertPlain(output, 3);
    }

    [Theory]
    [InlineData("CLAUDE_CODE_SESSION_ID", "0f3e9a12-aaaa-bbbb", "owner cc:0f3e9a12,")]
    [InlineData("OPENCODE_SESSION_ID", "ses_abcdefghijkl", "owner oc:ses_abcd,")]
    [InlineData("CLAUDE_PID", "50", "owner pid:50,")]
    [InlineData("OPENCODE_PID", "50", "owner pid:50,")]
    public async Task The_header_shows_the_short_form_of_the_first_keyAsync(string variable, string value, string expected)
    {
        _rig.Proc.Add(50, 777);

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Env((variable, value)));

        Assert.Equal(0, code);
        Assert.Contains(expected, StatusIndexRig.Lines(output)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_lists_the_runs_of_that_key_instead_of_the_callers_and_reports_it_in_jsonAsync()
    {
        _ = _rig.Add("a-one", ["claude-session:sess-A"]);
        _ = _rig.Add("b-one", ["claude-session:sess-B"]);

        (int code, string plain) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--owner", "claude-session:sess-B");
        (_, string json) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--owner", "claude-session:sess-B", "--json");

        Assert.Equal(0, code);
        Assert.StartsWith("lean-worker status: owner cc:sess-B, 1 live", plain, StringComparison.Ordinal);
        Assert.Equal(["b-one"], StatusIndexRig.JsonNames(json));
        JsonObject doc = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("claude-session:sess-B", doc["caller"]!["keys"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task The_json_listing_of_the_default_scope_names_the_caller_with_its_keys_and_relay_nameAsync()
    {
        _rig.Proc.Add(50, 777);
        _ = _rig.Add("a-one", ["claude-session:sess-A"]);

        (int code, string json) = await _rig.CallAsync(StatusIndexRig.Session("sess-A", ("CLAUDE_PID", "50"), ("MSG_RELAY_NAME", "track-1a")), "--json");

        Assert.Equal(0, code);
        JsonObject doc = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("lean-worker.status-list/1", doc["schema"]!.GetValue<string>());
        Assert.Equal(["claude-session:sess-A", "pid:50@777"], [.. doc["caller"]!["keys"]!.AsArray().Select(k => k!.GetValue<string>())]);
        Assert.Equal("track-1a", doc["caller"]!["relay_name"]!.GetValue<string>());
        Assert.Equal(["a-one"], StatusIndexRig.JsonNames(json));
    }

    [Fact]
    public async Task Owner_works_without_any_caller_identity_and_matches_the_key_as_givenAsync()
    {
        _ = _rig.Add("typed", ["opencode-session:s1"]);
        _ = _rig.Add("other", ["opencode-session:s2"]);
        _ = _rig.Add("custom", ["track/1.a@b"]);

        Assert.Equal(["typed"], await NamesAsync("--owner", "opencode-session:s1"));
        Assert.Equal(["custom"], await NamesAsync("--owner", "track/1.a@b"));
        Assert.Empty(await NamesAsync("--owner", "no-such-key"));
    }

    [Fact]
    public async Task Owner_with_a_run_key_lists_the_children_of_that_runAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", ["claude-session:sess-A"]);
        _ = _rig.Add("child-run", ["run:" + parent.Id], parent: parent.Id);
        _ = _rig.Add("unrelated", ["claude-session:sess-B"]);

        string[] names = await NamesAsync("--owner", "run:" + parent.Id);

        Assert.Equal(["child-run"], names);
    }

    [Fact]
    public async Task All_lists_every_pointer_names_the_folder_and_shows_the_owner_column_with_a_dash_for_no_ownerAsync()
    {
        _ = _rig.Add("a-run", ["claude-session:0f3e9a12-xxxx", "pid:50@777"]);
        _ = _rig.Add("b-run", ["opencode-session:ses_abcdefghijkl"]);
        _ = _rig.Add("p-run", ["pid:77@12345"]);
        _ = _rig.Add("nobody-run");

        (int code, string output) = await _rig.AllAsync();

        Assert.Equal(0, code);
        string[] lines = TerminalAssert.AssertPlain(output, 4);
        Assert.StartsWith("lean-worker status: all runs in " + _rig.Folder + ", 4 live", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"a-run\s+cc:0f3e9a12\s", lines.Single(l => l.Contains("a-run", StringComparison.Ordinal)));
        Assert.Matches(@"b-run\s+oc:ses_abcd\s", lines.Single(l => l.Contains("b-run", StringComparison.Ordinal)));
        Assert.Matches(@"p-run\s+pid:77\s", lines.Single(l => l.Contains("p-run", StringComparison.Ordinal)));
        Assert.Matches(@"nobody-run\s+-\s", lines.Single(l => l.Contains("nobody-run", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_default_and_owner_scopes_have_no_owner_columnAsync()
    {
        _ = _rig.Add("a-run", ["claude-session:sess-A"]);

        (_, string output) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"));

        Assert.DoesNotMatch(@"a-run\s+cc:sess-A", output);
        Assert.Matches(@"(?m)^running\s+a-run\s+oc\s", output);
    }

    [Fact]
    public async Task All_with_no_index_folder_at_all_lists_nothing_and_exits_0Async()
    {
        (int code, string output) = await _rig.AllAsync();

        Assert.Equal(0, code);
        Assert.Equal(["lean-worker status: all runs in " + _rig.Folder + ", 0 live, 0 ended, 0 need attention"], StatusIndexRig.Lines(output));
    }

    [Fact]
    public async Task The_owner_scope_has_no_window_all_and_here_hide_what_ended_over_24_hours_agoAsync()
    {
        string[] mine = ["claude-session:sess-A"];
        _ = _rig.Add("recent-end", mine, endedAgo: TimeSpan.FromHours(1), startedAgo: TimeSpan.FromHours(2));
        _ = _rig.Add("old-end", mine, endedAgo: _old, startedAgo: _old + TimeSpan.FromHours(1));

        string[] owner = await NamesAsync(StatusIndexRig.Session("sess-A"));
        string[] ownerKey = await NamesAsync("--owner", mine[0]);
        string[] all = await NamesAsync("--all");
        string[] allWide = await NamesAsync("--all", "--since", "30d");
        string[] here = await NamesAsync("--here", "--runs-root", _rig.Root);

        Assert.Equal(["old-end", "recent-end"], owner);
        Assert.Equal(["old-end", "recent-end"], ownerKey);
        Assert.Equal(["recent-end"], all);
        Assert.Equal(["old-end", "recent-end"], allWide);
        Assert.Equal(["recent-end"], here);
        (_, string plain) = await _rig.CallAsync("--all");
        Assert.Contains(", 1 older than 24h not shown", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_owner_scope_with_since_applies_the_window_to_ended_rows_onlyAsync()
    {
        string[] mine = ["claude-session:sess-A"];
        _ = _rig.Add("old-end", mine, endedAgo: _old, startedAgo: _old + TimeSpan.FromHours(1));
        _ = _rig.Add("old-live", mine, startedAgo: _old);

        string[] names = await NamesAsync(StatusIndexRig.Session("sess-A"), "--since", "1h");

        Assert.Equal(["old-live"], names);
    }

    [Fact]
    public async Task Rows_that_need_attention_are_never_hidden_by_the_windowAsync()
    {
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        _ = _rig.Add("old-orphaned", ["claude-session:sess-A"], startedAgo: _old, tweak: d => d["launcher"]!["pid"] = 900);
        _ = _rig.Add("old-live", ["claude-session:sess-A"], startedAgo: _old);
        _ = _rig.Add("old-unknown", ["claude-session:sess-A"], startedAgo: _old, tweak: d => d["launcher"]!["boot_id"] = null);
        StatusIndexRig.Added torn = _rig.Add("old-unreadable", ["claude-session:sess-A"], startedAgo: _old);
        await File.WriteAllTextAsync(Path.Combine(torn.Dir, "status.json"), "{ torn", TestContext.Current.CancellationToken);
        _ = _rig.Add("old-ended", ["claude-session:sess-A"], endedAgo: _old, startedAgo: _old);

        string[] names = await NamesAsync("--all");

        Assert.Equal(["old-live", "old-orphaned", "old-unknown", "old-unreadable"], names);
    }

    [Fact]
    public async Task Whoami_prints_the_callers_keys_one_per_line_and_with_key_only_the_session_keyAsync()
    {
        _rig.Proc.Add(50, 777);
        Func<string, string?> env = StatusIndexRig.Session("sess-A", ("CLAUDE_PID", "50"), ("MSG_RELAY_NAME", "track-1a"));

        (int code, string all) = await _rig.CallAsync(env, "--whoami");
        (int keyCode, string key) = await _rig.CallAsync(env, "--whoami", "--key");

        Assert.Equal(0, code);
        Assert.Equal("claude-session:sess-A\npid:50@777\n", all);
        Assert.Equal(0, keyCode);
        Assert.Equal("claude-session:sess-A\n", key);
    }

    [Fact]
    public async Task Whoami_key_falls_back_to_the_pid_key_when_there_is_no_session_idAsync()
    {
        _rig.Proc.Add(50, 777);

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Env(("OPENCODE_PID", "50")), "--whoami", "--key");

        Assert.Equal(0, code);
        Assert.Equal("pid:50@777\n", output);
    }

    [Fact]
    public async Task Whoami_inside_a_worker_prints_the_run_key_and_not_the_inherited_session_keysAsync()
    {
        Func<string, string?> env = StatusIndexRig.Session("sess-A", ("LEAN_WORKER_RUN_ID", "20261122-115000-parent"));

        (_, string all) = await _rig.CallAsync(env, "--whoami");
        (_, string key) = await _rig.CallAsync(env, "--whoami", "--key");

        Assert.Equal("run:20261122-115000-parent\n", all);
        Assert.Equal("run:20261122-115000-parent\n", key);
    }

    [Theory]
    [InlineData("--whoami")]
    [InlineData("--whoami --key")]
    public async Task Whoami_with_no_identity_is_exit_2_with_the_no_identity_messageAsync(string args)
    {
        _rig.Proc.Add(60, 1, state: 'Z');

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Env(("CLAUDE_PID", "60"), ("OPENCODE_PID", "61"), ("CLAUDE_CODE_SESSION_ID", "bad id")), args.Split(' '));

        Assert.Equal(2, code);
        Assert.Equal(NoCaller, output);
    }

    [Theory]
    [InlineData("--whoami --json")]
    [InlineData("--whoami --all")]
    [InlineData("--whoami --owner k")]
    [InlineData("--whoami --since 1h")]
    [InlineData("--key")]
    public async Task Whoami_and_key_misused_are_usage_errorsAsync(string args)
    {
        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), args.Split(' '));

        Assert.Equal(2, code);
        Assert.StartsWith("lean-worker status: ", output, StringComparison.Ordinal);
        Assert.Contains("usage:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--json")]
    [InlineData("--since 1h")]
    public async Task Without_a_scope_and_without_a_caller_identity_the_exit_is_2_and_nothing_is_listedAsync(string args)
    {
        _ = _rig.Add("some-run", ["claude-session:sess-A"]);

        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Env(("CLAUDE_PID", "9999")), args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(2, code);
        Assert.Equal(NoCaller, output);
    }

    [Theory]
    [InlineData("--owner")]
    [InlineData("--owner a;b")]
    [InlineData("--owner a=b")]
    [InlineData("--owner with space")]
    [InlineData("--owner a --all")]
    public async Task A_bad_owner_option_or_two_scopes_is_a_usage_errorAsync(string args)
    {
        (int code, string output) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), args.Split(' '));

        Assert.Equal(2, code);
        Assert.Contains("usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_owner_key_with_an_escape_or_too_long_is_a_usage_error_with_a_clean_messageAsync()
    {
        string[] keys = ["claude-session:\u001b[31mx", new string('k', OwnerKeys.MaxLength + 1), "a\nb"];
        foreach (string key in keys)
        {
            (int code, string output) = await _rig.CallAsync("--owner", key);

            Assert.Equal(2, code);
            _ = TerminalAssert.AssertPlain(output, 0);
        }
    }

    [Fact]
    public async Task The_longest_valid_owner_key_is_accepted_by_ownerAsync()
    {
        string key = new('k', OwnerKeys.MaxLength);
        _ = _rig.Add("long-key", [key]);

        Assert.Equal(["long-key"], await NamesAsync("--owner", key));
    }

    [Fact]
    public async Task Reading_an_index_scope_changes_nothing_when_no_pointer_is_old_enough_to_reapAsync()
    {
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        _ = _rig.Add("live", ["claude-session:sess-A"]);
        _ = _rig.Add("done", ["claude-session:sess-A"], endedAgo: TimeSpan.FromDays(6));
        _ = _rig.Add("orphan", ["claude-session:sess-A"], tweak: d => d["launcher"]!["pid"] = 900);
        string[] before = StatusIndexRig.Snapshot(_rig.Root, _rig.State, _rig.Proc.Root);

        _ = await _rig.CallAsync(StatusIndexRig.Session("sess-A"));
        _ = await _rig.CallAsync("--all", "--json");
        _ = await _rig.CallAsync("--owner", "claude-session:sess-A");
        _ = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--whoami");

        Assert.Equal(before, StatusIndexRig.Snapshot(_rig.Root, _rig.State, _rig.Proc.Root));
    }
}
