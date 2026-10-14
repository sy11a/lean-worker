using System.Globalization;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The "prices" subcommand: the merged sources in order and each entry's date, flagging stale ones.
/// </summary>
[Collection("launcher-process-state")]
public sealed class PricesCommandTests : IDisposable
{
    private readonly CommandSandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Prices_lists_the_sources_in_merge_order_and_flags_entries_older_than_90_days()
    {
        string today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string user = _box.WriteFile(Path.Combine("config", "lean-worker", "prices.json"), /*lang=json,strict*/ """{"models":{}}""");
        string project = Path.Combine(_box.RunsRoot, "prices.json");
        File.WriteAllText(project, $$$"""
            {"asOf":null,"unknownModel":"error","models":{
              "testprov/stale":{"input":1,"asOf":"2020-01-01"},
              "testprov/fresh":{"input":1,"asOf":"{{{today}}}"},
              "testprov/undated":{"input":1},
              "testprov/odd":{"input":1,"asOf":"not a date"}
            }}
            """);

        int code = _box.Run(() => Commands.Prices(["--runs-root", _box.RunsRoot]));

        Assert.Equal(0, code);
        string[] lines = _box.Lines;
        Assert.Equal("sources (later ones override earlier):", lines[0]);
        Assert.Equal($"  {Path.Combine(AppContext.BaseDirectory, "prices.json")}", lines[1]);
        Assert.Equal($"  {user}", lines[2]);
        Assert.Equal($"  {project}", lines[3]);
        Assert.Equal("unknown models: error", lines[4]);
        Assert.Contains($"  {"testprov/stale",-36} as of 2020-01-01  <- older than 90 days, check it", lines, StringComparer.Ordinal);
        Assert.Contains($"  {"testprov/fresh",-36} as of {today}", lines, StringComparer.Ordinal);
        Assert.Contains($"  {"testprov/undated",-36} as of ?", lines, StringComparer.Ordinal);
        Assert.Contains($"  {"testprov/odd",-36} as of not a date", lines, StringComparer.Ordinal);
    }

    [Fact]
    public void Prices_reads_an_explicit_prices_file_instead_of_the_projects()
    {
        string explicitFile = _box.WriteFile("other.json", /*lang=json,strict*/ """{"unknownModel":"dearest","models":{"testprov/x":{"input":1,"asOf":"2020-01-01"}}}""");

        _ = _box.Run(() => Commands.Prices(["--runs-root", _box.RunsRoot, "--prices", explicitFile]));

        string[] lines = _box.Lines;
        Assert.Equal($"  {explicitFile}", lines[2]);
        Assert.Equal("unknown models: dearest", lines[3]);
        Assert.Contains($"  {"testprov/x",-36} as of 2020-01-01  <- older than 90 days, check it", lines, StringComparer.Ordinal);
    }
}
