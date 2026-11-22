using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Fix round 1 of issue #146, point 3 (docs/research/live-status/README.md, section 4.2): the rounds of a gate chain are
/// run by one launcher process, so a row joins a chain only when its launcher identity (pid, start ticks, boot id) in its
/// status file, and in its pointer for a pointer row, equals the first run's, with distinct round numbers. A row that
/// does not prove it stays its own row, and no row is hidden behind another. A chain's row is its worst round (attention
/// over live over ended, the latest on a tie); --since hides a chain only when it would hide every round.
/// </summary>
public sealed class StatusChainProofTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];

    private static readonly Dictionary<string, Action<JsonObject>> _changes = new(StringComparer.Ordinal)
    {
        ["pid"] = l => l["pid"] = 101,
        ["ticks"] = l => l["start_ticks"] = 5001,
        ["boot"] = l => l["boot_id"] = "boot-B",
        ["no-ticks"] = l => l["start_ticks"] = null,
        ["no-boot"] = l => l["boot_id"] = null,
    };

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static Func<string, string?> Caller() => StatusIndexRig.Session("sess-A");

    private static TimeSpan Min(int minutes) => TimeSpan.FromMinutes(minutes);

    private static TimeSpan Days(int days) => TimeSpan.FromDays(days);

    // Round 1, whose launcher (100) is alive: it wrote its end `ended` ago (the later rounds run in the same launcher).
    private StatusIndexRig.Added Opening(string name, TimeSpan started, TimeSpan ended, string status = "success", Action<JsonObject>? more = null)
    {
        return _rig.Add(name, _mine, startedAgo: started, tweak: d =>
        {
            d["phase"] = "done";
            d["end"] = StatusDocs.End(status, StatusDocs.Now - ended);
            more?.Invoke(d);
        });
    }

    // Round 1 in its gate, launcher alive.
    private StatusIndexRig.Added OpeningInGate(string name, TimeSpan started) =>
        _rig.Add(name, _mine, startedAgo: started, tweak: d => d["phase"] = "gate");

    // A later round in its gate, launcher alive.
    private StatusIndexRig.Added InGate(string name, StatusIndexRig.Added first, int round, TimeSpan started, Action<JsonObject>? more = null)
    {
        return _rig.Add(name, _mine, startedAgo: started, chain: first.Id, round: round, tweak: d =>
        {
            d["phase"] = "gate";
            more?.Invoke(d);
        });
    }

    // A later round that wrote its end `ended` ago, launcher alive.
    private StatusIndexRig.Added Closed(string name, StatusIndexRig.Added first, int round, TimeSpan started, TimeSpan ended, string status = "success")
    {
        return _rig.Add(name, _mine, startedAgo: started, chain: first.Id, round: round, tweak: d =>
        {
            d["phase"] = "done";
            d["end"] = StatusDocs.End(status, StatusDocs.Now - ended);
        });
    }

    // A first round whose launcher (900) is gone and that ended `ended` ago.
    private StatusIndexRig.Added DeadOpening(string name, TimeSpan started, TimeSpan ended, string status = "success") =>
        _rig.Add(name, _mine, endedAgo: ended, startedAgo: started, status: status);

    // A later round whose launcher (900) is gone and that ended `ended` ago.
    private StatusIndexRig.Added DeadRound(string name, StatusIndexRig.Added first, int round, TimeSpan started, TimeSpan ended, string status = "success") =>
        _rig.Add(name, _mine, endedAgo: ended, startedAgo: started, status: status, chain: first.Id, round: round);

    private void Repoint(StatusIndexRig.Added run, Action<JsonObject> change)
    {
        JsonObject pointer = run.PointerDoc.DeepClone().AsObject();
        change(pointer);
        _ = _rig.WritePointer(run.Pointer, pointer.ToJsonString());
    }

    private async Task<(string Plain, List<JsonObject> Json)> ListAsync(Func<string, string?> env, params string[] args)
    {
        (int code, string plain) = await _rig.CallAsync(env, args);
        (int jsonCode, string json) = await _rig.CallAsync(env, [.. args, "--json"]);
        Assert.Equal([0, 0], [code, jsonCode]);
        return (plain, StatusIndexRig.JsonRows(json));
    }

    private static string[] JsonNames(List<JsonObject> rows) => [.. rows.Select(r => (r["run"]?["name"] ?? r["name"])!.GetValue<string>()).Order(StringComparer.Ordinal)];

    // Every named run is its own row, plain and --json, and none belongs to a chain.
    private static void AssertAllSeparate((string Plain, List<JsonObject> Json) listing, params string[] names)
    {
        string[] rows = StatusIndexRig.Rows(listing.Plain);
        Assert.Equal(names.Length, rows.Length);
        Assert.All(names, n => Assert.Contains(rows, r => r.Contains(n + " ", StringComparison.Ordinal) || r.EndsWith(n, StringComparison.Ordinal)));
        Assert.Equal([.. names.Order(StringComparer.Ordinal)], JsonNames(listing.Json));
        Assert.All(listing.Json, r => Assert.Null(r["chain"]));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("ticks")]
    [InlineData("boot")]
    [InlineData("no-ticks")]
    [InlineData("no-boot")]
    public async Task A_round_whose_status_file_names_another_launcher_identity_is_its_own_row_and_nothing_is_hiddenAsync(string how)
    {
        _rig.Proc.Add(101, StatusDocs.LauncherTicks);
        StatusIndexRig.Added first = Opening("chain", Min(30), Min(25));
        _ = InGate("chain-gate2", first, 2, Min(20), more: d => _changes[how](d["launcher"]!.AsObject()));

        AssertAllSeparate(await ListAsync(Caller()), "chain", "chain-gate2");
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("ticks")]
    [InlineData("boot")]
    public async Task A_round_whose_pointer_names_another_launcher_than_its_status_file_is_its_own_row_and_nothing_is_hiddenAsync(string how)
    {
        _rig.Proc.Add(101, StatusDocs.LauncherTicks);
        StatusIndexRig.Added first = Opening("chain", Min(30), Min(25));
        StatusIndexRig.Added second = InGate("chain-gate2", first, 2, Min(20));
        Repoint(second, p => _changes[how](p["launcher"]!.AsObject()));

        AssertAllSeparate(await ListAsync(Caller()), "chain", "chain-gate2");
    }

    [Fact]
    public async Task A_first_run_whose_pointer_names_another_launcher_than_its_status_file_proves_no_chain_for_any_roundAsync()
    {
        _rig.Proc.Add(101, StatusDocs.LauncherTicks);
        StatusIndexRig.Added first = Opening("chain", Min(30), Min(25));
        _ = Closed("chain-gate2", first, 2, Min(20), Min(15));
        _ = InGate("chain-gate3", first, 3, Min(10));
        Repoint(first, p => p["launcher"]!["pid"] = 101);

        AssertAllSeparate(await ListAsync(Caller()), "chain", "chain-gate2", "chain-gate3");
    }

    [Theory]
    [InlineData("no-ticks")]
    [InlineData("no-boot")]
    public async Task A_first_run_without_a_complete_launcher_identity_proves_no_chain_for_any_roundAsync(string how)
    {
        StatusIndexRig.Added first = Opening("chain", Min(30), Min(25), more: d => _changes[how](d["launcher"]!.AsObject()));
        _ = Closed("chain-gate2", first, 2, Min(20), Min(15));
        _ = InGate("chain-gate3", first, 3, Min(10));

        AssertAllSeparate(await ListAsync(Caller()), "chain", "chain-gate2", "chain-gate3");
    }

    [Fact]
    public async Task Duplicate_round_numbers_leave_every_row_of_the_chain_separate_and_visibleAsync()
    {
        StatusIndexRig.Added first = Opening("chain", Min(30), Min(25));
        _ = InGate("chain-gate2", first, 2, Min(20));
        _ = InGate("chain-gate2-again", first, 2, Min(10));

        AssertAllSeparate(await ListAsync(Caller()), "chain", "chain-gate2", "chain-gate2-again");
    }

    [Fact]
    public async Task Rounds_whose_first_run_is_not_in_the_listing_stay_separate_and_group_once_the_first_run_isAsync()
    {
        StatusIndexRig.Added first = _rig.Add("chain", ["claude-session:sess-B"], startedAgo: Min(30), tweak: d =>
        {
            d["phase"] = "done";
            d["end"] = StatusDocs.End("success", StatusDocs.Now - Min(25));
        });
        _ = Closed("chain-gate2", first, 2, Min(20), Min(15));
        _ = InGate("chain-gate3", first, 3, Min(10));

        AssertAllSeparate(await ListAsync(Caller()), "chain-gate2", "chain-gate3");
        (string all, List<JsonObject> allJson) = await ListAsync(StatusIndexRig.NoCaller, "--all");

        _ = Assert.Single(StatusIndexRig.Rows(all));
        Assert.Equal(3, allJson.Count);
        Assert.All(allJson, r => Assert.Equal(first.Id, r["chain"]!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task In_the_here_scope_where_there_is_no_pointer_the_status_files_launcher_identity_alone_decides_membershipAsync()
    {
        _rig.Proc.Add(101, StatusDocs.LauncherTicks);
        StatusIndexRig.Added first = Opening("chain", Min(30), Min(25));
        _ = InGate("chain-gate2", first, 2, Min(20), more: d => d["launcher"]!["pid"] = 101);

        (int code, string plain) = await _rig.CallAsync("--here", "--runs-root", _rig.Root);
        (_, string json) = await _rig.CallAsync("--here", "--runs-root", _rig.Root, "--json");

        Assert.Equal(0, code);
        AssertAllSeparate((plain, StatusIndexRig.JsonRows(json)), "chain", "chain-gate2");
    }

    [Fact]
    public async Task The_row_of_a_chain_is_its_worst_round_attention_over_live_and_ended_with_every_round_in_jsonAsync()
    {
        StatusIndexRig.Added first = Opening("chain", Min(60), Min(55));
        _ = Closed("chain-gate2", first, 2, Min(50), Min(45), status: "error");
        _ = InGate("chain-gate3", first, 3, Min(30));

        (string plain, List<JsonObject> json) = await ListAsync(Caller());

        Assert.Matches(@"^error\s+chain-gate2\s", Assert.Single(StatusIndexRig.Rows(plain)));
        Assert.Contains("1 needs attention", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
        Assert.Equal(["success", "error", "gate"], [.. json.Select(r => r["state"]!.GetValue<string>())]);
        Assert.Equal([false, false, true], [.. json.Select(r => r["chain"]!["latest"]!.GetValue<bool>())]);
    }

    [Fact]
    public async Task An_earlier_failed_round_outranks_a_later_successful_oneAsync()
    {
        StatusIndexRig.Added first = DeadOpening("chain", Min(60), Min(55), status: "error");
        _ = DeadRound("chain-gate2", first, 2, Min(50), Min(45));

        (string plain, _) = await ListAsync(Caller());

        Assert.Matches(@"^error\s+chain\s", Assert.Single(StatusIndexRig.Rows(plain)));
        Assert.Contains("1 needs attention", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_tie_the_latest_round_is_the_row_among_attention_rounds_and_among_ended_roundsAsync()
    {
        StatusIndexRig.Added failed = DeadOpening("failing", Min(60), Min(55), status: "error");
        _ = DeadRound("failing-gate2", failed, 2, Min(50), Min(45), status: "timeout");
        StatusIndexRig.Added fine = DeadOpening("fine", Min(60), Min(55));
        _ = DeadRound("fine-gate2", fine, 2, Min(50), Min(45), status: "wrapped-up");

        (string plain, _) = await ListAsync(Caller());

        string[] rows = StatusIndexRig.Rows(plain);
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r => r.StartsWith("timeout", StringComparison.Ordinal) && r.Contains("failing-gate2 ", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("wrapped-up", StringComparison.Ordinal) && r.Contains("fine-gate2 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Since_hides_a_chain_whose_every_round_it_would_hideAsync()
    {
        StatusIndexRig.Added first = DeadOpening("chain", Days(3) + Min(60), Days(3));
        _ = DeadRound("chain-gate2", first, 2, Days(3), Days(3) - Min(10), status: "error");

        (string plain, List<JsonObject> json) = await ListAsync(Caller(), "--since", "1h");

        Assert.Empty(StatusIndexRig.Rows(plain));
        Assert.Empty(json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_old_failed_earlier_round_that_is_the_chains_row_does_not_hide_the_live_round_in_either_listingAsync(bool allScope)
    {
        StatusIndexRig.Added first = Opening("chain", Days(3) + Min(60), Days(3), status: "error");
        _ = InGate("chain-gate2", first, 2, Min(10));

        (string plain, List<JsonObject> json) = allScope
            ? await ListAsync(StatusIndexRig.NoCaller, "--all")
            : await ListAsync(Caller(), "--since", "1h");

        _ = Assert.Single(StatusIndexRig.Rows(plain));
        Assert.Equal(["error", "gate"], [.. json.Select(r => r["state"]!.GetValue<string>())]);
        Assert.Equal(["alive"], [.. json.Select(r => r["liveness"]!.GetValue<string>()).Where(l => l is "alive")]);
    }

    [Fact]
    public async Task A_forged_old_failed_round_that_shares_the_first_runs_launcher_cannot_hide_the_live_first_runAsync()
    {
        StatusIndexRig.Added first = OpeningInGate("chain", Min(30));
        // The forger copies the launcher identity (status file and pointer) and claims round 0 of the chain.
        _ = Closed("forger", first, 0, Days(3) + Min(60), Days(3), status: "error");

        (string plain, List<JsonObject> json) = await ListAsync(Caller(), "--since", "1h");

        _ = Assert.Single(StatusIndexRig.Rows(plain));
        Assert.Equal(2, json.Count);
        Assert.Contains(json, r => r["state"]!.GetValue<string>() is "gate" && r["liveness"]!.GetValue<string>() is "alive");
    }

    [Fact]
    public async Task An_old_failed_latest_round_does_not_hide_an_orphaned_earlier_round_of_the_same_chainAsync()
    {
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        // Launcher 900 is gone: round 1 never wrote an end and its worker lives (orphaned), round 2 failed long ago.
        StatusIndexRig.Added first = _rig.Add("chain", _mine, startedAgo: Days(3) + Min(60), tweak: d => d["launcher"]!["pid"] = 900);
        _ = DeadRound("chain-gate2", first, 2, Days(3), Days(3) - Min(10), status: "error");

        (string plain, List<JsonObject> json) = await ListAsync(Caller(), "--since", "1h");

        _ = Assert.Single(StatusIndexRig.Rows(plain));
        Assert.Equal(2, json.Count);
        Assert.Contains(json, r => r["state"]!.GetValue<string>() is "orphaned");
        Assert.Contains("needs attention", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
    }
}
