using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Issue #146 fix round 1, point 1: a signal the launcher inherited as ignored gets no handler (the SigIgn line of
/// self/status, read through ProcFs, so a fake /proc can give it), nothing is registered when that line cannot be read,
/// and a recorded signal end never sits next to a live phase written afterwards: the next write of any kind drops it
/// and puts the replaced phase back, and Finish writes the normal end. What registration does to a real ignored signal
/// is in LauncherIgnoredSignalProcessTests. Which signals are registered is not observable in process (a
/// PosixSignalRegistration does not say its signal; raising a signal here would end the test host), so the count of
/// registrations is pinned with the pure mapping of the mask (<see cref="StatusSignals.Ignored"/>) next to it.
/// </summary>
[Collection("launcher-process-state")]
public sealed class RunStatusIgnoredSignalTests : IDisposable
{
    private const string StepFinish = """{"type":"step_finish","sessionID":"ses_1","part":{"id":"p1","type":"step-finish","reason":"tool-calls","tokens":{"input":721,"output":2343,"reasoning":445,"cache":{"write":0,"read":149184}},"cost":0}}""";

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly List<RunStatus> _opened = [];
    private readonly StatusRig _rig = new(new OpencodeRuntime());

    public void Dispose()
    {
        // The run's last write stops the handler: no registration outlives its test.
        foreach (RunStatus status in _opened)
        {
            status.Finish("success", 0);
        }

        _rig.Dispose();
        _proc.Dispose();
        _dirs.Dispose();
    }

    private static string SelfStatus(string sigIgn)
    {
        return "Name:\tdotnet\nState:\tS (sleeping)\nSigQ:\t0/63000\nSigPnd:\t0000000000000000\nSigBlk:\t0000000000000000\nSigIgn:\t" + sigIgn
            + "\nSigCgt:\t0000000000004a07\nCapInh:\t0000000000000000\n";
    }

    private void WriteSelfStatus(string content)
    {
        _ = Directory.CreateDirectory(Path.Combine(_proc.Root, "self"));
        File.WriteAllText(Path.Combine(_proc.Root, "self", "status"), content);
    }

    // The writer of a launch with the signal handler on, over the fake /proc.
    private RunStatus Open()
    {
        _proc.Add(4000, 7432110);
        string state = _dirs.Create("lw-ign-state");
        RunStatusSpec spec = new(_dirs.Create("lw-ign-run"), "ign", "ign", 1, Profile: null, "claude", "anthropic", "m", 3m, WrapUpUsd: null, 60, StatusDocs.Now, Worktree: null);
        RunStatus status = RunStatus.Open(spec, owner: null, StatusIndexRig.Env(("LEAN_WORKER_STATE_DIR", state)), _proc.Fs, new FakeClock(StatusDocs.Now), 4000, handleSignals: true)!;
        _opened.Add(status);
        return status;
    }

