using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The "stats" subcommand over a fixture runs.jsonl: the table, the --json document and the --since filter.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatsCommandTests : IDisposable
{
    private const string Runs = """
        {"timestamp":"2026-10-01T10:00:00+00:00","profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"success","total_cost_usd":1.0,"quota_used_pct":{"5h":4,"weekly":1}}
        {"timestamp":"2026-10-02T10:00:00+00:00","profile":"code","provider":"minimax-coding-plan","model":"MiniMax-M3","status":"wrapped-up","total_cost_usd":0.5,"escalate_to":"claude-sonnet-5"}
        {"timestamp":"2026-10-03T10:00:00+00:00","profile":"review","model":"claude-opus-5-5","status":"error","total_cost_usd":2.0}
        {"timestamp":"2025-01-01T00:00:00+00:00","profile":"old","model":"m","status":"success","total_cost_usd":9}
        not json
        {"profile":"no-timestamp","model":"m","status":"success"}
        """;

    private const string Footer = "cost = list price (list-price equivalent for subscriptions); esc = runs that offered the next model; quota%/run = largest window increase per run.";

    private readonly CommandSandbox _box = new();

    public StatsCommandTests() => File.WriteAllText(Path.Combine(_box.RunsRoot, "runs.jsonl"), Runs);

    public void Dispose() => _box.Dispose();

    private static string Row(string profile, string model, int runs, int ok, int wrap, int esc, string cost, string perSuccess, string quota) =>
        string.Create(CultureInfo.InvariantCulture, $"{profile,-16} {model,-34} {runs,4} {ok,4} {wrap,4} {esc,4} {cost,9} {perSuccess,9} {quota,10}");

    [Fact]
    public void Stats_without_a_runs_file_fails()
    {
        string empty = Path.Combine(_box.Root, "empty");

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Stats(["--runs-root", empty])));

        Assert.Equal($"no runs recorded yet ({Path.Combine(empty, "runs.jsonl")})", ex.Message);
    }

    [Fact]
    public void Stats_prints_a_table_of_the_runs_since_a_date()
    {
        int code = _box.Run(() => Commands.Stats(["--runs-root", _box.RunsRoot, "--since", "2026-01-01"]));

        Assert.Equal(0, code);
        Assert.Equal(
            [
                $"{"profile",-16} {"model",-34} {"runs",4} {"ok",4} {"wrap",4} {"esc",4} {"cost",9} {"$/success",9} {"quota%/run",10}",
                Row("code", "minimax-coding-plan/MiniMax-M3", 2, 1, 1, 1, "$1.500", "$1.500", "4.0"),
                Row("review", "anthropic/claude-opus-5-5", 1, 0, 0, 0, "$2.000", "-", "-"),
                Footer,
            ],
            _box.Lines);
    }

    [Fact]
    public void Stats_without_since_counts_every_run_with_a_timestamp()
    {
        _ = _box.Run(() => Commands.Stats(["--runs-root", _box.RunsRoot]));

        string[] lines = _box.Lines;
        Assert.Equal(5, lines.Length);
        Assert.Equal(Row("old", "anthropic/m", 1, 1, 0, 0, "$9.000", "$9.000", "-"), lines[2]);
    }

    [Fact]
    public void Stats_json_has_the_schema_version_the_runs_file_and_one_group_per_profile_and_model()
    {
        _ = _box.Run(() => Commands.Stats(["--runs-root", _box.RunsRoot, "--since", "2026-01-01", "--json"]));

        JsonObject doc = JsonNode.Parse(_box.Output)!.AsObject();
        Assert.Equal(Launcher.RunSchemaVersion, (int)doc["schema_version"]!);
        Assert.Equal(Path.GetFullPath(Path.Combine(_box.RunsRoot, "runs.jsonl")), (string?)doc["runs_file"]);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01", CultureInfo.InvariantCulture), DateTimeOffset.Parse((string)doc["since"]!, CultureInfo.InvariantCulture));
        Assert.Equal("list price; list-price equivalent for subscriptions", (string?)doc["cost_basis"]);
        JsonArray groups = doc["groups"]!.AsArray();
        Assert.Equal(2, groups.Count);
        JsonObject code = groups[0]!.AsObject();
        Assert.Equal("code", (string?)code["profile"]);
        Assert.Equal("minimax-coding-plan/MiniMax-M3", (string?)code["model"]);
        Assert.Equal(2, (int)code["runs"]!);
        Assert.Equal(1, (int)code["success"]!);
        Assert.Equal(1, (int)code["wrapped_up"]!);
        Assert.Equal(1, (int)code["escalations"]!);
        Assert.Equal(1.5m, (decimal)code["cost_usd"]!);
        Assert.Equal(1.5m, (decimal)code["cost_per_success_usd"]!);
        Assert.Equal(4m, (decimal)code["quota_pct_per_run"]!);
        JsonObject review = groups[1]!.AsObject();
        Assert.Null(review["cost_per_success_usd"]);
        Assert.Null(review["quota_pct_per_run"]);
    }

    [Fact]
    public void Stats_json_without_since_has_a_null_since()
    {
        _ = _box.Run(() => Commands.Stats(["--json", "--runs-root", _box.RunsRoot]));

        JsonObject doc = JsonNode.Parse(_box.Output)!.AsObject();
        Assert.True(doc.ContainsKey("since"));
        Assert.Null(doc["since"]);
        Assert.Equal(3, doc["groups"]!.AsArray().Count);
    }
}
