using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// --started-after on the one-shot listing (docs/research/live-status/README.md, section 4.2: "--wait (and the listing) take
/// only runs started at or after t"): the runs started earlier are left out of the plain listing and of --json, a run
/// that ended still counts, a gate chain counts by its first round, and a run whose start cannot be told at all is kept.
/// </summary>
public sealed class StatusStartedAfterTests : IDisposable
{
    private static readonly string[] _mine = ["k:1"];

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static string After(TimeSpan ago) => StatusDocs.Stamp(StatusDocs.Now - ago);

    private async Task<string[]> NamesAsync(params string[] args)
    {
        (int code, string json) = await _rig.CallAsync(StatusIndexRig.NoCaller, [.. args, "--json"]);
        Assert.Equal(0, code);
        return [.. StatusIndexRig.JsonNames(json).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Runs_started_before_the_time_are_left_out_of_the_listing_and_a_run_started_exactly_at_it_is_keptAsync()
    {
        _ = _rig.Add("started-3h-ago", _mine, startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("started-2h-ago", _mine, startedAgo: TimeSpan.FromHours(2));
        _ = _rig.Add("started-90m-ago", _mine, startedAgo: TimeSpan.FromMinutes(90));
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));

        string[] names = await NamesAsync("--owner", "k:1", "--started-after", After(TimeSpan.FromHours(2)));

        Assert.Equal(["started-10m-ago", "started-2h-ago", "started-90m-ago"], names);
    }

    [Fact]
    public async Task The_plain_listing_and_its_header_follow_the_filter_tooAsync()
    {
        _ = _rig.Add("started-3h-ago", _mine, startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));

        (int code, string plain) = await _rig.CallAsync(StatusIndexRig.NoCaller, "--owner", "k:1", "--started-after", After(TimeSpan.FromHours(1)));

        Assert.Equal(0, code);
        string[] rows = StatusIndexRig.Rows(plain);
        _ = Assert.Single(rows);
        Assert.Contains("started-10m-ago", rows[0], StringComparison.Ordinal);
        Assert.Contains("1 live", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("+02:00")]
    [InlineData("-05:00")]
    public async Task The_time_may_carry_an_offset_and_means_the_same_instantAsync(string zone)
    {
        _ = _rig.Add("started-3h-ago", _mine, startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));
        DateTimeOffset instant = StatusDocs.Now - TimeSpan.FromHours(1);
        TimeSpan offset = zone is "Z" ? TimeSpan.Zero : TimeSpan.Parse(zone.TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture);
        string written = instant.ToOffset(offset).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + zone;

        string[] names = await NamesAsync("--owner", "k:1", "--started-after", written);

        Assert.Equal(["started-10m-ago"], names);
    }

    [Fact]
    public async Task A_run_that_has_ended_still_counts_when_it_started_after_the_timeAsync()
    {
        _ = _rig.Add("ended-after", _mine, endedAgo: TimeSpan.FromMinutes(5), startedAgo: TimeSpan.FromMinutes(30), status: "error");
        _ = _rig.Add("ended-before", _mine, endedAgo: TimeSpan.FromMinutes(5), startedAgo: TimeSpan.FromHours(5));

        string[] names = await NamesAsync("--owner", "k:1", "--started-after", After(TimeSpan.FromHours(1)));

        Assert.Equal(["ended-after"], names);
    }

    [Fact]
    public async Task A_run_whose_start_time_cannot_be_told_at_all_is_keptAsync()
    {
        // No start in its status file, no creation time in its pointer, no stamp in its run id: nothing to judge it by.
        StatusIndexRig.Added none = _rig.Add("no-start-time", _mine, id: "no-stamp-in-this-id", tweak: d => ((JsonObject)d["spec"]!).Remove("started_at"));
        _ = none.PointerDoc.Remove("created_at");
        _ = _rig.WritePointer(none.Pointer, none.PointerDoc.ToJsonString());
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));
        _ = _rig.Add("started-3h-ago", _mine, startedAgo: TimeSpan.FromHours(3));

        string[] names = await NamesAsync("--owner", "k:1", "--started-after", After(TimeSpan.FromHours(1)));

        Assert.Equal(["no-start-time", "started-10m-ago"], names);
    }

    [Fact]
    public async Task A_gate_chain_counts_by_its_first_roundAsync()
    {
        const string first = "20261122-090000-chain-w1";
        const string second = "20261122-114000-chain-w1";
        _ = _rig.Add("chain-w1", _mine, endedAgo: TimeSpan.FromMinutes(25), startedAgo: TimeSpan.FromHours(3), chain: first, id: first);
        _ = _rig.Add("chain-w1", _mine, endedAgo: TimeSpan.FromMinutes(5), startedAgo: TimeSpan.FromMinutes(20), chain: first, round: 2, id: second);

        // --json lists every round of a chain as an entry of its own.
        Assert.Equal(["chain-w1", "chain-w1"], await NamesAsync("--owner", "k:1", "--started-after", After(TimeSpan.FromHours(4))));
        Assert.Empty(await NamesAsync("--owner", "k:1", "--started-after", After(TimeSpan.FromHours(1))));
    }

    [Fact]
    public async Task The_filter_applies_to_the_scan_of_here_and_to_allAsync()
    {
        _ = _rig.Add("started-3h-ago", _mine, startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));
        string after = After(TimeSpan.FromHours(1));

        Assert.Equal(["started-10m-ago"], await NamesAsync("--here", "--runs-root", _rig.Root, "--started-after", after));
        Assert.Equal(["started-10m-ago"], await NamesAsync("--all", "--started-after", after));
    }

    [Fact]
    public async Task Without_the_option_every_run_is_listed_as_beforeAsync()
    {
        _ = _rig.Add("started-3h-ago", _mine, startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));

        string[] names = await NamesAsync("--owner", "k:1");

        Assert.Equal(["started-10m-ago", "started-3h-ago"], names);
    }

    [Fact]
    public async Task A_filter_that_leaves_nothing_is_an_empty_listing_with_exit_0_not_an_errorAsync()
    {
        _ = _rig.Add("started-10m-ago", _mine, startedAgo: TimeSpan.FromMinutes(10));

        (int code, string plain) = await _rig.CallAsync(StatusIndexRig.NoCaller, "--owner", "k:1", "--started-after", After(-TimeSpan.FromHours(1)));

        Assert.Equal(0, code);
        Assert.Empty(StatusIndexRig.Rows(plain));
    }
}
