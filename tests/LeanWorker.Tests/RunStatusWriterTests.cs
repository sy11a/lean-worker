using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The writer of status.json (docs/research/live-status/README.md, section 4.1): what the file holds
/// after each phase, the identities it records from /proc, the write discipline (one lock, seq inside
/// it, temp plus rename), the refresh timer and best effort. Driven with a fake /proc and a fake clock.
/// </summary>
public sealed class RunStatusWriterTests : IDisposable
{
    private const int LauncherPid = 4000;
    private const long LauncherTicks = 7432110;
    private const int WorkerPid = 4010;
    private const long WorkerTicks = 7432151;

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new("5c2f0a4e-test-boot");
    private readonly FakeClock _clock = new(StatusDocs.Now);
    private readonly string _runDir;

    public RunStatusWriterTests()
    {
        _runDir = Path.Combine(_dirs.Create("lw-status"), "20261122-135850-fix-save-w1");
        _ = Directory.CreateDirectory(_runDir);
        _proc.Add(LauncherPid, LauncherTicks, pgrp: 3990, session: 3990, comm: "lean-worker");
        _proc.Add(WorkerPid, WorkerTicks);
    }

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private static RunStatusSpec Spec(string runDir, DateTimeOffset? started = null)
    {
        return new RunStatusSpec(runDir, "fix-save-w1", "20261122-135850-fix-save-w1", 1,
            "code-minimax", "opencode", "minimax-coding-plan", "MiniMax-M3", 3.0m, 2.4m, 60, started ?? StatusDocs.Now.AddMinutes(-1), Worktree: null);
    }

    private RunStatus Writer(DateTimeOffset? started = null) => new(Spec(_runDir, started), _proc.Fs, _clock, LauncherPid);

    private JsonObject Disk() => StatusFileAssert.Read(_runDir);

