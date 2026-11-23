using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The pointer a launch writes into the per-user index (docs/research/live-status/README.md, section 4.1), through the
/// writer's own seam (RunStatus.Open with a fake environment, /proc and clock): its name, fields and modes, that it is
/// written once and never rewritten or replaced, where the folder is, and that an index that cannot be written never
/// changes the run (one note, the status file still written).
/// </summary>
[Collection("launcher-process-state")]
public sealed class RunStatusPointerTests : IDisposable
{
    private const int LauncherPid = 4000;

    private static readonly DateTimeOffset _started = new(2026, 11, 22, 11, 55, 0, TimeSpan.Zero);

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly FakeClock _clock = new(_started.AddSeconds(2));
    private readonly string _state;

    public RunStatusPointerTests()
    {
        _proc.Add(LauncherPid, 7432110);
        _state = _dirs.Create("lw-ptr-state");
    }

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private static RunStatusSpec Spec(string runDir, int round = 1, string chain = "chain-1") =>
        new(runDir, "job", chain, round, Profile: null, "claude", "anthropic", "claude-haiku-4-5", 3m, WrapUpUsd: null, 60, _started, Worktree: null);

    private RunStatus? Open(string runDir, Func<string, string?> env, string? owner = null, int round = 1) =>
        RunStatus.Open(Spec(runDir, round), owner, env, _proc.Fs, _clock, LauncherPid, handleSignals: false);

    private Func<string, string?> StateEnv(params (string Name, string Value)[] more) =>
        StatusIndexRig.Env([("LEAN_WORKER_STATE_DIR", _state), .. more]);

    private string Index => Path.Combine(_state, "runs");