    // How many signals have a handler: -1 when the writer made no signal object at all.
    private static int Registered(RunStatus status)
    {
        object? signals = typeof(RunStatus).GetField("_signals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(status);
        return signals is null
            ? -1
            : ((ICollection)typeof(StatusSignals).GetField("_registrations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(signals)!).Count;
    }

    [Theory]
    [InlineData("0000000000000000", 3)]
    [InlineData("0000000000000001", 2)]
    [InlineData("0000000000000002", 2)]
    [InlineData("0000000000004000", 2)]
    [InlineData("0000000000000003", 1)]
    [InlineData("0000000000004001", 1)]
    [InlineData("0000000000004002", 1)]
    [InlineData("0000000000004003", 0)]
    [InlineData("0000000000000004", 3)]
    [InlineData("ffffffffffffbffc", 3)]
    [InlineData("ffffffffffffffff", 0)]
    public void A_handler_is_registered_only_for_the_signals_the_launcher_does_not_ignore(string sigIgn, int expected)
    {
        WriteSelfStatus(SelfStatus(sigIgn));

        RunStatus status = Open();

        Assert.Equal(expected, Registered(status));
    }

    [Theory]
    [InlineData(1UL, PosixSignal.SIGHUP, true)]
    [InlineData(2UL, PosixSignal.SIGHUP, false)]
    [InlineData(2UL, PosixSignal.SIGINT, true)]
    [InlineData(1UL, PosixSignal.SIGINT, false)]
    [InlineData(0x4000UL, PosixSignal.SIGTERM, true)]
    [InlineData(0x2000UL, PosixSignal.SIGTERM, false)]
    [InlineData(0x8000UL, PosixSignal.SIGTERM, false)]
    [InlineData(0x4003UL, PosixSignal.SIGTERM, true)]
    [InlineData(0UL, PosixSignal.SIGHUP, false)]
    [InlineData(0UL, PosixSignal.SIGINT, false)]
    [InlineData(0UL, PosixSignal.SIGTERM, false)]
    public void The_mask_has_SIGHUP_at_bit_1_SIGINT_at_bit_2_and_SIGTERM_at_bit_15_counted_from_1(ulong mask, PosixSignal signal, bool ignored) =>
        Assert.Equal(ignored, StatusSignals.Ignored(mask, signal));

    // The first entry stands for no self/status file at all.
    private const string NoFile = "<no file>";

    public static TheoryData<string> Unusable() =>
        [NoFile, string.Empty, "Name:\tdotnet\nSigQ:\t0/63000\n", "SigIgn:\n", "SigIgn:\tnot-hex\n", "SigIgn:\t-1\n", "SigIgn:\t10000000000000000\n", "sigign:\t0000000000000000\n"];

    [Theory]
    [MemberData(nameof(Unusable))]
    public void A_self_status_that_is_missing_or_malformed_registers_no_handler_at_all(string content)
    {
        if (!string.Equals(content, NoFile, StringComparison.Ordinal))
        {
            WriteSelfStatus(content);
        }

        RunStatus status = Open();

        Assert.Equal(0, Registered(status));
        Assert.Null(_proc.Fs.IgnoredSignals());
    }

    [Fact]
    public void A_self_status_that_cannot_be_read_as_a_file_registers_no_handler_at_all()
    {
        // A directory where the file should be stands in for a permission error.
        _ = Directory.CreateDirectory(Path.Combine(_proc.Root, "self", "status"));

        RunStatus status = Open();

        Assert.Equal(0, Registered(status));
        Assert.Null(_proc.Fs.IgnoredSignals());
    }

    [Fact]
    public void The_ignored_set_is_the_SigIgn_line_read_as_hex()
    {
        WriteSelfStatus(SelfStatus("0000000000004003"));
        Assert.Equal(0x4003UL, _proc.Fs.IgnoredSignals());

        WriteSelfStatus(SelfStatus("ffffffffffffffff"));
        Assert.Equal(ulong.MaxValue, _proc.Fs.IgnoredSignals());
    }

    [Fact]
    public void A_launch_with_the_handler_off_registers_nothing_whatever_the_mask_says()
    {
        _proc.Add(4000, 7432110);
        WriteSelfStatus(SelfStatus("0000000000000000"));
        RunStatusSpec spec = new(_dirs.Create("lw-ign-run"), "ign", "ign", 1, Profile: null, "claude", "anthropic", "m", 3m, WrapUpUsd: null, 60, StatusDocs.Now, Worktree: null);

        RunStatus status = RunStatus.Open(spec, owner: null, StatusIndexRig.Env(("LEAN_WORKER_STATE_DIR", _dirs.Create("lw-ign-state"))), _proc.Fs, new FakeClock(StatusDocs.Now), 4000, handleSignals: false)!;

        Assert.Equal(-1, Registered(status));
    }

    [Fact]
    public void A_stream_line_after_a_recorded_signal_drops_the_end_and_puts_the_replaced_phase_back()
    {
        _rig.Status.GateStarted(2, 3);
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        Assert.Equal("launcher-signalled", _rig.Disk()["end"]!["status"]!.GetValue<string>());

        _rig.Feed(StepFinish);

        JsonObject doc = _rig.Disk();
        Assert.Null(doc["end"]);
        Assert.Equal("gate", doc["phase"]!.GetValue<string>());
        Assert.Equal(2, doc["live"]!["gate_round"]!.GetValue<int>());
        Assert.Equal(1, doc["live"]!["api_calls"]!.GetValue<int>());
    }

    [Fact]
    public void A_phase_write_after_a_recorded_signal_drops_the_end_and_sets_its_own_phase()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));

        _rig.Status.GateStarted(2, 3);

        JsonObject doc = _rig.Disk();
        Assert.Null(doc["end"]);
        Assert.Equal("gate", doc["phase"]!.GetValue<string>());
        Assert.Equal(2, doc["live"]!["gate_round"]!.GetValue<int>());
        _rig.Status.Finishing();
        Assert.Equal("finishing", _rig.Disk()["phase"]!.GetValue<string>());
        Assert.Null(_rig.Disk()["end"]);
    }

    [Fact]
    public void A_worker_start_after_a_recorded_signal_drops_the_end()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGINT));

        _rig.Status.WorkerStarted(4001, 4001);

        JsonObject doc = _rig.Disk();
        Assert.Null(doc["end"]);
        Assert.Equal("worker", doc["phase"]!.GetValue<string>());
        Assert.Equal(4001, doc["worker"]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public void Finish_after_a_recorded_signal_writes_the_normal_end_and_nothing_is_recorded_after_it()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));

        _rig.Status.Finish("success", 0);

        JsonObject doc = _rig.Disk();
        Assert.Equal("done", doc["phase"]!.GetValue<string>());
        JsonObject end = doc["end"]!.AsObject();
        Assert.Equal("success", end["status"]!.GetValue<string>());
        Assert.Equal(0, end["exit_code"]!.GetValue<int>());
        Assert.Null(end["signal"]);
        Assert.False(_rig.Status.Signalled(PosixSignal.SIGTERM));
        Assert.Equal("success", _rig.Disk()["end"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void A_signal_after_a_dropped_one_is_recorded_again_and_a_second_signal_in_a_row_is_not()
    {
        Assert.True(_rig.Status.Signalled(PosixSignal.SIGTERM));
        Assert.False(_rig.Status.Signalled(PosixSignal.SIGHUP));
        _rig.Feed(StepFinish);

        Assert.True(_rig.Status.Signalled(PosixSignal.SIGHUP));

        JsonObject end = _rig.Disk()["end"]!.AsObject();
        Assert.Equal("SIGHUP", end["signal"]!.GetValue<string>());
        Assert.Equal("failed", _rig.Disk()["phase"]!.GetValue<string>());
    }
}
