using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The reader's state rules (docs/research/live-status/README.md, section 4.1, "What a reader does"
/// and the ended rule), each case over a fake /proc root, a fake clock and a given boot id. Launcher
/// 100 (start ticks 5000) is the one every status document records; worker 300 (ticks 6000) leads
/// group 300 of session 300.
/// </summary>
public sealed class StatusReaderTests : IDisposable
{
    private const string Id = "20261122-110000-job-w1";

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly FakeClock _clock = new(StatusDocs.Now);
    private readonly string _runsRoot;

    public StatusReaderTests() => _runsRoot = _dirs.Create("lw-reader");

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private void LauncherAlive() => _proc.Add(StatusDocs.LauncherPid, StatusDocs.LauncherTicks, comm: "lean-worker");

    private StatusRow Read(JsonObject doc, string? summary = null, string? bootId = FakeProcRoot.DefaultBoot)
    {
        string dir = StatusDocs.WriteRun(_runsRoot, Id, doc, summary);
        return new StatusReader(_proc.Fs, _clock, bootId).Read(dir);
    }

    private static JsonObject Orphanable(string boot = FakeProcRoot.DefaultBoot, JsonObject? worker = null) =>
        StatusDocs.Make("job-w1", boot: boot, worker: worker ?? StatusDocs.Worker());

    [Fact]
    public void A_run_whose_launcher_is_provably_alive_is_live_and_running()
    {
        LauncherAlive();

        StatusRow row = Read(StatusDocs.Make("job-w1", worker: StatusDocs.Worker()));

        Assert.Equal(StatusStates.Running, row.State);
        Assert.Equal(Liveness.Alive, row.Liveness);
        Assert.True(row.Live);
        Assert.False(row.Ended);
        Assert.False(row.Attention);
        Assert.Null(row.Quiet);
        Assert.Equal("job-w1", row.Name);
    }

    [Fact]
    public void A_live_run_that_has_wrapped_up_says_so()
    {
        LauncherAlive();

        StatusRow row = Read(StatusDocs.Make("job-w1", wrappedUp: true));

        Assert.Equal(StatusStates.WrapUp, row.State);
        Assert.True(row.Live);
    }

