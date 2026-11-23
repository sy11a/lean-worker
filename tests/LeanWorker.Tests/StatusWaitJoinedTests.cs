using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A unit of `status --wait` is judged by every row that names its first round, also the ones the layout sets apart
/// (docs/research/live-status/README.md, sections 4.1 and 4.2; the review of #147, finding 1): a round whose status.json
/// is missing or unreadable has no chain id and falls out of the chain, but its pointer names the chain's launcher; a
/// round by another launcher with the chain's id sets itself apart. The unit ends only when every such row has, and a
/// row that is not a success makes it failed; one that never ends gives it up after --grace (exit 8, or 7 when --timeout
/// is shorter). A joined row only ever makes the verdict stricter.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitJoinedTests : IDisposable
{
    private const string FirstId = "20261122-115000-chain-w1";
    private const string SecondId = "20261122-115300-chain-w1";

    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(2);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    // Round 1 of a chain, its launcher alive.
    private StatusLiveRig.Live Round1() => _rig.Add("chain-w1", startedAgo: TimeSpan.FromMinutes(10), chain: FirstId, id: FirstId);

    // Round 2, run by the same launcher; its status file is hurt as <how> says when it is not "plain".
    private StatusLiveRig.Live Round2(StatusLiveRig.Live first, string how)
    {
        StatusLiveRig.Live second = _rig.Add("chain-w1", startedAgo: TimeSpan.FromMinutes(7), chain: FirstId, round: 2, id: SecondId, launcher: first.Pid);
        if (how is "missing")
        {
            StatusLiveRig.Unwrite(second);
        }
        else if (how is "unreadable")
        {
            StatusLiveRig.Garble(second);
        }

        return second;
    }

    // The review's scenario: the wait starts with round 1 alone; at 10 s its gate says continue (its end is recorded, its
    // launcher lives on) and round 2 appears, hurt as <how> says; at 40 s the launcher ends.
    private StatusLiveRig.Live SplitChain(string how)
    {
        StatusLiveRig.Live first = Round1();
        StatusLiveRig.Live? second = null;
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first);
            second = Round2(first, how);
        });
        _rig.At(Seconds(40), () => _rig.End(first));
        return first;
    }

    // A round of the chain that its launcher did not run (another launcher, the chain's id), added at 10 s and made to
    // end as <how> says at 20 s; round 1's launcher ends at 40 s.
    private void ForeignRound(string how)
    {
        StatusLiveRig.Live first = Round1();
        StatusLiveRig.Live? other = null;
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first);
            other = _rig.Add("split-round-2", startedAgo: TimeSpan.FromMinutes(7), chain: FirstId, round: 2, id: SecondId);
        });
        _rig.At(Seconds(20), () =>
        {
            if (how is "unlisted")
            {
                _rig.Unlist(other!);
            }
            else if (how is "unknown")
            {
                _rig.Blur(other!);
            }
            else if (how is not "live")
            {
                StatusWaitScenarios.Apply(_rig, other!, how);
            }
        });
        _rig.At(Seconds(40), () => _rig.End(first));
    }

    // --grace 2m, and --timeout 30m unless the test gives its own.
    private async Task<StatusLiveRig.Result> WaitAsync(params string[] more)
    {
        List<string> args = ["--owner", StatusLiveRig.Key, "--wait", "--grace", "2m"];
        if (!more.Contains("--timeout", StringComparer.Ordinal))
        {
            args.AddRange(["--timeout", "30m"]);
        }

        args.AddRange(more);
        StatusLiveRig.Result result = await _rig.CallAsync([.. args]);
        return result;
    }

    [Fact]
    public async Task A_round_without_a_status_file_that_names_the_chains_launcher_makes_a_clean_first_round_a_failure_not_a_successAsync()
    {
        _ = SplitChain("missing");

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Null(r.Error);
        Assert.Equal(1, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40), Seconds(42));
        // Round 1 is a clean success and its row says so; round 2 is the joined row, gone with its launcher.
        Assert.Equal(5, r.Lines.Length);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains(r.Lines[2..4], l => l.StartsWith("success", StringComparison.Ordinal));
        Assert.Contains(r.Lines[2..4], l => l.StartsWith("gone", StringComparison.Ordinal));
        Assert.EndsWith("(exit 1)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_with_an_unreadable_status_file_that_names_the_chains_launcher_holds_the_wait_through_the_grace_then_exit_8Async()
    {
        _ = SplitChain("unreadable");

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Null(r.Error);
        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40) + _grace, Seconds(42) + _grace);
        Assert.Equal(5, r.Lines.Length);
        Assert.Contains(r.Lines[2..4], l => l.StartsWith("success", StringComparison.Ordinal));
        Assert.Contains(r.Lines[2..4], l => l.StartsWith("unreadable", StringComparison.Ordinal));
        Assert.EndsWith("(exit 8)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_joined_row_that_never_ends_gives_exit_7_when_the_timeout_is_shorter_than_the_graceAsync()
    {
        _ = SplitChain("unreadable");

        StatusLiveRig.Result r = await WaitAsync("--timeout", "100s");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(100), _rig.Elapsed);
        Assert.Contains("1 of 1 runs not ended", r.Output, StringComparison.Ordinal);
        Assert.Contains(r.Lines, l => l.StartsWith("unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_round_that_failed_is_a_failure_even_when_the_first_round_ended_clean_and_the_launcher_ran_the_next_oneAsync()
    {
        // The honest chain of the existing tests (round 2 has its status file) is not touched: a failed last round gives 1.
        StatusLiveRig.Live first = Round1();
        StatusLiveRig.Live? second = null;
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first);
            second = Round2(first, "plain");
        });
        _rig.At(Seconds(40), () => _rig.End(second!, "error"));

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Equal(1, r.Code);
        Assert.Equal(4, r.Lines.Length);
    }

    [Theory]
    [InlineData("success", 0)]
    [InlineData("exit0", 0)]
    [InlineData("noexit", 0)]
    [InlineData("error", 1)]
    [InlineData("timeout", 1)]
    [InlineData("signalled", 1)]
    [InlineData("exit4", 1)]
    [InlineData("exit5", 1)]
    [InlineData("wrapped-up", 1)]
    [InlineData("gone", 1)]
    [InlineData("lost", 1)]
    [InlineData("orphaned", 1)]
    public async Task A_round_by_another_launcher_with_the_chains_id_ends_the_unit_as_that_row_endedAsync(string how, int code)
    {
        ForeignRound(how);

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40), Seconds(42));
        // The joined row is in the listing next to the first round's.
        Assert.Equal(5, r.Lines.Length);
        Assert.Contains("split-round-2", r.Output, StringComparison.Ordinal);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("unknown")]
    [InlineData("unlisted")]
    public async Task A_round_by_another_launcher_that_never_ends_or_vanishes_gives_the_unit_up_after_the_grace_with_exit_8Async(string how)
    {
        ForeignRound(how);

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40) + _grace, Seconds(42) + _grace);
        Assert.Contains("split-round-2", r.Output, StringComparison.Ordinal);
        Assert.EndsWith("(exit 8)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_by_another_launcher_that_is_still_live_when_the_first_round_ends_gives_exit_7_when_the_timeout_is_shorterAsync()
    {
        ForeignRound("live");

        StatusLiveRig.Result r = await WaitAsync("--timeout", "90s");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(90), _rig.Elapsed);
    }

    [Fact]
    public async Task A_row_that_names_the_units_launcher_but_has_a_chain_of_its_own_joins_the_unit_and_a_failed_one_turns_0_into_1Async()
    {
        StatusLiveRig.Live a = _rig.Add("plain-run");
        StatusLiveRig.Live? forged = null;
        _rig.At(Seconds(5), () => forged = _rig.Add("forged-by-launcher", launcher: a.Pid));
        _rig.At(Seconds(20), () => _rig.End(a));

        StatusLiveRig.Result r = await WaitAsync();

        // The forged row has no end; when the launcher it names is gone it reads lost, a failure.
        Assert.NotNull(forged);
        Assert.Equal(1, r.Code);
        Assert.Contains("forged-by-launcher", r.Output, StringComparison.Ordinal);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_joined_row_that_is_a_success_leaves_the_exit_0_as_it_wasAsync()
    {
        StatusLiveRig.Live a = _rig.Add("plain-run");
        StatusLiveRig.Live? friend = null;
        _rig.At(Seconds(5), () => friend = _rig.Add("friend-by-launcher", launcher: a.Pid));
        _rig.At(Seconds(10), () => _rig.Record(friend!));
        _rig.At(Seconds(20), () => _rig.End(a));

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Equal(0, r.Code);
        Assert.Contains("friend-by-launcher", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_that_was_there_before_the_wait_and_is_left_out_of_the_scope_by_started_after_still_joins_by_chain_idAsync()
    {
        // The row is not a unit (it started before the time) and it ended before the wait: it still names the run as its chain.
        StatusLiveRig.Live a = _rig.Add("plain-run", startedAgo: TimeSpan.FromMinutes(10));
        _ = _rig.Idx.Add("old-forged", [StatusLiveRig.Key], endedAgo: TimeSpan.FromMinutes(1), startedAgo: TimeSpan.FromHours(3), status: "error",
            chain: a.Run.Id, round: 2, id: "20261122-090000-old-forged");
        _rig.At(Seconds(20), () => _rig.End(a));

        StatusLiveRig.Result r = await WaitAsync("--started-after", StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromMinutes(30)));

        Assert.Equal(1, r.Code);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("old-forged", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_any_a_unit_whose_joined_row_failed_wakes_the_wait_with_exit_1_while_another_run_is_still_liveAsync()
    {
        _ = SplitChain("missing");
        _ = _rig.Add("plain-run");

        StatusLiveRig.Result r = await WaitAsync("--any", "--timeout", "10m");

        Assert.Equal(1, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40), Seconds(42));
        Assert.Contains("plain-run", r.Output, StringComparison.Ordinal);
        Assert.Contains(r.Lines, l => l.StartsWith("gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_any_a_unit_whose_joined_row_has_not_ended_is_not_ended_and_does_not_wake_the_waitAsync()
    {
        _ = SplitChain("unreadable");
        StatusLiveRig.Live plain = _rig.Add("plain-run");
        _rig.At(Seconds(60), () => _rig.End(plain));

        StatusLiveRig.Result r = await WaitAsync("--any");

        // At 40 s the chain's own rows are over, but its joined row is not: only the plain run's end returns the wait.
        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(60), _rig.Elapsed);
    }

    [Fact]
    public async Task With_any_a_unit_alone_with_a_joined_row_that_never_ends_is_given_up_after_the_grace_not_a_successAsync()
    {
        _ = SplitChain("unreadable");

        StatusLiveRig.Result r = await WaitAsync("--any");

        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40) + _grace, Seconds(42) + _grace);
    }
}
