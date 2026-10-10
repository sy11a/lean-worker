using Xunit;

namespace LeanWorker.Tests;

public class GateChainTests
{
    private static Gate.GateResult Result(string outcome)
    {
        int exitCode = outcome switch
        {
            "clean" => 0,
            "error" => 2,
            _ => 1,
        };
        return new(outcome, exitCode, Count: null, ReportPath: null, Feedback: string.Empty, Error: null, Duration: TimeSpan.Zero);
    }

    [Fact]
    public void Clean_outcome_decides_clean() =>
        Assert.Equal(GateChain.Clean, GateChain.Decide(Result("clean"), [0], round: 1, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));

    [Fact]
    public void Error_outcome_decides_error() =>
        Assert.Equal(GateChain.Error, GateChain.Decide(Result("error"), [0], round: 1, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));

    [Fact]
    public void Findings_with_room_to_run_continues() =>
        Assert.Equal(GateChain.Continue, GateChain.Decide(Result("findings"), [3], round: 1, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));

    [Fact]
    public void Findings_3_then_2_continues() =>
        Assert.Equal(GateChain.Continue, GateChain.Decide(Result("findings"), [3, 2], round: 2, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));

    [Fact]
    public void Three_three_three_is_stuck()
    {
        Assert.Equal(GateChain.Stuck, GateChain.Decide(Result("findings"), [3, 3, 3], round: 3, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
        Assert.Equal("no decrease in 2 rounds", GateChain.StuckReason([3, 3, 3], round: 3, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
    }

    [Fact]
    public void Three_four_four_is_stuck() =>
        Assert.Equal(GateChain.Stuck, GateChain.Decide(Result("findings"), [3, 4, 4], round: 3, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));

    [Fact]
    public void Three_three_two_is_not_stuck()
    {
        Assert.Equal(GateChain.Continue, GateChain.Decide(Result("findings"), [3, 3, 2], round: 3, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
        Assert.Null(GateChain.StuckReason([3, 3, 2], round: 3, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
    }

    [Fact]
    public void Only_one_non_decrease_at_two_rounds_is_not_stuck()
    {
        // 3 -> 3 is only one non-decreasing step; "no decrease in two consecutive rounds" needs three counts.
        Assert.Equal(GateChain.Continue, GateChain.Decide(Result("findings"), [3, 3], round: 2, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
    }

    [Fact]
    public void Stuck_when_round_reaches_max_rounds()
    {
        Assert.Equal(GateChain.Stuck, GateChain.Decide(Result("findings"), [5, 4], round: 2, maxRounds: 2, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
        Assert.Equal("max rounds", GateChain.StuckReason([5, 4], round: 2, maxRounds: 2, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
    }

    [Fact]
    public void Stuck_when_chain_cost_reaches_the_cap()
    {
        Assert.Equal(GateChain.Stuck, GateChain.Decide(Result("findings"), [5, 4], round: 2, maxRounds: 10, chainCostIncludingThis: 10m, maxTotalUsd: 10m));
        Assert.Equal("cost cap $10", GateChain.StuckReason([5, 4], round: 2, maxRounds: 10, chainCostIncludingThis: 10m, maxTotalUsd: 10m));
    }

    [Fact]
    public void Unknown_outcome_throws() =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GateChain.Decide(Result("bogus"), [1], round: 1, maxRounds: 5, chainCostIncludingThis: 0m, maxTotalUsd: 10m));
}