    private string[] Entries() => [.. Directory.GetFileSystemEntries(_runDir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Nothing_is_written_until_the_first_phase_is_reported()
    {
        _ = Writer();

        Assert.Empty(Entries());
    }

    [Fact]
    public void The_starting_phase_records_the_run_the_spec_and_the_launcher()
    {
        DateTimeOffset started = new(2026, 11, 22, 14, 58, 50, 104, TimeSpan.FromHours(2));
        RunStatus status = Writer(started);

        status.Phase(RunPhase.Starting);

        JsonObject doc = Disk();
        Assert.Equal("lean-worker.status/1", doc["schema"]!.GetValue<string>());
        Assert.Equal(1, doc["seq"]!.GetValue<long>());
        Assert.Equal("starting", doc["phase"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:00.000Z", doc["updated_at"]!.GetValue<string>());
        JsonObject run = doc["run"]!.AsObject();
        Assert.Equal("20261122-135850-fix-save-w1", run["id"]!.GetValue<string>());
        Assert.Equal(_runDir, run["dir"]!.GetValue<string>());
        Assert.Equal("fix-save-w1", run["name"]!.GetValue<string>());
        Assert.Equal("20261122-135850-fix-save-w1", run["chain_id"]!.GetValue<string>());
        Assert.Equal(1, run["round"]!.GetValue<int>());
        Assert.Null(run["parent_run"]);
        Assert.Null(doc["owner"]);
        Assert.Null(doc["worker"]);
        Assert.Null(doc["end"]);
        JsonObject spec = doc["spec"]!.AsObject();
        Assert.Equal("code-minimax", spec["profile"]!.GetValue<string>());
        Assert.Equal("opencode", spec["runtime"]!.GetValue<string>());
        Assert.Equal("minimax-coding-plan", spec["provider"]!.GetValue<string>());
        Assert.Equal("MiniMax-M3", spec["model"]!.GetValue<string>());
        Assert.Equal(3.0m, spec["budget_usd"]!.GetValue<decimal>());
        Assert.Equal(2.4m, spec["wrap_up_usd"]!.GetValue<decimal>());
        Assert.Equal(60, spec["timeout_minutes"]!.GetValue<int>());
        Assert.Equal("2026-11-22T12:58:50.104Z", spec["started_at"]!.GetValue<string>());
        Assert.Equal("2026-11-22T13:58:50.104Z", spec["deadline_at"]!.GetValue<string>());
        Assert.Null(spec["worktree"]);
    }

    [Fact]
    public void The_launcher_identity_is_its_pid_start_ticks_and_the_boot_id_read_from_proc()
    {
        RunStatus status = Writer();

        status.Phase(RunPhase.Starting);

        JsonObject launcher = Disk()["launcher"]!.AsObject();
        Assert.Equal(LauncherPid, launcher["pid"]!.GetValue<int>());
        Assert.Equal(LauncherTicks, launcher["start_ticks"]!.GetValue<long>());
        Assert.Equal("5c2f0a4e-test-boot", launcher["boot_id"]!.GetValue<string>());
    }

    [Fact]
    public void A_launcher_the_proc_does_not_show_has_no_start_ticks()
    {
        _proc.Remove(LauncherPid);
        RunStatus status = Writer();

        status.Phase(RunPhase.Starting);

        JsonObject launcher = Disk()["launcher"]!.AsObject();
        Assert.Equal(LauncherPid, launcher["pid"]!.GetValue<int>());
        Assert.Null(launcher["start_ticks"]);
    }

    [Fact]
    public void The_worker_identity_is_its_pid_the_group_the_launcher_resolved_and_the_session_and_start_ticks_from_proc()
    {
        using FakeProcRoot proc = new();
        proc.Add(LauncherPid, LauncherTicks);
        proc.Add(WorkerPid, WorkerTicks, pgrp: WorkerPid, session: 4005);
        RunStatus status = new(Spec(_runDir), proc.Fs, _clock, LauncherPid);

        status.WorkerStarted(WorkerPid, WorkerPid);

        JsonObject doc = Disk();
        Assert.Equal("worker", doc["phase"]!.GetValue<string>());
        JsonObject worker = doc["worker"]!.AsObject();
        Assert.Equal(WorkerPid, worker["pid"]!.GetValue<int>());
        Assert.Equal(WorkerPid, worker["pgid"]!.GetValue<int>());
        Assert.Equal(4005, worker["sid"]!.GetValue<int>());
        Assert.Equal(WorkerTicks, worker["start_ticks"]!.GetValue<long>());
    }

    [Fact]
    public void A_worker_group_the_launcher_resolved_gives_its_group_id()
    {
        WorkerGroup group = new() { Available = true };
        group.Resolved(WorkerPid);
        RunStatus status = Writer();

        status.WorkerStarted(WorkerPid, group);

        JsonObject worker = Disk()["worker"]!.AsObject();
        Assert.Equal(WorkerPid, worker["pgid"]!.GetValue<int>());
        Assert.Equal(WorkerPid, worker["sid"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, WorkerPid)]
    [InlineData(true, 0)]
    public void Without_a_group_the_worker_has_no_pgid_and_no_sid_but_keeps_pid_and_start_ticks(bool available, int groupId)
    {
        WorkerGroup group = new() { Available = available };
        group.Resolved(groupId);
        RunStatus status = Writer();

        status.WorkerStarted(WorkerPid, group);

        JsonObject worker = Disk()["worker"]!.AsObject();
        Assert.Equal(WorkerPid, worker["pid"]!.GetValue<int>());
        Assert.Null(worker["pgid"]);
        Assert.Null(worker["sid"]);
        Assert.Equal(WorkerTicks, worker["start_ticks"]!.GetValue<long>());
    }

    [Fact]
    public void A_worker_the_proc_does_not_show_has_no_start_ticks_and_no_sid()
    {
        _proc.Remove(WorkerPid);
        RunStatus status = Writer();

        status.WorkerStarted(WorkerPid, WorkerPid);

        JsonObject worker = Disk()["worker"]!.AsObject();
        Assert.Equal(WorkerPid, worker["pgid"]!.GetValue<int>());
        Assert.Null(worker["sid"]);
        Assert.Null(worker["start_ticks"]);
    }

    [Fact]
    public void The_gate_phase_records_its_round_and_leaving_it_clears_the_round()
    {
        RunStatus status = Writer();
        status.WorkerStarted(WorkerPid, WorkerPid);

        status.GateStarted(2);
        JsonObject gate = Disk();
        status.Finishing();
        JsonObject finishing = Disk();

        Assert.Equal("gate", gate["phase"]!.GetValue<string>());
        Assert.Equal(2, gate["live"]!["gate_round"]!.GetValue<int>());
        Assert.Equal("finishing", finishing["phase"]!.GetValue<string>());
        Assert.Null(finishing["live"]!["gate_round"]);
        Assert.NotNull(finishing["worker"]);
    }

    [Fact]
    public void Done_records_the_end_with_the_status_exit_code_time_and_summary()
    {
        RunStatus status = Writer();
        status.WorkerStarted(WorkerPid, WorkerPid);
        _clock.Advance(TimeSpan.FromSeconds(90));

        status.Finish("wrapped-up", 3);

        JsonObject doc = Disk();
        Assert.Equal("done", doc["phase"]!.GetValue<string>());
        JsonObject end = doc["end"]!.AsObject();
        Assert.Equal("wrapped-up", end["status"]!.GetValue<string>());
        Assert.Equal(3, end["exit_code"]!.GetValue<int>());
        Assert.Equal("2026-11-22T12:01:30.000Z", end["at"]!.GetValue<string>());
        Assert.Equal("summary.json", end["summary"]!.GetValue<string>());
    }

    [Fact]
    public void A_launch_failure_before_the_summary_records_launch_failed_with_the_message()
    {
        RunStatus status = Writer();
        status.WorkerStarted(WorkerPid, WorkerPid);

        status.Fail("could not start claude", summaryStatus: null);

        JsonObject doc = Disk();
        Assert.Equal("failed", doc["phase"]!.GetValue<string>());
        JsonObject end = doc["end"]!.AsObject();
        Assert.Equal("launch-failed", end["status"]!.GetValue<string>());
        Assert.Equal("could not start claude", end["error"]!.GetValue<string>());
        Assert.False(end["after_summary"]!.GetValue<bool>());
        Assert.Null(end["summary_status"]);
        Assert.Null(end["exit_code"]);
    }

    [Fact]
    public void A_launch_failure_after_the_summary_records_that_and_the_summary_status()
    {
        RunStatus status = Writer();

        status.Fail("ledger append failed", summaryStatus: "success");

        JsonObject end = Disk()["end"]!.AsObject();
        Assert.Equal("launch-failed", end["status"]!.GetValue<string>());
        Assert.True(end["after_summary"]!.GetValue<bool>());
        Assert.Equal("success", end["summary_status"]!.GetValue<string>());
    }

    [Fact]
    public void Every_timestamp_is_UTC_with_a_Z_whatever_offset_the_input_had()
    {
        DateTimeOffset started = new(2026, 11, 22, 14, 58, 50, 104, TimeSpan.FromHours(2));
        RunStatus status = Writer(started);
        status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: false);
        status.Finish("success", 0);

        JsonObject doc = Disk();

        string[] stamps =
        [
            doc["updated_at"]!.GetValue<string>(),
            doc["spec"]!["started_at"]!.GetValue<string>(),
            doc["spec"]!["deadline_at"]!.GetValue<string>(),
            doc["live"]!["last_event_at"]!.GetValue<string>(),
            doc["live"]!["last_tool"]!["at"]!.GetValue<string>(),
            doc["end"]!["at"]!.GetValue<string>(),
        ];
        Assert.All(stamps, s => Assert.True(s.EndsWith('Z') && s.Length is 24 && s[10] is 'T' && s[19] is '.', s));
        Assert.Equal("2026-11-22T12:58:50.104Z", stamps[1]);
    }

    [Fact]
    public void Elapsed_time_comes_from_the_monotonic_clock_since_the_writer_was_made()
    {
        RunStatus status = Writer();
        _clock.Advance(TimeSpan.FromMilliseconds(221_304));

        status.Phase(RunPhase.Starting);

        Assert.Equal(221_304, Disk()["live"]!["elapsed_ms"]!.GetValue<long>());
    }

    [Fact]
    public void Seq_grows_by_one_with_every_write_and_the_file_carries_the_last()
    {
        RunStatus status = Writer();
        List<long> seen = [status.Seq];

        status.Phase(RunPhase.Starting);
        seen.Add(Disk()["seq"]!.GetValue<long>());
        status.WorkerStarted(WorkerPid, WorkerPid);
        seen.Add(Disk()["seq"]!.GetValue<long>());
        status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: false);
        seen.Add(Disk()["seq"]!.GetValue<long>());
        status.GateStarted(1);
        seen.Add(Disk()["seq"]!.GetValue<long>());
        status.Finishing();
        seen.Add(Disk()["seq"]!.GetValue<long>());
        status.Finish("success", 0);
        seen.Add(Disk()["seq"]!.GetValue<long>());

        Assert.Equal([0L, 1L, 2L, 3L, 4L, 5L, 6L], seen);
        Assert.Equal(6, status.Seq);
    }

    [Fact]
    public void Repeated_writes_leave_only_status_json_in_the_run_dir()
    {
        RunStatus status = Writer();

        for (int i = 0; i < 25; i++)
        {
            status.Phase(RunPhase.Worker);
        }

        Assert.Equal(["status.json"], Entries());
    }

    /// <summary>
    /// One writer thread: alternately a phase change and a tool start, each of which is one write.
    /// </summary>
    private static void Hammer(RunStatus status, int writer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (i % 2 is 0)
            {
                status.Phase(i % 4 is 0 ? RunPhase.Gate : RunPhase.Worker, i);
                continue;
            }

            status.OnLine(StreamActivity.ToolStart("Bash", string.Create(CultureInfo.InvariantCulture, $"c{writer}-{i}")), usage: false);
        }
    }

    [Fact]
    public async Task Parallel_lines_and_phases_leave_a_file_that_always_parses_and_a_seq_that_never_goes_backwardsAsync()
    {
        const int Writers = 8;
        const int PerWriter = 120;
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);
        using CancellationTokenSource stop = new();
        long lastSeen = 0;
        long reads = 0;
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long seq = Disk()["seq"]!.GetValue<long>();
                Assert.True(seq >= lastSeen, string.Create(CultureInfo.InvariantCulture, $"seq went from {lastSeen} to {seq}"));
                lastSeen = seq;
                reads++;
            }
        }, TestContext.Current.CancellationToken);

        List<Task> writers = [];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            writers.Add(Task.Run(() => Hammer(status, writer, PerWriter), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await reader;

        JsonObject last = Disk();
        Assert.Equal(1 + (Writers * PerWriter), last["seq"]!.GetValue<long>());
        Assert.Equal(status.Seq, last["seq"]!.GetValue<long>());
        Assert.True(reads > 0);
        Assert.Equal(["status.json"], Entries());
    }

    [Fact]
    public void A_line_that_changes_nothing_displayed_writes_nothing()
    {
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);

        status.OnLine(activity: null, usage: false);
        status.OnLine(activity: null, usage: true);

        Assert.Equal(1, status.Seq);
    }

    [Fact]
    public void A_tool_start_an_end_and_an_error_event_each_write()
    {
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);

        status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: false);
        status.OnLine(StreamActivity.ToolEnd(tool: null, "c1", StreamActivity.Completed), usage: false);
        status.OnLine(StreamActivity.Problem, usage: false);

        Assert.Equal(4, status.Seq);
    }

    [Fact]
    public void A_quiet_stream_refreshes_the_file_after_thirty_seconds_and_not_before()
    {
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);

        _clock.Advance(RunStatus.RefreshAfter - TimeSpan.FromSeconds(1));
        status.OnLine(activity: null, usage: false);
        long before = status.Seq;
        _clock.Advance(TimeSpan.FromSeconds(1));
        status.OnLine(activity: null, usage: false);
        long at = status.Seq;

        Assert.Equal(TimeSpan.FromSeconds(30), RunStatus.RefreshAfter);
        Assert.Equal(1, before);
        Assert.Equal(2, at);
        Assert.Equal("2026-11-22T12:00:30.000Z", Disk()["live"]!["last_event_at"]!.GetValue<string>());
    }

    [Fact]
    public void Every_write_restarts_the_refresh_timer()
    {
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);
        _clock.Advance(TimeSpan.FromSeconds(29));
        status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: false);
        long afterWrite = status.Seq;

        _clock.Advance(TimeSpan.FromSeconds(29));
        status.OnLine(activity: null, usage: false);
        long stillQuiet = status.Seq;
        _clock.Advance(TimeSpan.FromSeconds(1));
        status.OnLine(activity: null, usage: false);

        Assert.Equal(2, afterWrite);
        Assert.Equal(2, stillQuiet);
        Assert.Equal(3, status.Seq);
    }

    [Fact]
    public void Last_event_at_is_empty_before_any_line_and_the_time_of_the_last_written_line_after()
    {
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);
        Assert.Null(Disk()["live"]!["last_event_at"]);

        _clock.Advance(TimeSpan.FromSeconds(7));
        status.OnLine(StreamActivity.Problem, usage: false);

        Assert.Equal("2026-11-22T12:00:07.000Z", Disk()["live"]!["last_event_at"]!.GetValue<string>());
    }

    [Fact]
    public void An_unwritable_run_dir_never_throws_and_leaves_one_note()
    {
        RunStatus status = Writer();
        Directory.Delete(_runDir);

        status.Phase(RunPhase.Starting);
        status.WorkerStarted(WorkerPid, WorkerPid);
        status.OnLine(StreamActivity.Problem, usage: false);
        status.Finish("success", 0);

        string note = Assert.IsType<string>(status.Note);
        Assert.Contains("status.json", note, StringComparison.Ordinal);
        List<string> notes = [];
        status.AddNoteTo(notes);
        status.AddNoteTo(notes);
        Assert.Equal([note], notes);
    }

    [Fact]
    public void After_a_failed_write_later_writes_are_tried_again_and_the_first_note_stays()
    {
        RunStatus status = Writer();
        Directory.Delete(_runDir);
        status.Phase(RunPhase.Starting);
        string? firstNote = status.Note;

        _ = Directory.CreateDirectory(_runDir);
        status.Phase(RunPhase.Worker);

        Assert.NotNull(firstNote);
        Assert.Equal("worker", Disk()["phase"]!.GetValue<string>());
        Assert.Equal(firstNote, status.Note);
        Assert.Equal(["status.json"], Entries());
    }

    [Fact]
    public void A_status_without_a_failed_write_has_no_note()
    {
        RunStatus status = Writer();
        status.Phase(RunPhase.Starting);
        List<string> notes = [];

        status.AddNoteTo(notes);

        Assert.Null(status.Note);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData(" off ", false)]
    public void Only_the_value_off_turns_the_writer_off(string? value, bool enabled)
    {
        Assert.Equal(enabled, RunStatus.Enabled(value));
        Assert.Equal("LEAN_WORKER_STATUS", RunStatus.KillSwitch);
    }
}
