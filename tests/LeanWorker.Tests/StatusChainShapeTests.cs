using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Issue #146, fix round 3, point 1 (review finding 1): the launcher identity of a chain can be copied from the victim's
/// files, so a group of rows is one chain row only when it also has the shape an honest chain has: the rounds are exactly
/// 1..N with the first run (the run whose id is the chain id) as round 1, every round below N ended <c>success</c> or has
/// no end, and no round below N reads live and not ending. A group that breaks any of it is shown as its own rows, all
/// visible, each in its own state. Both forgeries of the review (a copied identity and chain id claiming a round with an
/// error end, and a live round N+1) leave the victim's rounds listed with their own states; the honest chains stay one row.
/// </summary>
public sealed class StatusChainShapeTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static Func<string, string?> Caller() => StatusIndexRig.Session("sess-A");

    private static TimeSpan Min(int minutes) => TimeSpan.FromMinutes(minutes);

    // In its gate (no end), or done a minute after it started with `status`.
    private static void Gate(JsonObject doc, TimeSpan started, string? status)
    {
        doc["phase"] = status is null ? "gate" : "done";
        doc["end"] = status is null ? null : StatusDocs.End(status, StatusDocs.Now - started + Min(1));
    }

    // Round 1, its launcher (100) alive: in its gate, or (with an end) done.
    private StatusIndexRig.Added Opening(string name, TimeSpan started, string? status = null) =>
        _rig.Add(name, _mine, startedAgo: started, tweak: d => Gate(d, started, status));

    // A later round of the first run's chain, its launcher (100) alive: in its gate, or (with an end) done.
    private StatusIndexRig.Added Round(string name, StatusIndexRig.Added first, int round, TimeSpan started, string? status = null) =>
        _rig.Add(name, _mine, startedAgo: started, chain: first.Id, round: round, tweak: d => Gate(d, started, status));

    // A row of the plain listing: it starts with the state and names the run (its name, then a space or the line's end).
    private static bool IsRow(string row, string state, string name) => row.StartsWith(state, StringComparison.Ordinal) && Names(row, name);

    private static bool Names(string row, string name) =>
        row.Contains(" " + name + " ", StringComparison.Ordinal) || row.EndsWith(" " + name, StringComparison.Ordinal);

    // The same two, for a launcher (900) that is gone: it ended `ended` ago with `status`.
    private StatusIndexRig.Added DeadOpening(string name, TimeSpan started, string status = "success", int round = 1) =>
        _rig.Add(name, _mine, endedAgo: started - Min(1), startedAgo: started, status: status, round: round);

    private StatusIndexRig.Added DeadRound(string name, StatusIndexRig.Added first, int round, TimeSpan started, string status = "success") =>
        _rig.Add(name, _mine, endedAgo: started - Min(1), startedAgo: started, status: status, chain: first.Id, round: round);

    // A round of a gone launcher (900) that never wrote an end, in its gate; no worker of it is alive.
    private StatusIndexRig.Added DeadInGate(string name, TimeSpan started, StatusIndexRig.Added? first = null, int round = 1)
    {
        return _rig.Add(name, _mine, startedAgo: started, chain: first?.Id, round: round, tweak: d =>
        {
            d["phase"] = "gate";
            d["launcher"]!["pid"] = 900;
        });
    }

    private async Task<(string Plain, List<JsonObject> Json)> ListAsync(params string[] args)
    {
        (int code, string plain) = await _rig.CallAsync(Caller(), args);
        (int jsonCode, string json) = await _rig.CallAsync(Caller(), [.. args, "--json"]);
        Assert.Equal([0, 0], [code, jsonCode]);
        return (plain, StatusIndexRig.JsonRows(json));
    }

    private static string NameOf(JsonObject row) => (row["run"]?["name"] ?? row["name"])!.GetValue<string>();

    private static JsonObject Row(List<JsonObject> json, string name) => json.Single(r => string.Equals(NameOf(r), name, StringComparison.Ordinal));

    private static string State(List<JsonObject> json, string name) => Row(json, name)["state"]!.GetValue<string>();

    // The plain listing has exactly these rows (state, then run name), the --json listing the same runs, none in a chain.
    private static void AssertRows((string Plain, List<JsonObject> Json) listing, params (string State, string Name)[] expected)
    {
        string[] rows = StatusIndexRig.Rows(listing.Plain);
        Assert.Equal(expected.Length, rows.Length);
        foreach ((string state, string name) in expected)
        {
            Assert.Contains(rows, r => IsRow(r, state, name));
            Assert.Equal(state, State(listing.Json, name));
        }

        Assert.Equal(expected.Length, listing.Json.Count);
        Assert.All(listing.Json, r => Assert.Null(r["chain"]));
    }

    // The victim's rounds: rounds below the last are done (success), the last is in its gate; names victim, victim-gate2.
    // Standing alone, a round that wrote its end while its launcher still runs reads `ending` (a row's own state: only a
    // round with a later round in the same chain is final), and the live gate round reads `gate`.
    private (StatusIndexRig.Added First, (string State, string Name)[] Expected) AliveVictim(int rounds)
    {
        if (rounds is 1)
        {
            return (Opening("victim", Min(30)), [("gate", "victim")]);
        }

        StatusIndexRig.Added first = Opening("victim", Min(40), "success");
        _ = Round("victim-gate2", first, 2, Min(30));
        return (first, [("ending", "victim"), ("gate", "victim-gate2")]);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 3)]
    public async Task A_forged_member_with_the_victims_launcher_identity_and_chain_id_claiming_a_round_with_an_error_end_does_not_replace_the_victims_live_roundAsync(int victimRounds, int forgedRound)
    {
        (StatusIndexRig.Added first, (string State, string Name)[] victim) = AliveVictim(victimRounds);
        _ = Round("forger", first, forgedRound, Min(20), "error");

        (string plain, List<JsonObject> json) = await ListAsync();

        // The victim's live gate round is the row shown for it, with its own state; the forger stands alone.
        AssertRows((plain, json), [.. victim, ("ending", "forger")]);
        Assert.Equal("alive", Row(json, victim[^1].Name)["liveness"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_forged_live_round_after_the_last_one_with_the_victims_identity_does_not_replace_the_victims_live_round_while_the_launcher_runsAsync(int victimRounds)
    {
        (StatusIndexRig.Added first, (string State, string Name)[] victim) = AliveVictim(victimRounds);
        _ = Round("forger", first, victimRounds + 1, Min(10));

        (string plain, List<JsonObject> json) = await ListAsync();

        AssertRows((plain, json), [.. victim, ("gate", "forger")]);
        Assert.Equal("alive", Row(json, victim[^1].Name)["liveness"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_forged_live_round_after_the_last_one_with_the_victims_identity_is_one_chain_row_once_the_victims_launcher_has_exitedAsync(int victimRounds)
    {
        // Launcher 900 is gone, and so is the worker: the victim's last round and the forgery both never wrote an end.
        // Such a group cannot be told from an honest chain whose launcher died mid-chain (accepted residual, equal to
        // overwriting a dead run's files directly), so it is one chain row and no member is hidden.
        StatusIndexRig.Added first = victimRounds is 1 ? DeadInGate("victim", Min(30)) : DeadOpening("victim", Min(40));
        string[] all = victimRounds is 2 ? ["victim", "victim-gate2", "forger"] : ["victim", "forger"];
        if (victimRounds is 2)
        {
            _ = DeadInGate("victim-gate2", Min(30), first, 2);
        }

        _ = DeadInGate("forger", Min(10), first, victimRounds + 1);

        (string plain, List<JsonObject> json) = await ListAsync();

        _ = Assert.Single(StatusIndexRig.Rows(plain));
        Assert.Equal([.. all.Order(StringComparer.Ordinal)], [.. json.Select(r => NameOf(r)).Order(StringComparer.Ordinal)]);
        Assert.All(json, r => Assert.Equal(first.Id, r["chain"]!["id"]!.GetValue<string>()));
        Assert.All(json, r => Assert.Equal(all.Length, r["chain"]!["rounds"]!.GetValue<int>()));
        Assert.All(all, n => Assert.Equal(n is "forger", Row(json, n)["chain"]!["latest"]!.GetValue<bool>()));
        if (victimRounds is 2)
        {
            Assert.Equal("success", State(json, "victim"));
        }

        // The runs that never wrote an end do not read as a success.
        Assert.All(all.Where(n => n is not "victim" || victimRounds is 1), n => Assert.NotEqual("success", State(json, n), StringComparer.Ordinal));
    }

    // The honest chain of the case, ended and its launcher (900) gone, then one way of breaking its shape.
    private string[] Broken(string how)
    {
        switch (how)
        {
            case "gap":
                {
                    StatusIndexRig.Added first = DeadOpening("chain", Min(60));
                    _ = DeadRound("chain-gate3", first, 3, Min(50));
                    return ["chain", "chain-gate3"];
                }

            case "gap-after-two":
                {
                    StatusIndexRig.Added first = DeadOpening("chain", Min(60));
                    _ = DeadRound("chain-gate2", first, 2, Min(50));
                    _ = DeadRound("chain-gate4", first, 4, Min(40));
                    return ["chain", "chain-gate2", "chain-gate4"];
                }

            case "round-0":
                {
                    StatusIndexRig.Added first = DeadOpening("chain", Min(60));
                    _ = DeadRound("chain-zero", first, 0, Min(70));
                    return ["chain", "chain-zero"];
                }

            case "first-not-round-1":
                {
                    StatusIndexRig.Added first = DeadOpening("chain", Min(60), round: 2);
                    _ = DeadRound("chain-gate3", first, 3, Min(50));
                    return ["chain", "chain-gate3"];
                }

            case "two-live":
                {
                    StatusIndexRig.Added first = Opening("chain", Min(60));
                    _ = Round("chain-gate2", first, 2, Min(50));
                    return ["chain", "chain-gate2"];
                }

            default:
                {
                    // An earlier round ended with a status other than success: `how` is that status.
                    StatusIndexRig.Added first = DeadOpening("chain", Min(60), status: how);
                    _ = DeadRound("chain-gate2", first, 2, Min(50));
                    return ["chain", "chain-gate2"];
                }
        }
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("gap-after-two")]
    [InlineData("round-0")]
    [InlineData("first-not-round-1")]
    [InlineData("two-live")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("wrapped-up")]
    [InlineData("launcher-signalled")]
    public async Task Each_way_of_breaking_the_shape_alone_splits_the_group_into_rows_all_visibleAsync(string how)
    {
        string[] names = Broken(how);

        (string plain, List<JsonObject> json) = await ListAsync();

        string[] rows = StatusIndexRig.Rows(plain);
        Assert.Equal(names.Length, rows.Length);
        Assert.All(names, n => Assert.Contains(rows, r => Names(r, n)));
        Assert.Equal([.. names.Order(StringComparer.Ordinal)], [.. json.Select(r => NameOf(r)).Order(StringComparer.Ordinal)]);
        Assert.All(json, r => Assert.Null(r["chain"]));
    }

    [Fact]
    public async Task Splitting_a_group_keeps_each_rows_own_stateAsync()
    {
        _ = Broken("error");

        (_, List<JsonObject> json) = await ListAsync();

        Assert.Equal("error", State(json, "chain"));
        Assert.Equal("success", State(json, "chain-gate2"));
    }

    // One chain: a plain row and every round in --json with the chain's id, count and latest flag.
    private static void AssertOneChain((string Plain, List<JsonObject> Json) listing, StatusIndexRig.Added first, string stateOfRow, string nameOfRow, params string[] states)
    {
        string[] rows = StatusIndexRig.Rows(listing.Plain);
        Assert.True(IsRow(Assert.Single(rows), stateOfRow, nameOfRow), "the row is " + rows[0]);
        string[] actual = [.. listing.Json.Select(r => r["state"]!.GetValue<string>())];
        Assert.Equal<IEnumerable<string>>(states, actual);
        Assert.All(listing.Json, r => Assert.Equal(first.Id, r["chain"]!["id"]!.GetValue<string>()));
        Assert.All(listing.Json, r => Assert.Equal(states.Length, r["chain"]!["rounds"]!.GetValue<int>()));
        Assert.Equal([.. states.Select((_, i) => i == states.Length - 1)], [.. listing.Json.Select(r => r["chain"]!["latest"]!.GetValue<bool>())]);
    }

    [Fact]
    public async Task A_finished_chain_whose_rounds_all_succeeded_is_one_rowAsync()
    {
        StatusIndexRig.Added first = DeadOpening("chain", Min(60));
        _ = DeadRound("chain-gate2", first, 2, Min(50));
        _ = DeadRound("chain-gate3", first, 3, Min(40));

        AssertOneChain(await ListAsync(), first, "success", "chain-gate3", "success", "success", "success");
    }

    [Fact]
    public async Task A_chain_running_in_round_k_with_earlier_rounds_that_succeeded_is_one_row_in_the_live_roundAsync()
    {
        StatusIndexRig.Added first = Opening("chain", Min(60), "success");
        _ = Round("chain-gate2", first, 2, Min(50), "success");
        _ = Round("chain-gate3", first, 3, Min(40));

        AssertOneChain(await ListAsync(), first, "gate", "chain-gate3", "success", "success", "gate");
    }

    [Theory]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("wrapped-up")]
    public async Task A_chain_whose_last_round_failed_is_one_row_in_the_failed_roundAsync(string status)
    {
        StatusIndexRig.Added first = DeadOpening("chain", Min(60));
        _ = DeadRound("chain-gate2", first, 2, Min(50), status);

        AssertOneChain(await ListAsync(), first, status, "chain-gate2", "success", status);
    }

    [Fact]
    public async Task A_chain_whose_launcher_died_mid_chain_leaving_the_last_round_without_an_end_is_one_rowAsync()
    {
        StatusIndexRig.Added first = DeadOpening("chain", Min(60));
        _ = DeadInGate("chain-gate2", Min(50), first, 2);

        (string plain, List<JsonObject> json) = await ListAsync();

        _ = Assert.Single(StatusIndexRig.Rows(plain));
        Assert.Equal(2, json.Count);
        Assert.All(json, r => Assert.Equal(first.Id, r["chain"]!["id"]!.GetValue<string>()));
        Assert.Equal("success", State(json, "chain"));
        Assert.NotEqual("success", State(json, "chain-gate2"), StringComparer.Ordinal);
        Assert.True(Row(json, "chain-gate2")["chain"]!["latest"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_chain_whose_launcher_died_with_the_last_round_ended_by_the_signal_is_one_rowAsync()
    {
        StatusIndexRig.Added first = DeadOpening("chain", Min(60));
        _ = DeadRound("chain-gate2", first, 2, Min(50), "launcher-signalled");

        // A signalled end reads `failed` (as a launch failure does).
        AssertOneChain(await ListAsync(), first, "failed", "chain-gate2", "success", "failed");
    }

    [Fact]
    public async Task A_single_run_is_one_row_without_a_chainAsync()
    {
        _ = DeadOpening("chain", Min(60));

        (string plain, List<JsonObject> json) = await ListAsync();

        _ = Assert.Single(StatusIndexRig.Rows(plain));
        Assert.Null(Assert.Single(json)["chain"]);
    }
}
