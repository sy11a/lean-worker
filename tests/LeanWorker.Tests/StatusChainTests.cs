using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A gate chain is one row (issue #142, docs/research/live-status/README.md, section 4.2): the latest round, shown as
/// gate r&lt;k&gt;/&lt;max&gt; while its gate runs and in its final state after; an earlier round with an end is final
/// (not ending) and not counted as live; --json keeps every round with a chain field; a chain id that a run only
/// claims cannot hide another run's row (a chain is rows of one chain id that include its first run and have
/// distinct round numbers).
/// </summary>
public sealed class StatusChainTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static Func<string, string?> Caller() => StatusIndexRig.Session("sess-A");

    // Round 1 of a chain: ended (its status file says done and has an end), launcher 100 still alive because it runs
    // the later rounds.
    private StatusIndexRig.Added FirstRound(string name = "chain", int minutesAgo = 30)
    {
        return _rig.Add(name, _mine, startedAgo: TimeSpan.FromMinutes(minutesAgo), tweak: d =>
        {
            d["phase"] = "done";
            d["end"] = StatusDocs.End("success", StatusDocs.Now - TimeSpan.FromMinutes(minutesAgo - 5));
        });
    }

    private StatusIndexRig.Added GateRound(StatusIndexRig.Added first, int round, int? max, int minutesAgo)
    {
        string name = first.Status["run"]!["name"]!.GetValue<string>() + "-gate" + round.ToString(CultureInfo.InvariantCulture);
        return _rig.Add(name, _mine, startedAgo: TimeSpan.FromMinutes(minutesAgo), chain: first.Id, round: round, tweak: d =>
        {
            d["phase"] = "gate";
            d["live"]!["gate_round"] = round;
            d["live"]!["gate_max_rounds"] = max;
        });
    }

    [Fact]
    public async Task A_chain_in_its_gate_is_one_plain_row_that_says_the_round_and_the_maximumAsync()
    {
        StatusIndexRig.Added first = FirstRound();
        _ = GateRound(first, 2, max: 3, minutesAgo: 20);

        (int code, string plain) = await _rig.CallAsync(Caller());

        Assert.Equal(0, code);
        string[] rows = StatusIndexRig.Rows(plain);
        string row = Assert.Single(rows);
        Assert.Matches(@"^gate r2/3\s+chain-gate2\s", row);
        Assert.Equal("lean-worker status: owner cc:sess-A, 1 live, 0 ended, 0 need attention", StatusIndexRig.Lines(plain)[0]);
        _ = TerminalAssert.AssertPlain(plain, 1);
    }

    [Fact]
    public async Task Without_a_recorded_maximum_the_state_is_gate_r_and_the_roundAsync()
    {
        _ = GateRound(FirstRound(), 2, max: null, minutesAgo: 20);

        (_, string plain) = await _rig.CallAsync(Caller());

        Assert.Matches(@"^gate r2\s+chain-gate2\s", Assert.Single(StatusIndexRig.Rows(plain)));
    }

    [Fact]
    public async Task Json_keeps_every_round_with_its_derived_state_and_a_chain_field_and_the_earlier_round_is_final_not_endingAsync()
    {
        StatusIndexRig.Added first = FirstRound();
        StatusIndexRig.Added second = GateRound(first, 2, max: 3, minutesAgo: 20);

        (_, string json) = await _rig.CallAsync(Caller(), "--json");

        List<JsonObject> rows = StatusIndexRig.JsonRows(json);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["success", "gate"], [.. rows.Select(r => r["state"]!.GetValue<string>())]);
        Assert.Equal(["gone", "alive"], [.. rows.Select(r => r["liveness"]!.GetValue<string>())]);
        Assert.Equal([1, 2], rows.Select(r => r["chain"]!["round"]!.GetValue<int>()));
        Assert.All(rows, r => Assert.Equal(first.Id, r["chain"]!["id"]!.GetValue<string>()));
        Assert.All(rows, r => Assert.Equal(2, r["chain"]!["rounds"]!.GetValue<int>()));
        Assert.Equal([false, true], rows.Select(r => r["chain"]!["latest"]!.GetValue<bool>()));
        Assert.Equal(first.Id, rows[0]["run"]!["id"]!.GetValue<string>());
        Assert.Equal(second.Id, rows[1]["run"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_outside_a_chain_has_no_chain_fieldAsync()
    {
        _ = _rig.Add("plain-run", _mine);

        (_, string json) = await _rig.CallAsync(Caller(), "--json");

        Assert.Null(Assert.Single(StatusIndexRig.JsonRows(json))["chain"]);
    }

    [Fact]
    public async Task A_finished_chain_is_one_row_in_the_final_state_of_its_latest_round_and_counts_as_one_endedAsync()
    {
        // One launcher (900, ended) ran all three rounds, as a real chain's launcher does.
        StatusIndexRig.Added first = _rig.Add("chain", _mine, endedAgo: TimeSpan.FromMinutes(25), startedAgo: TimeSpan.FromMinutes(30));
        _ = _rig.Add("chain-gate2", _mine, endedAgo: TimeSpan.FromMinutes(2), startedAgo: TimeSpan.FromMinutes(20), status: "wrapped-up", chain: first.Id, round: 2);
        _ = _rig.Add("chain-gate3", _mine, endedAgo: TimeSpan.FromMinutes(1), startedAgo: TimeSpan.FromMinutes(10), status: "error", chain: first.Id, round: 3);

        (_, string plain) = await _rig.CallAsync(Caller());
        (_, string json) = await _rig.CallAsync(Caller(), "--json");

        Assert.Matches(@"^error\s+chain-gate3\s", Assert.Single(StatusIndexRig.Rows(plain)));
        Assert.Equal("lean-worker status: owner cc:sess-A, 0 live, 1 ended, 1 needs attention", StatusIndexRig.Lines(plain)[0]);
        List<JsonObject> rows = StatusIndexRig.JsonRows(json);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["success", "wrapped-up", "error"], [.. rows.Select(r => r["state"]!.GetValue<string>())]);
        Assert.All(rows, r => Assert.Equal(3, r["chain"]!["rounds"]!.GetValue<int>()));
    }

    [Fact]
    public async Task A_chain_is_one_row_in_the_all_and_here_scopes_tooAsync()
    {
        _ = GateRound(FirstRound(), 2, max: 3, minutesAgo: 20);

        (_, string all) = await _rig.AllAsync();
        (_, string here) = await _rig.CallAsync("--here", "--runs-root", _rig.Root);

        Assert.Matches(@"^gate r2/3\s+chain-gate2\s", Assert.Single(StatusIndexRig.Rows(all)));
        Assert.Matches(@"^gate r2/3\s+chain-gate2\s", Assert.Single(StatusIndexRig.Rows(here)));
    }

    [Fact]
    public async Task Runs_that_claim_a_chain_whose_first_run_is_not_listed_each_keep_their_own_rowAsync()
    {
        _ = _rig.Add("claims-one", _mine, chain: "20250101-000000-no-such-first-run", round: 1);
        _ = _rig.Add("claims-two", _mine, chain: "20250101-000000-no-such-first-run", round: 2);

        (_, string plain) = await _rig.CallAsync(Caller());

        Assert.Equal(2, StatusIndexRig.Rows(plain).Length);
    }

    [Fact]
    public async Task Runs_that_claim_a_chain_with_the_same_round_number_each_keep_their_own_rowAsync()
    {
        StatusIndexRig.Added real = _rig.Add("real-run", _mine, startedAgo: TimeSpan.FromMinutes(20));
        _ = _rig.Add("forger-same-round", _mine, startedAgo: TimeSpan.FromMinutes(10), chain: real.Id, round: 1);

        (_, string plain) = await _rig.CallAsync(Caller());
        (_, string json) = await _rig.CallAsync(Caller(), "--json");

        Assert.Equal(2, StatusIndexRig.Rows(plain).Length);
        Assert.All(StatusIndexRig.JsonRows(json), r => Assert.Null(r["chain"]));
    }

    [Fact]
    public async Task A_forged_chain_id_with_a_new_round_number_cannot_hide_another_runs_rowAsync()
    {
        // The rule: a forged chain_id on an unrelated run cannot hide another run's row. Here the forger names the
        // victim's id as its chain and picks a round number the victim does not use.
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        StatusIndexRig.Added victim = _rig.Add("victim-orphaned", _mine, startedAgo: TimeSpan.FromMinutes(40), tweak: d => d["launcher"]!["pid"] = 900);
        _ = _rig.Add("forger", _mine, startedAgo: TimeSpan.FromMinutes(10), chain: victim.Id, round: 2);

        (_, string plain) = await _rig.CallAsync(Caller());

        string[] rows = StatusIndexRig.Rows(plain);
        Assert.Contains(rows, r => r.Contains("victim-orphaned", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Contains("forger", StringComparison.Ordinal));
    }
}
