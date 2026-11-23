using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Issue #146, fix round 3, point 2 (review finding 3): a recorded signal is held, not erased by later writes. Through
/// the timer seam (<see cref="RunStatus.HoldTimer"/>, fired by hand): while the hold lasts, a stream line, a phase change
/// and a worker start write nothing, so the record stays in the file for the launcher that dies of the signal; when the
/// timer fires the launcher survived, the record goes and one write shows the live state (no end, the phase that was
/// current, with what arrived during the hold) and later writes are normal; <c>Finish</c> and <c>Fail</c> write their end
/// at once and cancel the timer; a second signal records again and restarts the timer; a timer that fires late, or into a
/// writer that is finished or whose run dir is gone, does nothing and throws nothing.
/// </summary>
[Collection("launcher-process-state")]
public sealed class RunStatusSignalHoldTests : IDisposable
{
    private const string StepFinish = """{"type":"step_finish","sessionID":"ses_1","part":{"id":"p1","type":"step-finish","reason":"tool-calls","tokens":{"input":721,"output":2343,"reasoning":445,"cache":{"write":0,"read":149184}},"cost":0}}""";

    private const string Noise = """{"type":"noise"}""";

    private readonly StatusRig _rig = new(new OpencodeRuntime());
    private readonly FakeHoldTimer _timers = new();

    public RunStatusSignalHoldTests() => _ = _timers.Attach(_rig.Status);

    public void Dispose() => _rig.Dispose();

    private byte[] Bytes() => File.ReadAllBytes(Path.Combine(_rig.RunDir, "status.json"));

    private void AssertRecordHeld(string signal = "SIGTERM")
    {
        JsonObject doc = _rig.Disk();
        Assert.Equal("failed", doc["phase"]!.GetValue<string>());
        Assert.Equal("launcher-signalled", doc["end"]!["status"]!.GetValue<string>());
        Assert.Equal(signal, doc["end"]!["signal"]!.GetValue<string>());
        Assert.Null(doc["live"]!["gate_round"]);
    }

