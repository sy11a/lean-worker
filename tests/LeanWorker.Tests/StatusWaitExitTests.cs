using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The exit codes of `status --wait` and their priority (docs/research/live-status/README.md, section 4.2): 0 every run
/// ended success; 1 any run failed (over 8 and 3), gone and a success end that records exit code 4 or 5 included; 3 none
/// failed and one wrapped up; 6 nothing matched; 7 timeout; 8 given up; --any returns at the first run that ended with the
/// verdict of what has ended.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitExitTests : IDisposable
{
    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    [Theory]
    [InlineData("success", 0)]
    [InlineData("success success success", 0)]
    [InlineData("success exit0", 0)]
    [InlineData("success noexit", 0)]
    [InlineData("success wrapped-up", 3)]
    [InlineData("wrapped-up wrapped-up", 3)]
    [InlineData("wrapped-up success wrapped-up", 3)]
    [InlineData("success error", 1)]
    [InlineData("error success", 1)]
    [InlineData("success timeout", 1)]
    [InlineData("success signalled", 1)]
    [InlineData("wrapped-up error", 1)]
    [InlineData("error wrapped-up success", 1)]
    [InlineData("success gone", 1)]
    [InlineData("gone wrapped-up", 1)]
    [InlineData("success lost", 1)]
    [InlineData("success orphaned", 1)]
    [InlineData("success exit4", 1)]
    [InlineData("success exit5", 1)]
    [InlineData("wrapped-up exit4", 1)]
    [InlineData("success unknown", 8)]
    [InlineData("success success unknown", 8)]
    [InlineData("error unknown", 1)]
    [InlineData("success error unknown", 1)]
    [InlineData("gone unknown", 1)]
    [InlineData("exit4 unknown", 1)]
    public async Task The_exit_code_is_what_the_runs_ended_as_and_the_priority_is_1_then_8_then_3_then_0Async(string table, int code)
    {
        _ = StatusWaitScenarios.Run(_rig, table);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "1m", "--timeout", "30m");

        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.EndsWith("(exit " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", r.Lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exit4")]
    [InlineData("exit5")]
    public async Task A_success_end_that_records_exit_code_4_or_5_is_listed_as_success_and_counts_as_failedAsync(string how)
    {
        _ = StatusWaitScenarios.Run(_rig, how);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(1, r.Code);
        Assert.StartsWith("success", r.Lines[2], StringComparison.Ordinal);
        Assert.Contains("1 of 1 runs ended not success", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_owner_is_exit_6_never_a_silent_0_and_it_does_not_sleepAsync()
    {
        _ = _rig.AddEnded("someones-run");
        _ = _rig.Add("someones-live-run");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", "nobody:x", "--wait");

        Assert.Equal(6, r.Code);
        Assert.Empty(_rig.Pauses);
        Assert.Contains("no run in owner nobody:x", r.Output, StringComparison.Ordinal);
        Assert.EndsWith("(exit 6)", r.Lines[^1], StringComparison.Ordinal);
        _ = Assert.Single(r.Lines);
    }

    [Fact]
    public async Task An_empty_index_is_exit_6Async()
    {
        StatusLiveRig.Result r = await _rig.CallAsync("--all", "--wait");

        Assert.Equal(6, r.Code);
        Assert.Empty(_rig.Pauses);
    }

    [Fact]
    public async Task A_scope_whose_runs_have_all_ended_already_has_nothing_to_wait_on_and_is_exit_6Async()
    {
        _ = _rig.AddEnded("ended-1");
        _ = _rig.AddEnded("ended-2", "error");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(6, r.Code);
        Assert.Empty(_rig.Pauses);
    }

    [Fact]
    public async Task A_start_time_that_no_run_meets_is_exit_6_and_the_message_names_the_timeAsync()
    {
        _ = _rig.Add("started-lately", startedAgo: TimeSpan.FromMinutes(5));
        string after = StatusDocs.Stamp(StatusDocs.Now + TimeSpan.FromHours(1));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", after);

        Assert.Equal(6, r.Code);
        Assert.Contains("started at or after " + after, r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_is_exit_7_even_when_a_run_ended_failedAsync()
    {
        StatusLiveRig.Live bad = _rig.Add("fails");
        _ = _rig.Add("keeps-going");
        _rig.At(Seconds(10), () => _rig.End(bad, "error"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--timeout", "1m");

        Assert.Equal(7, r.Code);
    }

    [Theory]
    [InlineData("success", 0)]
    [InlineData("wrapped-up", 3)]
    [InlineData("error", 1)]
    [InlineData("gone", 1)]
    [InlineData("lost", 1)]
    [InlineData("exit4", 1)]
    public async Task With_any_the_wait_returns_at_the_first_run_that_ended_with_the_verdict_of_that_runAsync(string how, int code)
    {
        StatusLiveRig.Live first = _rig.Add("ends-first");
        _ = _rig.Add("keeps-going-1");
        _ = _rig.Add("keeps-going-2");
        _rig.At(Seconds(20), () => StatusWaitScenarios.Apply(_rig, first, how));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any");

        Assert.Equal(code, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(20), Seconds(22));
        Assert.Contains("ends-first", r.Output, StringComparison.Ordinal);
        Assert.Contains("keeps-going-1", r.Output, StringComparison.Ordinal);
        Assert.Contains("keeps-going-2", r.Output, StringComparison.Ordinal);
        Assert.Contains("waiting on 3 runs", r.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_any_the_verdict_is_of_what_has_ended_not_of_the_runs_still_runningAsync()
    {
        StatusLiveRig.Live first = _rig.Add("ends-first");
        StatusLiveRig.Live second = _rig.Add("would-fail-later");
        _rig.At(Seconds(10), () => _rig.End(first));
        _rig.At(Seconds(100), () => _rig.End(second, "error"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(10), _rig.Elapsed);
        Assert.Contains("1 live, 1 ended", r.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_any_and_two_runs_ended_at_that_poll_a_failed_one_makes_it_exit_1Async()
    {
        StatusLiveRig.Live a = _rig.Add("ends-ok");
        StatusLiveRig.Live b = _rig.Add("ends-bad");
        _ = _rig.Add("keeps-going");
        _rig.At(Seconds(10), () => _rig.End(a));
        _rig.At(Seconds(10), () => _rig.End(b, "error"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any");

        Assert.Equal(1, r.Code);
    }

    [Fact]
    public async Task With_any_and_nothing_ending_the_timeout_is_exit_7Async()
    {
        _ = _rig.Add("keeps-going-1");
        _ = _rig.Add("keeps-going-2");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any", "--timeout", "2m");

        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(2), _rig.Elapsed);
    }

    [Fact]
    public async Task With_any_a_run_given_up_does_not_count_as_ended_and_the_others_still_decideAsync()
    {
        _rig.Blur(_rig.Add("unsure"));
        StatusLiveRig.Live other = _rig.Add("ends-later");
        _rig.At(Seconds(30), () => _rig.End(other));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any", "--grace", "10m");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(30), _rig.Elapsed);
    }
}
