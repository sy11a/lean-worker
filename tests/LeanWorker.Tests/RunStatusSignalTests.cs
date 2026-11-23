using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The record-only signal handler (docs/research/live-status/README.md, section 5, slice 5), without a real
/// signal: what <c>Signalled</c> writes, when it writes nothing, and that it gives up after a bounded wait when a
/// status write holds the lock. The real-signal test is in LauncherSignalProcessTests.
/// </summary>
[Collection("launcher-process-state")]
public sealed class RunStatusSignalTests : IDisposable
{
    private readonly StatusRig _rig = new(new OpencodeRuntime());

    public void Dispose() => _rig.Dispose();

    private static object LockOf(RunStatus status) =>
        typeof(RunStatus).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(status)!;

    [Theory]
    [InlineData(PosixSignal.SIGTERM, "SIGTERM")]
    [InlineData(PosixSignal.SIGINT, "SIGINT")]
    [InlineData(PosixSignal.SIGHUP, "SIGHUP")]
    public void A_signal_makes_the_file_failed_and_launcher_signalled_with_the_signals_name(PosixSignal signal, string name)
    {
        long seq = _rig.Status.Seq;
        _rig.Clock.Advance(TimeSpan.FromSeconds(5));

        bool recorded = _rig.Status.Signalled(signal);

        Assert.True(recorded);
        JsonObject doc = _rig.Disk();
        Assert.Equal("failed", doc["phase"]!.GetValue<string>());
        JsonObject end = doc["end"]!.AsObject();
        Assert.Equal("launcher-signalled", end["status"]!.GetValue<string>());
        Assert.Equal(name, end["signal"]!.GetValue<string>());
        Assert.Null(end["exit_code"]);
        Assert.Equal(StatusDocs.Stamp(StatusDocs.Now.AddSeconds(5)), end["at"]!.GetValue<string>());
        Assert.Equal(seq + 1, doc["seq"]!.GetValue<long>());
        Assert.Null(doc["live"]!["gate_round"]);
    }

    [Fact]
    public void A_signal_during_the_gate_clears_the_gate_round()
    {
        _rig.Status.GateStarted(2, 3);

        _ = _rig.Status.Signalled(PosixSignal.SIGTERM);

        Assert.Null(_rig.Live()["gate_round"]);
        Assert.Equal("failed", _rig.Disk()["phase"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_is_written_when_the_run_already_has_an_end()
    {
        _rig.Status.Finish("success", 0);
        long seq = _rig.Status.Seq;
        string before = File.ReadAllText(Path.Combine(_rig.RunDir, "status.json"));

        bool recorded = _rig.Status.Signalled(PosixSignal.SIGTERM);

        Assert.False(recorded);
        Assert.Equal(seq, _rig.Status.Seq);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_rig.RunDir, "status.json")));
        Assert.Equal("done", _rig.Disk()["phase"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_is_written_over_a_launch_failure_and_a_second_signal_during_the_hold_replaces_the_first_record()
    {
        _rig.Status.Fail("it broke", summaryStatus: null);
        Assert.False(_rig.Status.Signalled(PosixSignal.SIGTERM));
        Assert.Equal("launch-failed", _rig.Disk()["end"]!["status"]!.GetValue<string>());

        using StatusRig second = new(new OpencodeRuntime());
        FakeHoldTimer timers = new FakeHoldTimer().Attach(second.Status);
        Assert.True(second.Status.Signalled(PosixSignal.SIGTERM));
        Assert.True(second.Status.Signalled(PosixSignal.SIGHUP));
        Assert.Equal("SIGHUP", second.Disk()["end"]!["signal"]!.GetValue<string>());
        Assert.Equal(2, timers.All.Count);
    }

    [Fact]
    public void A_status_write_that_holds_the_lock_makes_the_handler_give_up_after_the_bounded_wait_and_the_run_is_untouched()
    {
        object gate = LockOf(_rig.Status);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() => HoldLock(gate, held, release)) { IsBackground = true, Name = "test-lock-holder" };
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            byte[] before = File.ReadAllBytes(Path.Combine(_rig.RunDir, "status.json"));
            Stopwatch watch = Stopwatch.StartNew();

            bool recorded = _rig.Status.Signalled(PosixSignal.SIGTERM);
            watch.Stop();

            Assert.False(recorded);
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(800), RunStatus.SignalLockWait + TimeSpan.FromSeconds(5));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(_rig.RunDir, "status.json")));
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
        }

        // Once the lock is free the handler works as ever.
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        Assert.Equal("launcher-signalled", _rig.Disk()["end"]!["status"]!.GetValue<string>());
    }

    // A status write in progress: the lock is held until the test lets go (or 30 s pass).
    private static void HoldLock(object gate, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        lock (gate)
        {
            held.Set();
            _ = release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void The_wait_for_the_lock_is_a_second() => Assert.Equal(TimeSpan.FromSeconds(1), RunStatus.SignalLockWait);

    [Fact]
    public void With_the_writer_off_nothing_is_opened_nothing_is_written_and_no_pointer_is_made()
    {
        using FakeProcRoot proc = new();
        proc.Add(4000, 7432110);
        using TempDirs dirs = new();
        string runDir = dirs.Create("lw-off");
        string state = dirs.Create("lw-off-state");
        RunStatusSpec spec = new(runDir, "off", "off", 1, Profile: null, "claude", "anthropic", "m", 3m, WrapUpUsd: null, 60, StatusDocs.Now, Worktree: null);

        RunStatus? status = RunStatus.Open(spec, owner: null, StatusIndexRig.Env(("LEAN_WORKER_STATUS", "off"), ("LEAN_WORKER_STATE_DIR", state)),
            proc.Fs, new FakeClock(StatusDocs.Now), 4000, handleSignals: true);

        Assert.Null(status);
        Assert.Empty(Directory.GetFileSystemEntries(runDir));
        Assert.Empty(Directory.GetFileSystemEntries(state));
    }
}