    [Fact]
    public void A_recorded_signal_starts_one_timer_of_two_seconds()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));

        FakeHoldTimer.Started timer = Assert.Single(_timers.All);
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Due);
        Assert.False(timer.Disposed);
        AssertRecordHeld();
    }

    [Fact]
    public void A_signal_that_records_nothing_starts_no_timer()
    {
        _rig.Status.Finish("success", 0);

        Assert.False(_rig.Status.Signalled(PosixSignal.SIGTERM));

        Assert.Empty(_timers.All);
    }

    [Fact]
    public void A_stream_line_that_changes_a_counter_does_not_erase_the_record_while_the_hold_lasts()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        byte[] before = Bytes();
        long seq = _rig.Status.Seq;

        _rig.Feed(StepFinish);

        Assert.Equal(before, Bytes());
        Assert.Equal(seq, _rig.Status.Seq);
        AssertRecordHeld();
    }

    [Fact]
    public void A_stream_line_that_is_due_only_because_the_last_write_is_old_does_not_erase_the_record_either()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        byte[] before = Bytes();
        _rig.Clock.Advance(RunStatus.RefreshAfter + TimeSpan.FromSeconds(1));

        _rig.Feed(Noise);

        Assert.Equal(before, Bytes());
        AssertRecordHeld();
    }

    [Fact]
    public void A_phase_change_does_not_erase_the_record_while_the_hold_lasts()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));
        byte[] before = Bytes();

        _rig.Status.GateStarted(2, 3);
        _rig.Status.Finishing();

        Assert.Equal(before, Bytes());
        AssertRecordHeld("SIGHUP");
    }

    [Fact]
    public void A_worker_start_does_not_erase_the_record_while_the_hold_lasts()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGINT));
        byte[] before = Bytes();

        _rig.Status.WorkerStarted(4001, 4001);

        Assert.Equal(before, Bytes());
        AssertRecordHeld("SIGINT");
    }

    [Fact]
    public void When_the_timer_fires_the_file_shows_the_phase_that_was_current_and_no_end()
    {
        _rig.Status.GateStarted(2, 3);
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));

        _timers.Last.Fire();

        JsonObject doc = _rig.Disk();
        Assert.Null(doc["end"]);
        Assert.Equal("gate", doc["phase"]!.GetValue<string>());
        Assert.Equal(2, doc["live"]!["gate_round"]!.GetValue<int>());
        Assert.Equal(3, doc["live"]!["gate_max_rounds"]!.GetValue<int>());
    }

    [Fact]
    public void When_the_timer_fires_the_file_shows_what_arrived_during_the_hold_in_one_write()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        long seq = _rig.Status.Seq;
        _rig.Status.GateStarted(2, 3);
        _rig.Feed(StepFinish);
        _rig.Status.WorkerStarted(4001, 4001);
        _rig.Status.GateStarted(3, 3);
        Assert.Equal(seq, _rig.Status.Seq);

        _timers.Last.Fire();

        JsonObject doc = _rig.Disk();
        Assert.Null(doc["end"]);
        Assert.Equal("gate", doc["phase"]!.GetValue<string>());
        Assert.Equal(3, doc["live"]!["gate_round"]!.GetValue<int>());
        Assert.Equal(1, doc["live"]!["api_calls"]!.GetValue<int>());
        Assert.Equal(4001, doc["worker"]!["pid"]!.GetValue<int>());
        Assert.Equal(seq + 1, doc["seq"]!.GetValue<long>());
    }

    [Fact]
    public void After_the_timer_fired_later_writes_are_normal_and_a_new_signal_is_recorded_again()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        _timers.Last.Fire();
        long seq = _rig.Status.Seq;

        _rig.Feed(StepFinish);
        Assert.Equal(1, _rig.Live()["api_calls"]!.GetValue<int>());
        _rig.Status.Finishing();

        JsonObject doc = _rig.Disk();
        Assert.Equal("finishing", doc["phase"]!.GetValue<string>());
        Assert.Null(doc["end"]);
        Assert.Equal(seq + 2, doc["seq"]!.GetValue<long>());
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));
        AssertRecordHeld("SIGHUP");
        Assert.Equal(2, _timers.All.Count);
    }

    [Fact]
    public void Finish_during_the_hold_writes_its_end_at_once_and_a_timer_firing_afterwards_changes_nothing()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        FakeHoldTimer.Started timer = _timers.Last;

        _rig.Status.Finish("success", 0);

        JsonObject doc = _rig.Disk();
        Assert.Equal("done", doc["phase"]!.GetValue<string>());
        Assert.Equal("success", doc["end"]!["status"]!.GetValue<string>());
        Assert.Null(doc["end"]!["signal"]);
        Assert.True(timer.Disposed);
        byte[] after = Bytes();
        long seq = _rig.Status.Seq;

        Exception? thrown = Record.Exception(() => timer.Fire());

        Assert.Null(thrown);
        Assert.Equal(after, Bytes());
        Assert.Equal(seq, _rig.Status.Seq);
    }

    [Fact]
    public void Fail_during_the_hold_writes_its_end_at_once_and_a_timer_firing_afterwards_changes_nothing()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));
        FakeHoldTimer.Started timer = _timers.Last;

        _rig.Status.Fail("it broke", summaryStatus: null);

        JsonObject doc = _rig.Disk();
        Assert.Equal("failed", doc["phase"]!.GetValue<string>());
        Assert.Equal("launch-failed", doc["end"]!["status"]!.GetValue<string>());
        Assert.Equal("it broke", doc["end"]!["error"]!.GetValue<string>());
        Assert.True(timer.Disposed);
        byte[] after = Bytes();

        Exception? thrown = Record.Exception(() => timer.Fire());

        Assert.Null(thrown);
        Assert.Equal(after, Bytes());
    }

    [Fact]
    public void A_second_signal_during_the_hold_records_again_and_restarts_the_timer()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        FakeHoldTimer.Started first = _timers.Last;

        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));

        Assert.Equal(2, _timers.All.Count);
        Assert.True(first.Disposed);
        Assert.False(_timers.Last.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(2), _timers.Last.Due);
        AssertRecordHeld("SIGHUP");
    }

    [Fact]
    public void The_timer_a_second_signal_replaced_does_nothing_when_it_fires_and_the_new_one_ends_the_hold()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        FakeHoldTimer.Started first = _timers.Last;
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));
        byte[] held = Bytes();

        first.Fire();

        Assert.Equal(held, Bytes());
        AssertRecordHeld("SIGHUP");

        _timers.Last.Fire();

        Assert.Null(_rig.Disk()["end"]);
        Assert.Equal("worker", _rig.Disk()["phase"]!.GetValue<string>());
    }

    [Fact]
    public void A_timer_that_fires_twice_writes_once()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        _timers.Last.Fire();
        long seq = _rig.Status.Seq;
        byte[] after = Bytes();

        _timers.Last.Fire();

        Assert.Equal(seq, _rig.Status.Seq);
        Assert.Equal(after, Bytes());
    }

    [Fact]
    public void A_timer_that_fires_after_the_writers_last_write_does_nothing_and_does_not_throw()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        FakeHoldTimer.Started timer = _timers.Last;
        _rig.Status.Finish("error", 1);
        byte[] after = Bytes();

        Exception? thrown = Record.Exception(() => timer.Fire());

        Assert.Null(thrown);
        Assert.Equal(after, Bytes());
        Assert.Equal("error", _rig.Disk()["end"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void A_timer_that_fires_into_a_run_dir_that_is_gone_does_not_throw()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        Directory.Delete(_rig.RunDir, recursive: true);

        Exception? thrown = Record.Exception(() => _timers.Last.Fire());

        Assert.Null(thrown);
    }
}