    private JsonObject PointerOf(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Index, name)))!.AsObject();

    [Fact]
    public void Open_writes_one_pointer_named_by_the_start_stamp_the_launcher_pid_and_the_round()
    {
        string runDir = _dirs.Create("lw-ptr-run");

        _ = Open(runDir, StateEnv(("CLAUDE_CODE_SESSION_ID", "sess-1")), round: 3);

        string name = Assert.Single(Directory.GetFiles(Index).Select(f => Path.GetFileName(f)));
        Assert.Equal("20261122-115500-4000-r3.json", name);
        Assert.True(StatusIndex.IsPointerName(name));
    }

    [Fact]
    public void The_pointer_has_the_schema_and_the_fields_of_the_design_with_the_launcher_identity_from_proc()
    {
        string runDir = _dirs.Create("lw-ptr-run");

        _ = Open(runDir, StateEnv(("CLAUDE_CODE_SESSION_ID", "sess-1"), ("MSG_RELAY_NAME", "track-1a")));

        JsonObject p = PointerOf("20261122-115500-4000-r1.json");
        Assert.Equal(["schema", "created_at", "run_dir", "chain_id", "parent_run", "owner_keys", "launcher"], [.. p.Select(kv => kv.Key)]);
        Assert.Equal("lean-worker.pointer/1", p["schema"]!.GetValue<string>());
        Assert.Equal(StatusDocs.Stamp(_started.AddSeconds(2)), p["created_at"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(runDir), p["run_dir"]!.GetValue<string>());
        Assert.Equal("chain-1", p["chain_id"]!.GetValue<string>());
        Assert.Null(p["parent_run"]);
        Assert.Equal(["claude-session:sess-1"], [.. p["owner_keys"]!.AsArray().Select(k => k!.GetValue<string>())]);
        JsonObject launcher = p["launcher"]!.AsObject();
        Assert.Equal(LauncherPid, launcher["pid"]!.GetValue<int>());
        Assert.Equal(7432110, launcher["start_ticks"]!.GetValue<long>());
        Assert.Equal(FakeProcRoot.DefaultBoot, launcher["boot_id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(launcher, StatusFileAssert.Read(runDir)["launcher"]));
    }

    [Fact]
    public void The_folder_is_mode_0700_and_the_file_mode_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string runDir = _dirs.Create("lw-ptr-run");

        _ = Open(runDir, StateEnv());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Index));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Index, "20261122-115500-4000-r1.json")));
    }

    [Fact]
    public void The_pointer_is_written_once_and_never_rewritten_by_later_status_writes()
    {
        string runDir = _dirs.Create("lw-ptr-run");
        string file = Path.Combine(Index, "20261122-115500-4000-r1.json");

        RunStatus status = Open(runDir, StateEnv())!;
        byte[] first = File.ReadAllBytes(file);
        DateTime written = File.GetLastWriteTimeUtc(file);
        _clock.Advance(TimeSpan.FromMinutes(5));
        status.WorkerStarted(StatusDocs.WorkerPid, pgid: StatusDocs.WorkerPid);
        status.Finishing();
        status.Finish("success", 0);

        Assert.Equal(first, File.ReadAllBytes(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        _ = Assert.Single(Directory.GetFileSystemEntries(Index));
    }

    [Fact]
    public void A_name_that_is_taken_gets_the_next_suffix_and_the_other_pointer_is_not_replaced()
    {
        string firstRun = _dirs.Create("lw-ptr-run-1");
        string secondRun = _dirs.Create("lw-ptr-run-2");

        _ = Open(firstRun, StateEnv());
        _ = Open(secondRun, StateEnv());

        string[] names = [.. Directory.GetFiles(Index).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];
        Assert.Equal(["20261122-115500-4000-r1-2.json", "20261122-115500-4000-r1.json"], names);
        Assert.Equal(Path.GetFullPath(firstRun), PointerOf("20261122-115500-4000-r1.json")["run_dir"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(secondRun), PointerOf("20261122-115500-4000-r1-2.json")["run_dir"]!.GetValue<string>());
    }

    [Fact]
    public void The_pointer_carries_the_owner_keys_and_the_parent_run_of_the_launch()
    {
        string runDir = _dirs.Create("lw-ptr-run");

        _ = Open(runDir, StateEnv(("LEAN_WORKER_RUN_ID", "20261122-110000-up"), ("CLAUDE_CODE_SESSION_ID", "sess-1")), owner: "my-key");

        JsonObject p = PointerOf("20261122-115500-4000-r1.json");
        Assert.Equal("20261122-110000-up", p["parent_run"]!.GetValue<string>());
        Assert.Equal(["my-key", "run:20261122-110000-up"], [.. p["owner_keys"]!.AsArray().Select(k => k!.GetValue<string>())]);
    }

    [Fact]
    public void LEAN_WORKER_STATE_DIR_wins_over_XDG_STATE_HOME_and_XDG_STATE_HOME_over_the_home_default()
    {
        string xdg = _dirs.Create("lw-ptr-xdg");
        string home = _dirs.Create("lw-ptr-home");

        _ = Open(_dirs.Create("lw-ptr-run-1"), StateEnv(("XDG_STATE_HOME", xdg), ("HOME", home)));
        _ = Open(_dirs.Create("lw-ptr-run-2"), StatusIndexRig.Env(("XDG_STATE_HOME", xdg), ("HOME", home)));
        _ = Open(_dirs.Create("lw-ptr-run-3"), StatusIndexRig.Env(("HOME", home)));

        _ = Assert.Single(Directory.GetFiles(Index));
        _ = Assert.Single(Directory.GetFiles(Path.Combine(xdg, "lean-worker", "runs")));
        _ = Assert.Single(Directory.GetFiles(Path.Combine(home, ".local", "state", "lean-worker", "runs")));
    }

    [Fact]
    public void An_index_that_cannot_be_written_leaves_one_note_and_the_status_file_is_still_written()
    {
        string runDir = _dirs.Create("lw-ptr-run");
        File.WriteAllText(Index, "a file where the index folder should be");

        RunStatus status = Open(runDir, StateEnv())!;
        status.Finishing();
        status.Finish("success", 0);

        JsonObject doc = StatusFileAssert.Read(runDir);
        Assert.Equal("done", doc["phase"]!.GetValue<string>());
        Assert.Equal("success", doc["end"]!["status"]!.GetValue<string>());
        List<string> notes = [];
        status.AddNoteTo(notes);
        status.AddNoteTo(notes);
        string note = Assert.Single(notes);
        Assert.Contains("run index pointer", note, StringComparison.Ordinal);
        Assert.Contains("the run itself is not affected", note, StringComparison.Ordinal);
        Assert.Equal("a file where the index folder should be", File.ReadAllText(Index));
    }

    [Fact]
    public void Without_any_folder_the_run_gets_the_same_one_note_and_goes_on()
    {
        string runDir = _dirs.Create("lw-ptr-run");

        RunStatus status = Open(runDir, StatusIndexRig.Env(("PATH", "/usr/bin")))!;

        Assert.Equal("starting", StatusFileAssert.Read(runDir)["phase"]!.GetValue<string>());
        List<string> notes = [];
        status.AddNoteTo(notes);
        Assert.Contains("run index pointer", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_working_index_leaves_no_note()
    {
        RunStatus status = Open(_dirs.Create("lw-ptr-run"), StateEnv())!;
        List<string> notes = [];

        status.AddNoteTo(notes);

        Assert.Empty(notes);
    }

    [Fact]
    public void With_the_writer_off_no_pointer_is_written_and_the_folder_is_not_made()
    {
        RunStatus? status = Open(_dirs.Create("lw-ptr-run"), StateEnv(("LEAN_WORKER_STATUS", "off")));

        Assert.Null(status);
        Assert.False(Directory.Exists(Index));
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData(" off ")]
    [InlineData("Off")]
    public void The_kill_switch_ignores_case_and_surrounding_space(string value)
    {
        Assert.Null(Open(_dirs.Create("lw-ptr-run"), StateEnv(("LEAN_WORKER_STATUS", value))));
        Assert.False(Directory.Exists(Index));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("offf")]
    public void Any_other_kill_switch_value_leaves_the_writer_on(string value)
    {
        Assert.NotNull(Open(_dirs.Create("lw-ptr-run"), StateEnv(("LEAN_WORKER_STATUS", value))));
        _ = Assert.Single(Directory.GetFiles(Index));
    }

    [Fact]
    public void The_status_file_records_the_owner_object_and_the_parent_run()
    {
        string runDir = _dirs.Create("lw-ptr-run");

        _ = Open(runDir, StateEnv(("LEAN_WORKER_RUN_ID", "20261122-110000-up"), ("CLAUDE_CODE_SESSION_ID", "sess-1"), ("MSG_RELAY_NAME", "track-1a")));

        JsonObject doc = StatusFileAssert.Read(runDir);
        JsonObject owner = doc["owner"]!.AsObject();
        Assert.Equal(["keys", "inherited_keys", "source", "runtime", "relay_name"], [.. owner.Select(kv => kv.Key)]);
        Assert.Equal(["run:20261122-110000-up"], [.. owner["keys"]!.AsArray().Select(k => k!.GetValue<string>())]);
        Assert.Equal(["claude-session:sess-1"], [.. owner["inherited_keys"]!.AsArray().Select(k => k!.GetValue<string>())]);
        Assert.Equal("parent-run", owner["source"]!.GetValue<string>());
        Assert.Equal("claude", owner["runtime"]!.GetValue<string>());
        Assert.Equal("track-1a", owner["relay_name"]!.GetValue<string>());
        Assert.Equal("20261122-110000-up", doc["run"]!["parent_run"]!.GetValue<string>());
    }
}