    [Fact]
    public void A_live_run_in_the_gate_phase_shows_the_gate_and_its_round()
    {
        LauncherAlive();

        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "gate", gateRound: 2));

        Assert.Equal(StatusStates.Gate, row.State);
        Assert.Contains("gate round 2", row.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("finishing")]
    public void The_other_live_phases_are_shown_as_they_are(string phase)
    {
        LauncherAlive();

        Assert.Equal(phase, Read(StatusDocs.Make("job-w1", phase: phase)).State);
    }

    [Theory]
    [InlineData("done", "success")]
    [InlineData("failed", "launch-failed")]
    public void A_terminal_phase_under_a_live_launcher_is_ending_and_not_final(string phase, string endStatus)
    {
        LauncherAlive();

        StatusRow row = Read(StatusDocs.Make("job-w1", phase: phase, end: StatusDocs.End(endStatus)));

        Assert.Equal(StatusStates.Ending, row.State);
        Assert.Equal(Liveness.Alive, row.Liveness);
        Assert.True(row.Live);
        Assert.False(row.Ended);
        Assert.Null(row.EndedAt);
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("wrapped-up", false)]
    [InlineData("error", true)]
    [InlineData("crashed", true)]
    [InlineData("timed-out", true)]
    public void A_run_whose_launcher_is_gone_is_ended_with_the_status_files_end(string status, bool attention)
    {
        DateTimeOffset at = StatusDocs.Now.AddMinutes(-3);

        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "done", end: StatusDocs.End(status, at)));

        Assert.Equal(status, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.False(row.Live);
        Assert.Equal(attention, row.Attention);
        Assert.Equal(at, row.EndedAt);
    }

    [Fact]
    public void A_launcher_that_died_after_the_summary_leaves_the_state_to_summary_json()
    {
        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "finishing", worker: StatusDocs.Worker()), summary: "success");

        Assert.Equal("success", row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.False(row.Attention);
    }

    [Theory]
    [InlineData("success", "error", "success")]
    [InlineData("error", "success", "error")]
    public void The_status_files_end_wins_over_a_summary_that_disagrees(string end, string summary, string expected)
    {
        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "done", end: StatusDocs.End(end)), summary);

        Assert.Equal(expected, row.State);
    }

    [Fact]
    public void A_failure_after_a_successful_summary_is_failed_and_says_after_summary()
    {
        JsonObject end = new()
        {
            ["status"] = "launch-failed",
            ["exit_code"] = null,
            ["at"] = StatusDocs.Stamp(StatusDocs.Now),
            ["error"] = "ledger append failed",
            ["after_summary"] = true,
            ["summary_status"] = "success",
        };

        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "failed", end: end), summary: "success");

        Assert.Equal(StatusStates.Failed, row.State);
        Assert.True(row.Attention);
        Assert.Contains("after summary: success", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_boot_is_lost_without_reading_the_group_even_when_the_same_numbers_are_alive_now()
    {
        // The numbers of the recorded run now belong to a live process group with the recorded start
        // ticks, session and a member: a reader that looked would say orphaned.
        _proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: StatusDocs.WorkerPid);
        _proc.Add(StatusDocs.LauncherPid, StatusDocs.LauncherTicks);

        StatusRow row = Read(Orphanable(boot: "boot-OLD"));

        Assert.Equal(StatusStates.Lost, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.True(row.Attention);
    }

    [Fact]
    public void Another_boot_with_an_end_in_the_file_is_that_end_not_lost()
    {
        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "done", boot: "boot-OLD", end: StatusDocs.End("success")));

        Assert.Equal("success", row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
    }

    [Fact]
    public void On_the_same_boot_a_launcher_pid_that_is_absent_is_ended()
    {
        StatusRow row = Read(Orphanable());

        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.False(row.Live);
    }

    [Fact]
    public void A_launcher_pid_with_other_start_ticks_is_a_reused_pid_and_the_run_is_ended()
    {
        _proc.Add(StatusDocs.LauncherPid, StatusDocs.LauncherTicks + 1);

        StatusRow row = Read(Orphanable());

        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.False(row.Live);
    }

    [Theory]
    [InlineData('Z')]
    [InlineData('X')]
    public void A_zombie_or_dead_launcher_is_not_alive_so_the_run_is_ended(char state)
    {
        _proc.Add(StatusDocs.LauncherPid, StatusDocs.LauncherTicks, state);

        StatusRow row = Read(Orphanable());

        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
    }

    [Fact]
    public void A_group_leader_with_the_recorded_start_ticks_is_an_orphaned_worker()
    {
        _proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Orphaned, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.True(row.Attention);
    }

    [Fact]
    public void A_group_leader_pid_with_other_start_ticks_is_a_recycled_number_and_lost_even_with_members_around()
    {
        _proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks + 1);
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: StatusDocs.WorkerPid);

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Lost, row.State);
    }

    [Fact]
    public void A_gone_leader_with_a_member_of_the_recorded_group_and_session_is_orphaned()
    {
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: StatusDocs.WorkerPid);

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Orphaned, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
    }

    [Fact]
    public void A_process_in_the_recorded_group_but_another_session_does_not_make_it_orphaned()
    {
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: 9999);

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Lost, row.State);
    }

    [Fact]
    public void A_gone_leader_and_nobody_left_in_the_group_is_lost()
    {
        _proc.Add(302, 1, pgrp: 800, session: 800);

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Lost, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Attention);
    }

    [Fact]
    public void A_run_whose_worker_never_started_or_had_no_group_of_its_own_is_lost_not_orphaned()
    {
        // A process in the launcher's own group would be a false "orphaned" if the null group were scanned.
        _proc.Add(301, 1, pgrp: 3990, session: 3990);

        Assert.Equal(StatusStates.Lost, Read(StatusDocs.Make("job-w1", phase: "starting")).State);
        Assert.Equal(StatusStates.Lost, Read(Orphanable(worker: StatusDocs.Worker(pgid: null, sid: null))).State);
    }

    [Fact]
    public void A_launcher_probe_that_fails_is_unknown_never_lost_and_the_run_is_not_ended()
    {
        _proc.AddRaw(StatusDocs.LauncherPid, "not a stat line");

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Unknown, row.State);
        Assert.Equal(Liveness.Unknown, row.Liveness);
        Assert.False(row.Ended);
        Assert.False(row.Live);
        Assert.True(row.Attention);
    }

    [Fact]
    public void A_launcher_stat_entry_that_cannot_be_read_is_unknown_even_when_the_file_has_an_end()
    {
        _proc.AddUnreadable(StatusDocs.LauncherPid);

        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "done", end: StatusDocs.End("success")));

        Assert.Equal(StatusStates.Unknown, row.State);
        Assert.False(row.Ended);
    }

    [Fact]
    public void A_current_boot_id_that_cannot_be_read_makes_every_run_unknown()
    {
        LauncherAlive();

        StatusRow row = Read(Orphanable(), bootId: null);

        Assert.Equal(StatusStates.Unknown, row.State);
        Assert.Equal(Liveness.Unknown, row.Liveness);
    }

    [Fact]
    public void A_group_leader_probe_that_fails_is_unknown_not_lost()
    {
        _proc.AddRaw(StatusDocs.WorkerPid, "garbage");

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Unknown, row.State);
        Assert.False(row.Ended);
    }

    [Fact]
    public void A_process_list_that_cannot_be_read_when_the_leader_is_gone_is_unknown_not_lost()
    {
        _proc.AddUnreadable(450);

        StatusRow row = Read(Orphanable());

        Assert.Equal(StatusStates.Unknown, row.State);
        Assert.False(row.Ended);
    }

    [Fact]
    public void A_status_file_that_does_not_parse_is_unreadable()
    {
        string dir = Path.Combine(_runsRoot, "runs", Id);
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status.json"), "{ \"schema\": \"lean-worker.status/1\", \"seq\": ");

        StatusRow row = new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot).Read(dir);

        Assert.Equal(StatusStates.Unreadable, row.State);
        Assert.Equal(Liveness.Unknown, row.Liveness);
        Assert.True(row.Attention);
        Assert.False(row.Ended);
    }

    [Fact]
    public void A_run_dir_without_status_json_is_a_legacy_row_with_no_liveness()
    {
        string dir = StatusDocs.WriteRun(_runsRoot, Id, doc: null);

        StatusRow row = new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot).Read(dir);

        Assert.Equal(StatusStates.Legacy, row.State);
        Assert.Equal(Liveness.Unknown, row.Liveness);
        Assert.Null(row.Status);
        Assert.Null(row.SummaryStatus);
        Assert.Equal("job-w1", row.Name);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 11, 0, 0, TimeSpan.Zero), row.StartedAt);
    }

    [Fact]
    public void A_legacy_run_dir_with_a_summary_carries_the_summary_status()
    {
        string dir = StatusDocs.WriteRun(_runsRoot, Id, doc: null, summaryStatus: "wrapped-up");

        StatusRow row = new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot).Read(dir);

        Assert.Equal(StatusStates.Legacy, row.State);
        Assert.Equal(Liveness.Unknown, row.Liveness);
        Assert.Equal("wrapped-up", row.SummaryStatus);
    }

    [Fact]
    public void Quiet_is_an_annotation_on_a_live_run_that_starts_after_ten_minutes_without_an_event()
    {
        LauncherAlive();
        DateTimeOffset tenMinutesAgo = StatusDocs.Now.AddMinutes(-10);

        StatusRow atBoundary = Read(StatusDocs.Make("job-w1", lastEventAt: tenMinutesAgo));
        StatusRow past = Read(StatusDocs.Make("job-w1", lastEventAt: tenMinutesAgo.AddSeconds(-1)));

        Assert.Equal(TimeSpan.FromMinutes(10), StatusReader.QuietAfter);
        Assert.Null(atBoundary.Quiet);
        Assert.False(atBoundary.Attention);
        Assert.Equal(TimeSpan.FromSeconds(601), past.Quiet);
        Assert.True(past.Attention);
    }

    [Fact]
    public void A_quiet_run_is_still_running_and_the_detail_names_how_long()
    {
        LauncherAlive();
        JsonObject tool = StatusDocs.Tool("Bash", "running", StatusDocs.Now.AddMinutes(-41));

        StatusRow row = Read(StatusDocs.Make("job-w1", lastEventAt: StatusDocs.Now.AddMinutes(-41), lastTool: tool));

        Assert.Equal(StatusStates.Running, row.State);
        Assert.Equal(Liveness.Alive, row.Liveness);
        Assert.Contains("quiet 41m", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Bash", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ended_run_with_an_old_last_event_is_not_quiet()
    {
        StatusRow row = Read(StatusDocs.Make("job-w1", phase: "done", end: StatusDocs.End("success"), lastEventAt: StatusDocs.Now.AddHours(-5)));

        Assert.Null(row.Quiet);
        Assert.False(row.Attention);
    }
}
