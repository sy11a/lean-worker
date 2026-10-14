using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The "quota" subcommand served from a fresh cached reading (--max-age), so no adapter calls its API. The
/// provider with the quota adapter lives in the user prices file, the only one that may set an adapter;
/// a provider without a key fails before any request.
/// </summary>
[Collection("launcher-process-state")]
public sealed class QuotaCommandTests : IDisposable
{
    private const string Plan = "test-plan";

    private readonly CommandSandbox _box = new();

    public void Dispose() => _box.Dispose();

    /// <summary>
    /// Writes the user prices file: test-plan with a zai adapter and a 5h limit of 90%; with
    /// onlyTestPlan, every shipped provider's quota block is cleared so test-plan is the only quota provider.
    /// </summary>
    private void UserPrices(bool withTestPlan = true, bool onlyTestPlan = false)
    {
        JsonObject providers = [];
        if (onlyTestPlan)
        {
            foreach (string shipped in PriceBook.Load(_box.RunsRoot).QuotaProviders())
            {
                providers[shipped] = new JsonObject { ["quota"] = null };
            }
        }

        if (withTestPlan)
        {
            providers[Plan] = new JsonObject
            {
                ["billing"] = "subscription",
                ["quota"] = new JsonObject { ["adapter"] = "zai", ["maxPercent"] = new JsonObject { ["5h"] = 90 } },
            };
        }

        _ = _box.WriteFile(Path.Combine("config", "lean-worker", "prices.json"), new JsonObject { ["providers"] = providers }.ToJsonString());
    }

    private void CachedReading(decimal fiveHourPercent, string? level = "max")
    {
        QuotaReading q = new(Plan, level,
            [new("5h", fiveHourPercent, ResetsAt: null, Detail: null), new("mcp-monthly", 0, ResetsAt: null, "12 of 4000 calls")],
            DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(_box.QuotaCacheDir, $"quota-{Plan}.json"), q.ToJson().ToJsonString());
    }

    [Fact]
    public void Quota_prints_each_window_its_details_and_the_headroom()
    {
        UserPrices();
        CachedReading(12);

        int code = _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot, "--provider", Plan, "--max-age", "3600"]));

        Assert.Equal(0, code);
        Assert.Equal(
            [
                "test-plan (max): 5h 12%, mcp-monthly 0%",
                "  mcp-monthly: 12 of 4000 calls",
                "  headroom for workers: yes (test-plan 5h 12%, mcp-monthly 0%)",
            ],
            _box.Lines);
    }

    [Fact]
    public void Quota_without_a_level_and_over_the_limit_has_no_headroom()
    {
        UserPrices();
        CachedReading(95, level: null);

        _ = _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot, "--provider", Plan, "--max-age", "3600"]));

        string[] lines = _box.Lines;
        Assert.Equal("test-plan: 5h 95%, mcp-monthly 0%", lines[0]);
        Assert.Equal("  headroom for workers: no (test-plan 5h 95% >= 90%)", lines[2]);
    }

    [Fact]
    public void Quota_json_prints_an_array_of_readings()
    {
        UserPrices();
        CachedReading(12);

        _ = _box.Run(() => Commands.Quota(["--json", "--runs-root", _box.RunsRoot, "--provider", Plan, "--max-age", "3600"]));

        JsonObject reading = Assert.Single(JsonNode.Parse(_box.Output)!.AsArray())!.AsObject();
        Assert.Equal(Plan, (string?)reading["provider"]);
        Assert.Equal("max", (string?)reading["level"]);
        Assert.Equal(2, reading["windows"]!.AsArray().Count);
    }

    [Fact]
    public void Quota_without_a_provider_reads_every_provider_with_a_quota_adapter()
    {
        UserPrices(onlyTestPlan: true);
        CachedReading(12);

        _ = _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot, "--max-age", "3600"]));

        Assert.Equal("test-plan (max): 5h 12%, mcp-monthly 0%", _box.Lines[0]);
        Assert.Equal(3, _box.Lines.Length);
    }

    [Fact]
    public void Quota_fails_when_no_provider_has_a_quota_adapter()
    {
        UserPrices(withTestPlan: false, onlyTestPlan: true);

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot])));

        Assert.Equal("no provider in prices.json has a quota adapter", ex.Message);
    }

    [Fact]
    public void Quota_passes_a_launcher_error_through_unchanged()
    {
        UserPrices();

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot, "--provider", "no-quota-here"])));

        Assert.Equal("provider 'no-quota-here' has no quota adapter in prices.json", ex.Message);
    }

    [Fact]
    public void Quota_without_max_age_ignores_the_cache_and_needs_a_key()
    {
        UserPrices();
        CachedReading(12);

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot, "--provider", Plan])));

        Assert.StartsWith("no API key for provider 'test-plan'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_box.Output);
    }

    [Fact]
    public void Quota_names_the_provider_when_its_reading_cannot_be_read()
    {
        UserPrices();
        File.WriteAllText(Path.Combine(_box.QuotaCacheDir, $"quota-{Plan}.json"), "[]");

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Quota(["--runs-root", _box.RunsRoot, "--provider", Plan, "--max-age", "3600"])));

        Assert.StartsWith("quota for test-plan: ", ex.Message, StringComparison.Ordinal);
    }
}
