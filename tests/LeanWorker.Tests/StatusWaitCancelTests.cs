using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A `status --wait` that the host's token stops ends promptly and without an exception, with a last line and exit 130
/// (128 plus SIGINT, the code an interrupted wait already ends with); never 0, whatever phase it was in and whatever
/// the runs had just done (the review of #147, and the tests worker's finding on a cancelled wait).
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitCancelTests : IDisposable
{
    private const int Interrupted = 130;

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    // The expect phase (fewer runs than --expect) or the poll phase (the scope is fixed and its run never ends).
    private string[] Args(bool expectPhase)
    {
        _ = _rig.Add("never-ends");
        return expectPhase
            ? ["--owner", StatusLiveRig.Key, "--wait", "--expect", "2", "--grace", "10m", "--timeout", "60m"]
            : ["--owner", StatusLiveRig.Key, "--wait", "--timeout", "60m"];
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_cancelled_wait_ends_with_exit_130_and_a_last_line_and_no_exception_in_both_phasesAsync(bool expectPhase, bool cancelBeforeTheCall)
    {
        string[] args = Args(expectPhase);
        if (cancelBeforeTheCall)
        {
            _rig.CancelHost();
        }
        else
        {
            _rig.At(Seconds(10), () => _rig.CancelHost());
        }

        StatusLiveRig.Result r = await _rig.CallAsync(args);

        Assert.Null(r.Error);
        Assert.Equal(Interrupted, r.Code);
        Assert.True(_rig.Passed <= Seconds(10), $"the wait went on for {_rig.Passed}");
        Assert.StartsWith("lean-worker status: wait stopped before it was over", r.Lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("(exit 130)", r.Lines[^1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, r.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wait_cancelled_at_the_moment_its_run_ends_is_still_exit_130_never_0Async(bool any)
    {
        StatusLiveRig.Live run = _rig.Add("ends-with-the-cancel");
        _rig.At(Seconds(10), () =>
        {
            _rig.End(run);
            _rig.CancelHost();
        });
        string[] args = any ? ["--owner", StatusLiveRig.Key, "--wait", "--any"] : ["--owner", StatusLiveRig.Key, "--wait"];

        StatusLiveRig.Result r = await _rig.CallAsync(args);

        Assert.Null(r.Error);
        Assert.Equal(Interrupted, r.Code);
        Assert.EndsWith("(exit 130)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wait_that_is_not_cancelled_never_says_it_was_stoppedAsync()
    {
        StatusLiveRig.Live run = _rig.Add("ends-by-itself");
        _rig.At(Seconds(10), () => _rig.End(run));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("wait stopped", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_code_of_a_cancelled_wait_is_not_one_of_the_codes_the_waiter_gives_for_its_own_outcomes()
    {
        int[] own = [StatusWait.AllSuccess, StatusWait.SomeFailed, StatusWait.SomeWrappedUp, StatusWait.NothingMatched, StatusWait.TimedOut, StatusWait.Unresolved];

        Assert.Equal(Interrupted, StatusWait.Interrupted);
        Assert.DoesNotContain(StatusWait.Interrupted, own);
    }
}
