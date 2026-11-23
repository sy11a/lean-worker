using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The scope of `status --wait` (docs/research/live-status/README.md, section 4.2, "How --wait bounds itself"): fixed
/// when the wait starts, or after --expect runs appeared; --started-after; the interval, the timeout and the grace
/// against the fake clock; the scopes --run, --here and the caller's own; the legacy rows it skips.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitScopeTests : IDisposable
{
    private static readonly string _t0 = StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromMinutes(30));

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    [Fact]
    public async Task A_run_that_appears_after_the_wait_started_is_not_waited_on_and_not_in_the_resultAsync()
    {
        StatusLiveRig.Live first = _rig.Add("first-run");
        _rig.At(Seconds(10), () => _ = _rig.Add("late-run"));
        _rig.At(Seconds(20), () => _rig.End(first));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(20), Seconds(22));
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("first-run", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("late-run", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_had_already_ended_when_the_wait_started_is_not_in_its_scope_without_started_afterAsync()
    {
        _ = _rig.AddEnded("already-failed", "error");
        StatusLiveRig.Live live = _rig.Add("live-one");
        _rig.At(Seconds(10), () => _rig.End(live));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("already-failed", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_expect_the_wait_takes_runs_as_they_appear_until_there_are_n_then_fixes_the_scopeAsync()
    {
        StatusLiveRig.Live r1 = _rig.Add("batch-1");
        StatusLiveRig.Live? r2 = null;
        StatusLiveRig.Live? r3 = null;
        _rig.At(Seconds(10), () => r2 = _rig.Add("batch-2"));
        _rig.At(Seconds(30), () => r3 = _rig.Add("batch-3"));
        _rig.At(Seconds(40), () => _ = _rig.Add("batch-4-too-late"));
        _rig.At(Seconds(50), () => _rig.End(r1));
        _rig.At(Seconds(55), () => _rig.End(r2!));
        _rig.At(Seconds(60), () => _rig.End(r3!));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "3", "--started-after", _t0);

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(60), Seconds(62));
        Assert.Contains("waiting on 3 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("batch-1", r.Output, StringComparison.Ordinal);
        Assert.Contains("batch-3", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("batch-4", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_expect_n_runs_already_in_scope_start_the_wait_at_once_and_take_every_run_there_isAsync()
    {
        StatusLiveRig.Live[] runs = [_rig.Add("a-1"), _rig.Add("a-2"), _rig.Add("a-3")];
        _rig.At(Seconds(10), () => _rig.End(runs[0]));
        _rig.At(Seconds(10), () => _rig.End(runs[1]));
        _rig.At(Seconds(20), () => _rig.End(runs[2]));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "2", "--started-after", _t0);

        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 3 runs", r.Lines[0], StringComparison.Ordinal);
        Assert.InRange(_rig.Elapsed, Seconds(20), Seconds(22));
    }

    [Fact]
    public async Task With_expect_fewer_than_n_at_the_grace_is_exit_8_and_names_the_shortfallAsync()
    {
        _ = _rig.Add("only-one");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "3", "--started-after", _t0, "--grace", "2m", "--timeout", "60m");

        Assert.Null(r.Error);
        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + Seconds(2));
        Assert.Contains("only 1 of 3 expected runs appeared", r.Output, StringComparison.Ordinal);
        Assert.Contains("only-one", r.Output, StringComparison.Ordinal);
        Assert.EndsWith("(exit 8)", r.Lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("waiting on", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_expect_and_no_run_at_all_the_shortfall_is_exit_8_not_6Async()
    {
        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "2", "--started-after", _t0, "--grace", "1m");

        Assert.Equal(8, r.Code);
        Assert.Contains("only 0 of 2 expected runs appeared", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_expect_a_timeout_that_comes_before_the_grace_is_exit_7Async()
    {
        _ = _rig.Add("only-one");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--expect", "3", "--started-after", _t0, "--grace", "10m", "--timeout", "1m");

        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(1), _rig.Elapsed);
        Assert.Contains("timed out", r.Output, StringComparison.Ordinal);
        Assert.EndsWith("(exit 7)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_started_before_started_after_are_out_of_scopeAsync()
    {
        StatusLiveRig.Live old = _rig.Add("started-long-ago", startedAgo: TimeSpan.FromHours(3));
        StatusLiveRig.Live fresh = _rig.Add("started-lately", startedAgo: TimeSpan.FromMinutes(5));
        _rig.At(Seconds(10), () => _rig.End(fresh));
        string after = StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromHours(1));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", after);

        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("started-lately", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("started-long-ago", r.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(old.Dir, "status.json")));
    }

    [Fact]
    public async Task A_run_that_ended_before_the_wait_started_but_started_after_the_time_still_countsAsync()
    {
        _ = _rig.AddEnded("ended-failed", "error", startedAgo: TimeSpan.FromMinutes(20));
        StatusLiveRig.Live live = _rig.Add("live-one", startedAgo: TimeSpan.FromMinutes(5));
        _rig.At(Seconds(10), () => _rig.End(live));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", _t0);

        Assert.Equal(1, r.Code);
        Assert.Contains("waiting on 2 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("ended-failed", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_every_run_after_the_time_has_already_ended_the_wait_returns_at_once_without_sleepingAsync()
    {
        _ = _rig.AddEnded("done-1", startedAgo: TimeSpan.FromMinutes(20));
        _ = _rig.AddEnded("done-2", startedAgo: TimeSpan.FromMinutes(15));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", _t0);

        Assert.Equal(0, r.Code);
        Assert.Empty(_rig.Pauses);
        Assert.Contains("done-1", r.Output, StringComparison.Ordinal);
        Assert.Contains("done-2", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pauses_between_reads_are_the_interval_and_the_default_is_2_secondsAsync()
    {
        StatusLiveRig.Live run = _rig.Add("slow");
        _rig.At(Seconds(9), () => _rig.End(run));

        StatusLiveRig.Result defaults = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, defaults.Code);
        Assert.All(_rig.Pauses, p => Assert.Equal(Seconds(2), p));
        Assert.Equal(5, _rig.Pauses.Count);
    }

    [Fact]
    public async Task An_interval_of_5_seconds_is_the_pause_between_readsAsync()
    {
        StatusLiveRig.Live run = _rig.Add("slow");
        _rig.At(Seconds(12), () => _rig.End(run));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--interval", "5");

        Assert.Equal(0, r.Code);
        Assert.All(_rig.Pauses, p => Assert.Equal(Seconds(5), p));
        Assert.Equal(Seconds(15), _rig.Elapsed);
    }

    [Fact]
    public async Task Without_a_timeout_the_wait_gives_up_after_120_minutes_with_exit_7Async()
    {
        _ = _rig.Add("never-ends");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--interval", "600");

        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(120), _rig.Elapsed);
        Assert.Contains("never-ends", r.Output, StringComparison.Ordinal);
        Assert.Contains("running", r.Output, StringComparison.Ordinal);
        Assert.EndsWith("(exit 7)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_ends_the_wait_exactly_when_it_passes_even_if_the_interval_does_not_divide_itAsync()
    {
        _ = _rig.Add("never-ends");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--interval", "7", "--timeout", "30s");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(30), _rig.Elapsed);
        Assert.Equal(Seconds(2), _rig.Pauses[^1]);
    }

    [Fact]
    public async Task A_timeout_with_one_run_ended_and_one_live_is_exit_7_not_0Async()
    {
        StatusLiveRig.Live done = _rig.Add("finishes");
        _ = _rig.Add("keeps-going");
        _rig.At(Seconds(10), () => _rig.End(done));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--timeout", "1m");

        Assert.Equal(7, r.Code);
        Assert.Contains("1 of 2 runs not ended", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_callers_own_runs_are_the_default_scopeAsync()
    {
        StatusLiveRig.Live mine = _rig.Add("mine", "claude-session:sess-A");
        _ = _rig.Add("not-mine", "claude-session:sess-B");
        _rig.At(Seconds(10), () => _rig.End(mine));

        StatusLiveRig.Result r = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--wait");

        Assert.Equal(0, r.Code);
        Assert.Contains("mine", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("not-mine", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_scope_and_without_a_caller_identity_a_wait_is_exit_2_and_never_sleepsAsync()
    {
        _ = _rig.Add("someone");

        StatusLiveRig.Result r = await _rig.CallAsync("--wait");

        Assert.Equal(2, r.Code);
        Assert.Contains("cannot tell which session you are", r.Output, StringComparison.Ordinal);
        Assert.Empty(_rig.Pauses);
    }

    [Fact]
    public async Task A_wait_on_one_run_dir_waits_for_that_runAsync()
    {
        StatusLiveRig.Live run = _rig.Add("the-run");
        _ = _rig.Add("another-run");
        _rig.At(Seconds(10), () => _rig.End(run));

        StatusLiveRig.Result r = await _rig.CallAsync("--run", run.Dir, "--wait");

        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("another-run", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wait_on_a_run_dir_that_does_not_exist_is_exit_2Async()
    {
        StatusLiveRig.Result r = await _rig.CallAsync("--run", Path.Combine(_rig.Idx.Root, "no-such-run"), "--wait");

        Assert.Equal(2, r.Code);
        Assert.Contains("no run dir", r.Output, StringComparison.Ordinal);
        Assert.Empty(_rig.Pauses);
    }

    [Fact]
    public async Task Legacy_rows_are_not_waited_on_and_the_first_line_says_how_many_were_skippedAsync()
    {
        StatusLiveRig.Live live = _rig.Add("live-one", startedAgo: TimeSpan.FromMinutes(5));
        _ = StatusDocs.WriteRun(_rig.Idx.Root, "20261122-110000-old-one", doc: null);
        _ = StatusDocs.WriteRun(_rig.Idx.Root, "20261122-111000-old-two", doc: null, summaryStatus: "success");
        _rig.At(Seconds(10), () => _rig.End(live));

        StatusLiveRig.Result r = await _rig.CallAsync("--here", "--runs-root", _rig.Idx.Root, "--wait");

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("2 legacy runs not waited on", r.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_with_only_legacy_rows_has_nothing_to_wait_on_and_is_exit_6Async()
    {
        _ = StatusDocs.WriteRun(_rig.Idx.Root, "20261122-110000-old-one", doc: null);

        StatusLiveRig.Result r = await _rig.CallAsync("--here", "--runs-root", _rig.Idx.Root, "--wait");

        Assert.Equal(6, r.Code);
        Assert.Empty(_rig.Pauses);
    }
}
