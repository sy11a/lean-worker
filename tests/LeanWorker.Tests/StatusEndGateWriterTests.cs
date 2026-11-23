using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The writer of `end.gate` (docs/research/live-status/README.md, sections 4.1 and 4.2; the follow-up review of #147,
/// finding 1): a round whose gate decided to go on to another round records <c>gate: "continue"</c> in the same end
/// record, before the next round starts; every other end (success without a gate, a gate that passed, stuck or failed,
/// a failed worker, a timeout, a signal, a launch failure) has no <c>gate</c> key. Whole launches against a stub worker
/// and real gate commands show it for the rounds of a chain; the writer alone shows it for the ends a launch cannot
/// reach cheaply.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusEndGateWriterTests : IDisposable
{
    private readonly TempDirs _dirs = new();
    private readonly EnvScope _env = EnvScope.Clean();

    public void Dispose()
    {
        _env.Dispose();
        _dirs.Dispose();
    }

    // The status.json of every round of a launch, in round order.
    private static List<JsonObject> Rounds(string root)
    {
        List<JsonObject> rounds = [.. GateLoopTests.RunDirNames(root).Select(n => StatusFileAssert.Read(Path.Combine(root, "runs", n)))];
        return [.. rounds.OrderBy(d => d["run"]!["round"]!.GetValue<int>())];
    }

    private async Task<List<JsonObject>> LaunchAsync(string chain, int expectedCode, string? gateJson = null, Action<Options>? configure = null,
        string stream = RunAsyncGolden.SuccessStream)
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-end");
        JsonObject profile = await GateCommandScripts.ProfileOfAsync(scratch, chain);
        foreach ((string key, JsonNode? value) in gateJson is null ? [] : JsonNode.Parse(gateJson)!.AsObject())
        {
            GateCommandScripts.Gate(profile)[key] = value?.DeepClone();
        }

        RunAsyncGolden.WriteProfile(root, "test", profile);
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.Worker("true", stream), RunAsyncGolden.Tracking(_dirs, configure: configure));
        Assert.True(code == expectedCode, stdout);
        return Rounds(root);
    }

    // Per round, in order: the round's end.gate when it has the key, "none" when it has none.
    private static string GateKeys(List<JsonObject> rounds) =>
        string.Join(' ', rounds.Select(d => d["end"] is JsonObject end && end.ContainsKey("gate") ? end["gate"]!.GetValue<string>() : "none"));

    [Theory]
    [InlineData("a=0:0", 0, null, "none")]
    [InlineData("a=1:2,0:0", 0, null, "continue none")]
    [InlineData("a=1:2,1:1,0:0", 0, null, "continue continue none")]
    [InlineData("a=1:2,1:1", 4, /*lang=json,strict*/ """{"maxRounds":2}""", "continue none")]
    [InlineData("a=2:-1", 5, null, "none")]
    [InlineData("a=1:2,2:-1", 5, null, "continue none")]
    public async Task A_round_whose_gate_goes_on_records_continue_in_its_end_and_no_other_round_doesAsync(string chain, int code, string? gateJson, string expected)
    {
        List<JsonObject> rounds = await LaunchAsync(chain, code, gateJson);

        Assert.Equal(expected, GateKeys(rounds));
        // The record is part of a finished end: the round's own status and its exit code are there too.
        Assert.All(rounds, d => Assert.Equal("done", d["phase"]!.GetValue<string>()));
        Assert.All(rounds.Where(d => d["end"]!.AsObject().ContainsKey("gate")), d => Assert.Equal("success", d["end"]!["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_continue_is_written_with_the_end_before_the_next_round_startsAsync()
    {
        List<JsonObject> rounds = await LaunchAsync("a=1:2,0:0", 0);

        string endedAt = rounds[0]["end"]!["at"]!.GetValue<string>();
        string nextStarted = rounds[1]["spec"]!["started_at"]!.GetValue<string>();
        Assert.Equal("continue", rounds[0]["end"]!["gate"]!.GetValue<string>());
        Assert.True(string.CompareOrdinal(endedAt, nextStarted) <= 0, $"round 1 ended at {endedAt}, round 2 started at {nextStarted}");
        Assert.Equal(["status", "exit_code", "at", "summary", "gate"], GateCommandScripts.Keys(rounds[0]["end"]!.AsObject()), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_run_with_no_gate_or_a_worker_that_failed_has_no_gate_in_its_endAsync()
    {
        List<JsonObject> noGate = await LaunchAsync("a=1:2,0:0", 0, configure: o => o.NoGate = true);
        List<JsonObject> failed = await LaunchAsync("a=1:2,0:0", 1, stream: RunAsyncGolden.ErrorStream);

        Assert.Equal("none", GateKeys(noGate));
        Assert.Equal("none", GateKeys(failed));
    }

    // The ends a launch cannot reach cheaply, through the writer the launch uses.
    [Theory]
    [InlineData("success", 0)]
    [InlineData("error", 1)]
    [InlineData("timeout", 1)]
    [InlineData("wrapped-up", 3)]
    public void An_end_written_without_a_gate_has_no_gate_key(string status, int exitCode)
    {
        using StatusRig rig = new(new OpencodeRuntime());

        rig.Status.Finish(status, exitCode);

        Assert.False(rig.Disk()["end"]!.AsObject().ContainsKey("gate"));
    }

    [Fact]
    public void An_end_written_with_continue_records_it_in_the_same_write_and_nothing_else_changes()
    {
        using StatusRig rig = new(new OpencodeRuntime());

        rig.Status.Finish("success", 0, "continue");

        JsonObject doc = rig.Disk();
        Assert.Equal("done", doc["phase"]!.GetValue<string>());
        Assert.Equal("continue", doc["end"]!["gate"]!.GetValue<string>());
        Assert.Equal(["status", "exit_code", "at", "summary", "gate"], GateCommandScripts.Keys(doc["end"]!.AsObject()), StringComparer.Ordinal);
    }

    [Fact]
    public void A_signal_or_a_launch_failure_leaves_no_gate_in_the_end()
    {
        using StatusRig signalled = new(new OpencodeRuntime());
        using StatusRig failed = new(new OpencodeRuntime());

        Assert.True(signalled.Status.Signalled(PosixSignal.SIGTERM));
        failed.Status.Fail("it broke", summaryStatus: null);

        Assert.False(signalled.Disk()["end"]!.AsObject().ContainsKey("gate"));
        Assert.False(failed.Disk()["end"]!.AsObject().ContainsKey("gate"));
    }
}
