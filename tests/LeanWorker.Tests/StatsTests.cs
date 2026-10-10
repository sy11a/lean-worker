using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class StatsTests
{
    private static JsonObject R(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Rows_group_by_profile_and_model_and_count_success_wrap_ups_escalations_and_quota()
    {
        List<StatsRow> rows = Commands.StatsRows([
            R(/*lang=json,strict*/ """{"profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"success","total_cost_usd":1.0,"quota_used_pct":{"5h":4,"weekly":1}}"""),
            R(/*lang=json,strict*/ """{"profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"no-result","total_cost_usd":0.5,"escalate_to":"claude-sonnet-5","quota_used_pct":{"5h":2}}"""),
            R(/*lang=json,strict*/ """{"profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"wrapped-up","total_cost_usd":0.5}"""),
            R(/*lang=json,strict*/ """{"profile":"code","model":"claude-sonnet-5","status":"success","total_cost_usd":2.0}"""),
        ]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new StatsRow("code", "anthropic/claude-sonnet-5", 1, 1, 0, 0, 2.0m, 2.0m, QuotaPctPerRun: null), rows[0]);
        Assert.Equal(new StatsRow("code", "minimax-coding-plan/MiniMax-M3", 3, 1, 1, 1, 2.0m, 2.0m, 3m), rows[1]);
    }

    [Fact]
    public void A_clean_gate_decision_counts_as_success_even_though_the_worker_already_succeeded()
    {
        List<StatsRow> rows = Commands.StatsRows([
            R(/*lang=json,strict*/ """{"profile":"code","model":"claude-sonnet-5","status":"success","total_cost_usd":1.0,"gate":{"decision":"clean"}}"""),
        ]);
        StatsRow row = Assert.Single(rows);
        Assert.Equal(1, row.Success);
    }

    [Fact]
    public void A_continue_gate_decision_is_not_a_success_even_when_the_worker_status_is_success()
    {
        List<StatsRow> rows = Commands.StatsRows([
            R(/*lang=json,strict*/ """{"profile":"code","model":"claude-sonnet-5","status":"success","total_cost_usd":1.0,"gate":{"decision":"continue"}}"""),
        ]);
        StatsRow row = Assert.Single(rows);
        Assert.Equal(0, row.Success);
    }

    [Fact]
    public void A_null_gate_with_success_status_is_a_success()
    {
        List<StatsRow> rows = Commands.StatsRows([
            R(/*lang=json,strict*/ """{"profile":"code","model":"claude-sonnet-5","status":"success","total_cost_usd":1.0,"gate":null}"""),
        ]);
        StatsRow row = Assert.Single(rows);
        Assert.Equal(1, row.Success);
    }
}
