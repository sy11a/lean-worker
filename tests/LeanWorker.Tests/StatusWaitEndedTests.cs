using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// What "ended" is for `status --wait` (docs/research/live-status/README.md, sections 4.1, 4.2 and 5), one case per
/// derived state: a final state, gone, lost and orphaned end the wait; a live run, one that is ending and one in its gate
/// hold it for ever; unknown, unreadable and shared-dir rows and a run that drops out of the listing hold it until
/// --grace and then end it with exit 8.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitEndedTests : IDisposable
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    [Theory]
    [InlineData("success", 0)]
    [InlineData("error", 1)]
    [InlineData("timeout", 1)]
    [InlineData("wrapped-up", 3)]
    [InlineData("signalled", 1)]
    [InlineData("gone", 1)]
    [InlineData("lost", 1)]
    [InlineData("orphaned", 1)]
    public async Task A_run_in_a_final_or_gone_or_lost_or_orphaned_state_ends_the_waitAsync(string how, int code)
    {
        StatusLiveRig.Live run = _rig.Add("the-run");
        _rig.At(Seconds(10), () => StatusWaitScenarios.Apply(_rig, run, how));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(10), Seconds(12));
        Assert.StartsWith(StatusWaitScenarios.StateWord(how), r.Lines[2], StringComparison.Ordinal);
        Assert.EndsWith("(exit " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", r.Lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("wrap-up")]
    [InlineData("ending")]
    [InlineData("gate")]
    [InlineData("finishing")]
    public async Task A_live_run_holds_the_wait_for_ever_and_the_grace_never_gives_it_upAsync(string state)
    {
        StatusLiveRig.Live run = _rig.Add("the-run", tweak: d =>
        {
            d["phase"] = state switch { "running" or "wrap-up" => "worker", "ending" => "done", _ => state };
            d["live"]!["wrapped_up"] = state is "wrap-up";
            d["updated_at"] = StatusDocs.Stamp(StatusDocs.Now);
        });

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "1m", "--timeout", "10m");

        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(10), _rig.Elapsed);
        Assert.StartsWith(state, r.Lines[2], StringComparison.Ordinal);
        Assert.True(Directory.Exists(run.Dir));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unreadable")]
    [InlineData("shared-dir")]
    public async Task An_unsure_run_holds_the_wait_until_the_grace_and_then_the_wait_ends_with_exit_8Async(string how)
    {
        StatusLiveRig.Live finisher = _rig.Add("finishes");
        _rig.At(Seconds(10), () => _rig.End(finisher));
        if (how is "unknown")
        {
            _rig.Blur(_rig.Add("unsure"));
        }
        else if (how is "unreadable")
        {
            StatusLiveRig.Garble(_rig.Add("unsure"));
        }
        else
        {
            _ = _rig.Add("twin", id: "20261122-115500-twin");
            _ = _rig.Add("twin", id: "20261122-115500-twin");
        }

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "3m", "--timeout", "30m", "--interval", "30");

        Assert.Null(r.Error);
        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, _grace, _grace + _interval);
        Assert.Contains(how, r.Output, StringComparison.Ordinal);
        Assert.EndsWith("(exit 8)", r.Lines[^1], StringComparison.Ordinal);
        Assert.Contains("finishes", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_drops_out_of_the_listing_holds_the_wait_until_the_grace_counted_from_when_it_droppedAsync()
    {
        StatusLiveRig.Live dropped = _rig.Add("vanishes-from-the-index");
        _rig.At(Seconds(60), () => _rig.Unlist(dropped));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "3m", "--timeout", "30m", "--interval", "30");

        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(60) + _grace, Seconds(60) + _grace + _interval);
        Assert.Contains("vanishes-from-the-index", r.Output, StringComparison.Ordinal);
        Assert.Contains("unknown", r.Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsure_run_that_ends_before_the_grace_ends_the_wait_with_its_own_verdictAsync()
    {
        StatusLiveRig.Live unsure = _rig.Add("unsure-then-done");
        _rig.Blur(unsure);
        _rig.At(Seconds(60), () => _rig.End(unsure, "error"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "3m", "--interval", "30");

        Assert.Equal(1, r.Code);
        Assert.Equal(Seconds(60), _rig.Elapsed);
    }

    [Fact]
    public async Task The_grace_counts_how_long_a_run_stays_unsure_it_starts_over_when_the_run_is_seen_alive_againAsync()
    {
        StatusLiveRig.Live flaky = _rig.Add("flaky");
        _rig.Blur(flaky);
        _rig.At(Seconds(120), () =>
        {
            _rig.Idx.Proc.Remove(flaky.Pid);
            _rig.Idx.Proc.Add(flaky.Pid, StatusDocs.LauncherTicks);
        });
        _rig.At(Seconds(150), () => _rig.Blur(flaky));
        _rig.At(Seconds(300), () => _rig.End(flaky));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "3m", "--timeout", "30m", "--interval", "30");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(300), _rig.Elapsed);
    }

    [Fact]
    public async Task A_given_up_run_is_listed_with_its_state_and_why_it_is_unsureAsync()
    {
        _rig.Blur(_rig.Add("unsure"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "1m");

        Assert.Equal(8, r.Code);
        Assert.StartsWith("unknown", r.Lines[2], StringComparison.Ordinal);
        Assert.Contains("launcher state unknown", r.Lines[2], StringComparison.Ordinal);
        Assert.Contains("0 ended, 1 needs attention", r.Lines[1], StringComparison.Ordinal);
    }
}
