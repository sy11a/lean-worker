using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The index pointer in whole launches (Launcher.RunAsync against a stub worker, the index in a folder of the test):
/// where it is written, its name, fields and modes, that it is written once, that a writer that is off or an index
/// that cannot be written leaves the run exactly as it was, and what a gate chain and a continuation leave in it.
/// Process-global state (PATH, the environment, the working directory) is why this class sits in the launcher collection.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class RunIndexLaunchTests : IDisposable
{
    private const string PointerNote = "run index pointer";

    private readonly TempDirs _dirs = new();
    private readonly EnvScope _env = EnvScope.Clean();

    public void Dispose()
    {
        _env.Dispose();
        _dirs.Dispose();
    }

    private sealed record Launched(int Code, string Stdout, string Root, string State)
    {
        public string Index => Path.Combine(State, "runs");

        public string RunDir => RunAsyncGolden.RunDirFrom(Stdout);

        public JsonObject Status => StatusFileAssert.Read(RunDir);

        public string[] PointerFiles => Directory.Exists(Index) ? [.. Directory.GetFiles(Index).Order(StringComparer.Ordinal)] : [];
    }

    private async Task<Launched> LaunchAsync(string stream = RunAsyncGolden.SuccessStream, Action<Options>? configure = null,
        (string Name, string? Value)[]? environment = null, string? root = null, string? state = null)
    {
        string runsRoot = root ?? _dirs.NewRoot();
        string stateDir = state ?? _dirs.Create("lw-launch-state");
        (int code, string stdout) = await RunAsyncGolden.RunAsync(runsRoot, stream, configure, stateDir: stateDir, environment: environment);
        return new Launched(code, stdout, runsRoot, stateDir);
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(k => k!.GetValue<string>())];

    [GeneratedRegex(@"^\d{8}-\d{6}-\d+-r\d+\.json$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex PointerName();

    [Fact]
    public async Task A_launch_writes_one_pointer_named_by_the_start_stamp_the_launcher_pid_and_the_round_with_the_fields_of_the_designAsync()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Launched run = await LaunchAsync();

        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);
        string file = Assert.Single(run.PointerFiles);
        string name = Path.GetFileName(file);
        string id = Path.GetFileName(run.RunDir);
        Assert.Matches(PointerName(), name);
        Assert.Equal(id[..15] + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-r1.json", name);
        JsonObject pointer = Read(file);
        JsonObject status = run.Status;
        Assert.Equal(["schema", "created_at", "run_dir", "chain_id", "parent_run", "owner_keys", "launcher"], [.. pointer.Select(kv => kv.Key)]);
        Assert.Equal("lean-worker.pointer/1", pointer["schema"]!.GetValue<string>());
        Assert.InRange(DateTimeOffset.Parse(pointer["created_at"]!.GetValue<string>(), CultureInfo.InvariantCulture), before, after);
        Assert.Equal(Path.GetFullPath(run.RunDir), pointer["run_dir"]!.GetValue<string>());
        Assert.Equal(id, pointer["chain_id"]!.GetValue<string>());
        Assert.Null(pointer["parent_run"]);
        Assert.Empty(Strings(pointer["owner_keys"]));
        Assert.True(JsonNode.DeepEquals(status["launcher"], pointer["launcher"]));
        Assert.Equal(Environment.ProcessId, pointer["launcher"]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_index_folder_is_mode_0700_and_the_pointer_0600Async()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Launched run = await LaunchAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(run.Index));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Assert.Single(run.PointerFiles)));
    }

    [Fact]
    public async Task The_pointer_is_written_before_the_worker_starts_and_is_not_rewritten_afterwardsAsync()
    {
        string capture = Path.Combine(_dirs.Create("lw-launch-capture"), "pointer-at-worker-start.json");
        string script = RunAsyncGolden.Worker("cat \"$LEAN_WORKER_STATE_DIR\"/runs/*.json > '" + capture + "'");

        Launched run = await LaunchAsync(script);

        Assert.Equal(0, run.Code);
        Assert.True(File.Exists(capture));
        byte[] atStart = await File.ReadAllBytesAsync(capture, TestContext.Current.CancellationToken);
        byte[] atEnd = await File.ReadAllBytesAsync(Assert.Single(run.PointerFiles), TestContext.Current.CancellationToken);
        Assert.Equal(atStart, atEnd);
    }

    [Fact]
    public async Task A_launch_with_the_writer_off_writes_no_pointer_makes_no_index_folder_and_no_status_fileAsync()
    {
        Launched run = await LaunchAsync(environment: [("LEAN_WORKER_STATUS", "off")]);

        Assert.Equal(0, run.Code);
        Assert.False(Directory.Exists(run.Index));
        Assert.Empty(Directory.GetFileSystemEntries(run.State));
        Assert.False(File.Exists(Path.Combine(run.RunDir, "status.json")));
    }

    [Fact]
    public async Task An_index_that_cannot_be_written_changes_nothing_of_the_run_but_leaves_one_noteAsync()
    {
        string blockedState = _dirs.Create("lw-launch-blocked");
        await File.WriteAllTextAsync(Path.Combine(blockedState, "runs"), "a file where the index folder should be", TestContext.Current.CancellationToken);

        Launched off = await LaunchAsync(environment: [("LEAN_WORKER_STATUS", "off")]);
        Launched blocked = await LaunchAsync(state: blockedState);

        Assert.Equal(off.Code, blocked.Code);
        Assert.Equal(LaunchCompare.Block(off.Root, off.Stdout), LaunchCompare.Block(blocked.Root, blocked.Stdout, PointerNote));
        JsonObject offSummary = RunAsyncGolden.Summary(off.RunDir);
        JsonObject blockedSummary = RunAsyncGolden.Summary(blocked.RunDir);
        Assert.Equal(LaunchCompare.Comparable(off.Root, offSummary), LaunchCompare.Comparable(blocked.Root, blockedSummary, PointerNote));
        Assert.Equal(LaunchCompare.Comparable(off.Root, LedgerRow(off.Root)), LaunchCompare.Comparable(blocked.Root, LedgerRow(blocked.Root), PointerNote));
        string note = Assert.Single(RunAsyncGolden.Notes_(blockedSummary), n => n.Contains(PointerNote, StringComparison.Ordinal));
        Assert.Contains("the run itself is not affected", note, StringComparison.Ordinal);
        Assert.DoesNotContain(RunAsyncGolden.Notes_(offSummary), n => n.Contains(PointerNote, StringComparison.Ordinal));
        Assert.Equal("done", blocked.Status["phase"]!.GetValue<string>());
        Assert.Equal("a file where the index folder should be", await File.ReadAllTextAsync(Path.Combine(blockedState, "runs"), TestContext.Current.CancellationToken));
    }

    private static JsonObject LedgerRow(string root) =>
        JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(root, "runs.jsonl"))))!.AsObject();

    [Fact]
    public async Task Without_LEAN_WORKER_STATE_DIR_the_index_is_under_XDG_STATE_HOMEAsync()
    {
        string xdg = _dirs.Create("lw-launch-xdg");

        Launched run = await LaunchAsync(environment: [("LEAN_WORKER_STATE_DIR", null), ("XDG_STATE_HOME", xdg)]);

        string file = Assert.Single(Directory.GetFiles(Path.Combine(xdg, "lean-worker", "runs")));
        Assert.True(StatusIndex.IsPointerName(Path.GetFileName(file)));
        Assert.False(Directory.Exists(run.Index));
    }

    [Fact]
    public async Task Without_either_variable_the_index_is_under_the_home_directoryAsync()
    {
        string home = _dirs.Create("lw-launch-home");

        Launched run = await LaunchAsync(environment: [("LEAN_WORKER_STATE_DIR", null), ("XDG_STATE_HOME", null), ("HOME", home)]);

        string file = Assert.Single(Directory.GetFiles(Path.Combine(home, ".local", "state", "lean-worker", "runs")));
        Assert.True(StatusIndex.IsPointerName(Path.GetFileName(file)));
        Assert.False(Directory.Exists(run.Index));
    }

    [Fact]
    public async Task A_gate_chain_leaves_one_pointer_per_round_with_the_chain_id_of_the_first_and_the_run_dirs_it_always_hadAsync()
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-launch-gate");
        string script = await GateLoopTests.WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (0, 0));
        RunAsyncGolden.WriteProfile(root, "test", GateLoopTests.GateProfile(script));

        Launched run = await LaunchAsync(root: root);

        Assert.Equal(0, run.Code);
        string[] dirs = GateLoopTests.RunDirNames(root);
        Assert.Equal(3, dirs.Length);
        string first = dirs.Single(n => n.EndsWith("-golden", StringComparison.Ordinal));
        string second = dirs.Single(n => n.EndsWith("-golden-gate2", StringComparison.Ordinal));
        string third = dirs.Single(n => n.EndsWith("-golden-gate3", StringComparison.Ordinal));
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string[] pointers = [.. run.PointerFiles.Select(f => Path.GetFileName(f))];
        Assert.Equal(3, pointers.Length);
        Assert.Equal(["-r1.json", "-r2.json", "-r3.json"], [.. pointers.Select(p => p[p.LastIndexOf('-')..]).Order(StringComparer.Ordinal)]);
        Assert.All(pointers, p => Assert.Contains("-" + pid + "-", p, StringComparison.Ordinal));
        foreach (string file in run.PointerFiles)
        {
            Assert.Equal(first, Read(file)["chain_id"]!.GetValue<string>());
        }

        string[] runDirs = [.. run.PointerFiles.Select(f => Path.GetFileName(Read(f)["run_dir"]!.GetValue<string>())).Order(StringComparer.Ordinal)];
        Assert.Equal([.. new[] { first, second, third }.Order(StringComparer.Ordinal)], runDirs);
        JsonObject secondStatus = StatusFileAssert.Read(Path.Combine(root, "runs", second));
        Assert.Equal(2, secondStatus["run"]!["round"]!.GetValue<int>());
        Assert.Equal(first, secondStatus["run"]!["chain_id"]!.GetValue<string>());
        Assert.True(StatusFileAssert.Read(Path.Combine(root, "runs", first))["live"]!["gate_max_rounds"]!.GetValue<int>() >= 3);
    }

    [Fact]
    public async Task A_continuation_gets_a_run_dir_and_a_pointer_of_its_own_and_the_run_it_continues_is_untouchedAsync()
    {
        Launched first = await LaunchAsync();
        string[] before = StatusIndexRig.Snapshot(first.RunDir);

        Launched second = await LaunchAsync(configure: o =>
        {
            o.TaskFile = null;
            o.Name = "cont-run";
            o.ContinueFrom = first.RunDir;
        }, root: first.Root, state: first.State);

        Assert.Equal(0, second.Code);
        Assert.Matches(@"^\d{8}-\d{6}-cont-run$", Path.GetFileName(second.RunDir));
        Assert.NotEqual(first.RunDir, second.RunDir, StringComparer.Ordinal);
        Assert.Equal(2, second.PointerFiles.Length);
        Assert.Equal(before, StatusIndexRig.Snapshot(first.RunDir));
        Assert.Equal(Path.GetFileName(second.RunDir), second.Status["run"]!["chain_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_dir_name_that_is_taken_gets_the_launcher_pid_as_a_suffix_and_the_other_dirs_stay_as_they_wereAsync()
    {
        string root = _dirs.NewRoot();
        string[] taken = TakeStamps(root, withPid: false);

        Launched run = await LaunchAsync(root: root);

        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string id = Path.GetFileName(run.RunDir);
        Assert.Matches(@"^\d{8}-\d{6}-golden-" + pid + "$", id);
        Assert.True(File.Exists(Path.Combine(run.RunDir, "summary.json")));
        Assert.All(taken, d => Assert.Empty(Directory.GetFileSystemEntries(d)));
        Assert.Equal(id, run.Status["run"]!["id"]!.GetValue<string>());
        Assert.Equal(id, run.Status["run"]!["chain_id"]!.GetValue<string>());
        Assert.Equal(id[..15] + "-" + pid + "-r1.json", Path.GetFileName(Assert.Single(run.PointerFiles)));
    }

    [Fact]
    public async Task A_run_dir_name_that_is_taken_with_its_pid_suffix_too_is_a_launch_error_and_leaves_no_pointerAsync()
    {
        string root = _dirs.NewRoot();
        string[] taken = TakeStamps(root, withPid: true);
        string state = _dirs.Create("lw-launch-state");

        _ = await Assert.ThrowsAsync<LaunchException>(async () => await LaunchAsync(root: root, state: state));

        Assert.All(taken, d => Assert.Empty(Directory.GetFileSystemEntries(d)));
        Assert.False(Directory.Exists(Path.Combine(state, "runs")));
    }

    // Empty run dirs named like the launch's own for each of the next seconds (with the pid suffix too when asked).
    private static string[] TakeStamps(string root, bool withPid)
    {
        List<string> made = [];
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        for (int s = -1; s < 12; s++)
        {
            string stem = Path.Combine(root, "runs", DateTimeOffset.Now.AddSeconds(s).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-golden");
            foreach (string path in withPid ? new[] { stem, stem + "-" + pid } : [stem])
            {
                made.Add(Directory.CreateDirectory(path).FullName);
            }
        }

        return [.. made];
    }

    [Fact]
    public async Task The_owner_is_captured_from_the_session_variables_and_recorded_in_the_status_file_and_the_pointerAsync()
    {
        Launched run = await LaunchAsync(environment: [("CLAUDE_CODE_SESSION_ID", "sess-L"), ("MSG_RELAY_NAME", "track-L")]);

        JsonObject owner = run.Status["owner"]!.AsObject();
        Assert.Equal(["claude-session:sess-L"], Strings(owner["keys"]));
        Assert.Empty(Strings(owner["inherited_keys"]));
        Assert.Equal("env", owner["source"]!.GetValue<string>());
        Assert.Equal("claude", owner["runtime"]!.GetValue<string>());
        Assert.Equal("track-L", owner["relay_name"]!.GetValue<string>());
        Assert.Null(run.Status["run"]!["parent_run"]);
        Assert.Equal(["claude-session:sess-L"], Strings(Read(Assert.Single(run.PointerFiles))["owner_keys"]));
    }

    [Fact]
    public async Task A_pid_key_has_the_start_ticks_of_that_process_from_the_real_procAsync()
    {
        int pid = Environment.ProcessId;
        ProcProbe probe = ProcFs.Host.Stat(pid);
        Assert.SkipUnless(probe.Kind is ProbeKind.Present, "/proc is not readable here");

        Launched run = await LaunchAsync(environment: [("CLAUDE_PID", pid.ToString(CultureInfo.InvariantCulture))]);

        Assert.Equal(["pid:" + pid.ToString(CultureInfo.InvariantCulture) + "@" + probe.Stat.StartTicks.ToString(CultureInfo.InvariantCulture)], Strings(run.Status["owner"]!["keys"]));
    }

    [Fact]
    public async Task The_owner_option_wins_over_the_environment_and_every_key_found_is_still_recordedAsync()
    {
        Launched run = await LaunchAsync(configure: o => o.Owner = "my-track", environment: [("LEAN_WORKER_OWNER", "wrapper"), ("CLAUDE_CODE_SESSION_ID", "sess-L")]);

        JsonObject owner = run.Status["owner"]!.AsObject();
        Assert.Equal("option", owner["source"]!.GetValue<string>());
        Assert.Equal(["my-track", "wrapper", "claude-session:sess-L"], Strings(owner["keys"]));
        Assert.Equal(["my-track", "wrapper", "claude-session:sess-L"], Strings(Read(Assert.Single(run.PointerFiles))["owner_keys"]));
    }

    [Fact]
    public async Task LEAN_WORKER_OWNER_alone_sets_the_owner_and_names_its_sourceAsync()
    {
        Launched run = await LaunchAsync(environment: [("LEAN_WORKER_OWNER", "wrapper")]);

        JsonObject owner = run.Status["owner"]!.AsObject();
        Assert.Equal("owner-env", owner["source"]!.GetValue<string>());
        Assert.Equal(["wrapper"], Strings(owner["keys"]));
    }

    [Fact]
    public async Task A_launcher_inside_another_run_is_owned_by_that_run_and_the_session_keys_are_only_recordedAsync()
    {
        Launched run = await LaunchAsync(environment: [("LEAN_WORKER_RUN_ID", "20261122-110000-up"), ("CLAUDE_CODE_SESSION_ID", "sess-L")]);

        JsonObject status = run.Status;
        JsonObject owner = status["owner"]!.AsObject();
        Assert.Equal("parent-run", owner["source"]!.GetValue<string>());
        Assert.Equal(["run:20261122-110000-up"], Strings(owner["keys"]));
        Assert.Equal(["claude-session:sess-L"], Strings(owner["inherited_keys"]));
        Assert.Equal("20261122-110000-up", status["run"]!["parent_run"]!.GetValue<string>());
        JsonObject pointer = Read(Assert.Single(run.PointerFiles));
        Assert.Equal("20261122-110000-up", pointer["parent_run"]!.GetValue<string>());
        Assert.Equal(["run:20261122-110000-up"], Strings(pointer["owner_keys"]));
    }

    [Fact]
    public async Task The_claude_worker_gets_its_run_id_and_run_dir_in_its_environmentAsync()
    {
        string capture = Path.Combine(_dirs.Create("lw-launch-capture"), "env.txt");
        string script = RunAsyncGolden.Worker("printf '%s\\n%s\\n' \"$LEAN_WORKER_RUN_ID\" \"$LEAN_WORKER_RUN_DIR\" > '" + capture + "'");

        Launched run = await LaunchAsync(script);

        string[] lines = await File.ReadAllLinesAsync(capture, TestContext.Current.CancellationToken);
        Assert.Equal([Path.GetFileName(run.RunDir), Path.GetFullPath(run.RunDir)], lines);
    }

    [Fact]
    public async Task The_opencode_worker_gets_its_run_id_and_run_dir_in_its_environmentAsync()
    {
        string root = _dirs.NewRoot();
        string capture = Path.Combine(_dirs.Create("lw-launch-capture"), "env.txt");
        string script = "printf '%s\\n%s\\n' \"$LEAN_WORKER_RUN_ID\" \"$LEAN_WORKER_RUN_DIR\" > '" + capture + "'\ncat >/dev/null\nexit 0\n";
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, script, o =>
        {
            o.Runtime = "opencode";
            o.Mode = "auto";
        }, stateDir: _dirs.Create("lw-launch-state"), stubName: "opencode");

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        string[] lines = await File.ReadAllLinesAsync(capture, TestContext.Current.CancellationToken);
        Assert.Equal([Path.GetFileName(runDir), Path.GetFullPath(runDir)], lines);
    }
}
