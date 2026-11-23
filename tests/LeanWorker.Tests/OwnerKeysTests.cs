using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Owner capture (docs/research/live-status/README.md, section 4.4): the order of the rules at launch
/// (OwnerKeys.AtLaunch), the key check at the boundary, the caller's keys at read time (OwnerKeys.CallerOf) and
/// the short forms. The environment is a lookup and /proc a fake root, so no variable of this machine is read.
/// </summary>
public sealed class OwnerKeysTests : IDisposable
{
    private const string SessionKey = "claude-session:sess-1";
    private const string PidKey = "pid:50@777";

    private readonly FakeProcRoot _proc = new();

    public OwnerKeysTests() => _proc.Add(50, 777);

    public void Dispose() => _proc.Dispose();

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) => StatusIndexRig.Env(variables);

    private RunOwner AtLaunch(params (string Name, string Value)[] variables) => OwnerKeys.AtLaunch(option: null, Env(variables), _proc.Fs);

    private RunOwner WithOption(string option, params (string Name, string Value)[] variables) =>
        OwnerKeys.AtLaunch(option, Env(variables), _proc.Fs);

    private OwnerKeys.Caller Caller(params (string Name, string Value)[] variables) => OwnerKeys.CallerOf(Env(variables), _proc.Fs);

    [Fact]
    public void The_option_wins_over_every_other_rule_and_the_other_keys_are_still_recorded()
    {
        RunOwner owner = WithOption("my-key", ("LEAN_WORKER_OWNER", "env-key"), ("LEAN_WORKER_RUN_ID", "20261122-115000-parent"),
            ("CLAUDE_CODE_SESSION_ID", "sess-1"), ("CLAUDE_PID", "50"));

        Assert.Equal("option", owner.Source);
        Assert.Equal("my-key", owner.Keys[0]);
        Assert.Contains("env-key", owner.Keys, StringComparer.Ordinal);
        Assert.Contains("run:20261122-115000-parent", owner.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void An_option_without_a_known_type_prefix_is_stored_as_given()
    {
        RunOwner owner = WithOption("some-track.7@host/a:b");

        Assert.Equal(["some-track.7@host/a:b"], owner.Keys);
        Assert.Equal("option", owner.Source);
    }

    [Fact]
    public void LEAN_WORKER_OWNER_comes_next_and_names_its_source()
    {
        RunOwner owner = AtLaunch(("LEAN_WORKER_OWNER", "wrapper-key"), ("CLAUDE_CODE_SESSION_ID", "sess-1"));

        Assert.Equal("owner-env", owner.Source);
        Assert.Equal("wrapper-key", owner.Keys[0]);
        Assert.Contains(SessionKey, owner.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void Inside_another_run_the_owner_is_that_run_and_the_inherited_session_keys_are_recorded_but_not_match_keys()
    {
        RunOwner owner = AtLaunch(("LEAN_WORKER_RUN_ID", "20261122-115000-parent"), ("CLAUDE_CODE_SESSION_ID", "sess-1"),
            ("CLAUDE_PID", "50"), ("OPENCODE_SESSION_ID", "oc-1"));

        Assert.Equal("parent-run", owner.Source);
        Assert.Equal("20261122-115000-parent", owner.ParentRun);
        Assert.Equal(["run:20261122-115000-parent"], owner.Keys);
        Assert.Equal([SessionKey, PidKey, "opencode-session:oc-1"], owner.InheritedKeys);
    }

    [Fact]
    public void A_claude_session_id_and_the_pid_with_its_start_ticks_from_proc_are_both_keys()
    {
        RunOwner owner = AtLaunch(("CLAUDE_CODE_SESSION_ID", "sess-1"), ("CLAUDE_PID", "50"), ("MSG_RELAY_NAME", " track-1a "));

        Assert.Equal([SessionKey, PidKey], owner.Keys);
        Assert.Equal("env", owner.Source);
        Assert.Equal("claude", owner.Runtime);
        Assert.Equal("track-1a", owner.RelayName);
        Assert.Null(owner.ParentRun);
        Assert.Empty(owner.InheritedKeys);
    }

    [Fact]
    public void The_start_ticks_are_field_22_even_when_the_command_name_has_spaces_and_parentheses()
    {
        _proc.AddRaw(51, FakeProcRoot.Line(51, "my (odd) app", 'S', 51, 51, 424242));

        RunOwner owner = AtLaunch(("CLAUDE_PID", "51"));

        Assert.Equal(["pid:51@424242"], owner.Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("50x")]
    [InlineData("")]
    [InlineData("4999")]
    public void A_pid_that_is_not_a_live_process_gives_no_key(string pid)
    {
        RunOwner owner = AtLaunch(("CLAUDE_PID", pid));

        Assert.Empty(owner.Keys);
        Assert.Equal("none", owner.Source);
    }

    [Fact]
    public void A_zombie_is_no_pid_key()
    {
        _proc.Add(52, 1, state: 'Z');

        Assert.Empty(AtLaunch(("CLAUDE_PID", "52")).Keys);
    }

    [Fact]
    public void An_opencode_session_id_is_a_typed_key_and_the_runtime_is_opencode()
    {
        RunOwner owner = AtLaunch(("OPENCODE_SESSION_ID", "ses_abc"));

        Assert.Equal(["opencode-session:ses_abc"], owner.Keys);
        Assert.Equal("opencode", owner.Runtime);
        Assert.Equal("env", owner.Source);
    }

    [Fact]
    public void An_opencode_pid_alone_is_a_pid_key_with_its_start_ticks()
    {
        RunOwner owner = AtLaunch(("OPENCODE_PID", "50"));

        Assert.Equal([PidKey], owner.Keys);
        Assert.Equal("opencode", owner.Runtime);
    }

    [Fact]
    public void Claude_and_opencode_variables_together_give_every_key_and_claude_is_the_runtime()
    {
        RunOwner owner = AtLaunch(("OPENCODE_SESSION_ID", "ses_abc"), ("CLAUDE_CODE_SESSION_ID", "sess-1"));

        Assert.Equal([SessionKey, "opencode-session:ses_abc"], owner.Keys);
        Assert.Equal("claude", owner.Runtime);
    }

    [Fact]
    public void With_nothing_found_the_run_is_unowned()
    {
        RunOwner owner = AtLaunch(("PATH", "/usr/bin"));

        Assert.Empty(owner.Keys);
        Assert.Equal("none", owner.Source);
        Assert.Null(owner.Runtime);
        Assert.Null(owner.ParentRun);
    }

    [Fact]
    public void A_relay_name_is_for_display_and_never_a_key()
    {
        RunOwner owner = AtLaunch(("MSG_RELAY_NAME", "track-1a"));

        Assert.Empty(owner.Keys);
        Assert.Equal("track-1a", owner.RelayName);
    }

    [Theory]
    [InlineData("claude-session:abc-DEF_1.2@3/4", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("quo\"te", false)]
    [InlineData("café", false)]
    [InlineData("back\\slash", false)]
    [InlineData("star*", false)]
    public void The_key_check_allows_only_letters_digits_and_the_listed_marks(string key, bool valid) =>
        Assert.Equal(valid, OwnerKeys.Valid(key));

    // Control characters are built here, not written into the theory data (a test id must stay printable).
    private static readonly string[] _controlKeys =
    [
        "tab" + (char)9 + "here", "new" + (char)10 + "line", "nul" + (char)0 + "here", (char)27 + "[31mred", "del" + (char)127 + "here",
        "c1" + (char)0x85 + "here", "sep" + (char)0x2028 + "here",
    ];

    [Fact]
    public void A_control_character_or_an_escape_sequence_fails_the_key_check() =>
        Assert.All(_controlKeys, key => Assert.False(OwnerKeys.Valid(key)));

    [Fact]
    public void The_key_check_stops_at_200_characters()
    {
        Assert.True(OwnerKeys.Valid(new string('k', 200)));
        Assert.False(OwnerKeys.Valid(new string('k', 201)));
        const string? none = null;
        Assert.False(OwnerKeys.Valid(none));
    }

    [Fact]
    public void An_environment_key_that_fails_the_check_is_dropped_without_a_word()
    {
        foreach (string bad in _controlKeys.Append("has space"))
        {
            RunOwner owner = AtLaunch(("LEAN_WORKER_OWNER", bad), ("CLAUDE_CODE_SESSION_ID", bad), ("OPENCODE_SESSION_ID", bad), ("LEAN_WORKER_RUN_ID", bad));

            Assert.Empty(owner.Keys);
            Assert.Equal("none", owner.Source);
            Assert.Null(owner.ParentRun);
        }
    }

    [Fact]
    public void A_too_long_environment_key_is_dropped_and_the_next_rule_still_applies()
    {
        RunOwner owner = AtLaunch(("LEAN_WORKER_OWNER", new string('k', 201)), ("CLAUDE_CODE_SESSION_ID", "sess-1"));

        Assert.Equal([SessionKey], owner.Keys);
        Assert.Equal("env", owner.Source);
    }

    [Fact]
    public void A_session_id_that_makes_the_typed_key_too_long_is_dropped()
    {
        Assert.Empty(AtLaunch(("CLAUDE_CODE_SESSION_ID", new string('k', 200))).Keys);
        _ = Assert.Single(AtLaunch(("CLAUDE_CODE_SESSION_ID", new string('k', 200 - "claude-session:".Length))).Keys);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("has space")]
    [InlineData("")]
    public void A_run_id_that_is_no_run_dir_name_is_no_parent_run(string id)
    {
        RunOwner owner = AtLaunch(("LEAN_WORKER_RUN_ID", id), ("CLAUDE_CODE_SESSION_ID", "sess-1"));

        Assert.Null(owner.ParentRun);
        Assert.Equal([SessionKey], owner.Keys);
        Assert.Equal("env", owner.Source);
    }

    [Fact]
    public void The_caller_inside_a_worker_is_that_run_whatever_the_inherited_session_variables_say()
    {
        OwnerKeys.Caller caller = Caller(("LEAN_WORKER_RUN_ID", "20261122-115000-parent"), ("CLAUDE_CODE_SESSION_ID", "sess-1"), ("CLAUDE_PID", "50"));

        Assert.Equal(["run:20261122-115000-parent"], caller.Keys);
        Assert.Equal("run:20261122-115000-parent", caller.Key);
    }

    [Fact]
    public void The_caller_outside_a_worker_has_the_session_keys_and_the_session_key_is_the_one_to_pass()
    {
        OwnerKeys.Caller caller = Caller(("CLAUDE_PID", "50"), ("CLAUDE_CODE_SESSION_ID", "sess-1"), ("MSG_RELAY_NAME", "track-1a"));

        Assert.Equal([SessionKey, PidKey], caller.Keys);
        Assert.Equal(SessionKey, caller.Key);
        Assert.Equal("track-1a", caller.RelayName);
    }

    [Fact]
    public void The_pid_key_is_the_key_to_pass_when_there_is_no_session_id()
    {
        OwnerKeys.Caller caller = Caller(("OPENCODE_PID", "50"));

        Assert.Equal([PidKey], caller.Keys);
        Assert.Equal(PidKey, caller.Key);
    }

    [Fact]
    public void A_caller_with_no_variable_has_no_keys_and_no_key()
    {
        OwnerKeys.Caller caller = Caller();

        Assert.Empty(caller.Keys);
        Assert.Null(caller.Key);
    }

    [Theory]
    [InlineData("claude-session:0f3e9a12-aaaa", "cc:0f3e9a12")]
    [InlineData("claude-session:short", "cc:short")]
    [InlineData("opencode-session:ses_abcdefgh", "oc:ses_abcd")]
    [InlineData("pid:20480@7401233", "pid:20480")]
    [InlineData("run:20261122-115000-parent", "run:20261122-115000-parent")]
    [InlineData("custom-key", "custom-key")]
    public void The_short_form_is_what_the_header_and_the_owner_column_show(string key, string expected) =>
        Assert.Equal(expected, OwnerKeys.Short(key));
}
