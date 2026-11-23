using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Every span `status --wait` and `status --watch` measure (--timeout, --grace, the --expect phase, the pause between
/// reads, the redraw period) is measured on the clock's monotonic timestamp (the review of #147, finding 3): a step of
/// the wall clock, back or forward, mid-wait changes none of them. The fake clock's wall time is stepped apart from its
/// timestamp (<see cref="FakeClock.StepWall"/>).
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitMonotonicTests : IDisposable
{
    private const string Begin = "\u001b[?2026h\u001b[H";

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    // The wall clock steps at <at> by <hours> hours (back when negative); the timestamp does not.
    private void Step(TimeSpan at, int hours) => _rig.At(at, () => _rig.Idx.Clock.StepWall(TimeSpan.FromHours(hours)));

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(-24)]
    public async Task A_step_of_the_wall_clock_does_not_change_the_timeoutAsync(int hours)
    {
        _ = _rig.Add("never-ends");
        Step(Seconds(30), hours);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--timeout", "3m", "--interval", "10");

        Assert.Null(r.Error);
        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(3), _rig.Passed);
        Assert.Equal(TimeSpan.FromMinutes(3), _rig.Pauses.Aggregate(TimeSpan.Zero, (sum, p) => sum + p));
        Assert.Contains("timed out after 3m00s", r.Lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_step_of_the_wall_clock_does_not_change_the_graceAsync(int hours)
    {
        _rig.Blur(_rig.Add("unsure"));
        Step(Seconds(30), hours);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "2m", "--timeout", "30m", "--interval", "10");

        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Passed, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + Seconds(10));
        Assert.Contains("past the grace of 2m00s", r.Lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_step_of_the_wall_clock_does_not_change_how_long_expect_waits_for_runs_to_appearAsync(int hours)
    {
        _ = _rig.Add("only-one");
        Step(Seconds(20), hours);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "2", "--grace", "90s", "--timeout", "30m", "--interval", "10");

        Assert.Equal(8, r.Code);
        Assert.Equal(Seconds(90), _rig.Passed);
        Assert.Contains("only 1 of 2 expected runs appeared within 1m30s", r.Lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_step_of_the_wall_clock_does_not_change_the_timeout_of_the_expect_phaseAsync(int hours)
    {
        _ = _rig.Add("only-one");
        Step(Seconds(20), hours);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "2", "--grace", "10m", "--timeout", "60s", "--interval", "10");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(60), _rig.Passed);
        Assert.Contains("timed out after 1m00s with 1 of 2 expected runs", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_appears_during_the_expect_phase_is_still_taken_when_the_wall_clock_stepped_backAsync()
    {
        StatusLiveRig.Live first = _rig.Add("first");
        StatusLiveRig.Live? second = null;
        _rig.At(Seconds(30), () => second = _rig.Add("second"));
        Step(Seconds(20), -2);
        _rig.At(Seconds(60), () =>
        {
            _rig.End(first);
            _rig.End(second!);
        });

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "2", "--grace", "5m", "--timeout", "30m", "--interval", "10");

        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 2 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Equal(Seconds(60), _rig.Passed);
    }

    [Fact]
    public async Task The_pauses_stay_the_interval_whatever_the_wall_clock_doesAsync()
    {
        StatusLiveRig.Live run = _rig.Add("slow");
        Step(Seconds(10), -1);
        _rig.At(Seconds(40), () => _rig.End(run));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--interval", "10");

        Assert.Equal(0, r.Code);
        Assert.All(_rig.Pauses, p => Assert.Equal(Seconds(10), p));
        Assert.Equal(4, _rig.Pauses.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task A_step_of_the_wall_clock_does_not_change_the_period_of_the_watchs_redrawAsync(int hours)
    {
        _ = _rig.Add("run-a");
        Step(Seconds(7), hours);
        _rig.At(TimeSpan.FromMilliseconds(12_500), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--watch", "--interval", "5");

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        // Frames at 0, 5 and 10 seconds of the timestamp: the step at 7 seconds neither adds a frame nor drops one.
        Assert.Equal(3, r.Output.Split(Begin).Length - 1);
    }

    [Fact]
    public async Task Without_a_step_the_watch_draws_the_same_frames_so_the_test_above_measures_the_stepAsync()
    {
        _ = _rig.Add("run-a");
        _rig.At(TimeSpan.FromMilliseconds(12_500), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--watch", "--interval", "5");

        Assert.Equal(3, r.Output.Split(Begin).Length - 1);
    }
}
