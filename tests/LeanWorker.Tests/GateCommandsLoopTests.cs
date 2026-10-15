using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// End-to-end tests of the <c>gate.commands</c> round loop through Launcher.RunAsync: a stubbed worker and
/// one real sh script per named command (<see cref="GateCommandScripts"/>, whose chain notation the tests
/// use), each varying its outcome per round.
/// </summary>
[Collection("launcher-process-state")]
public sealed class GateCommandsLoopTests : IDisposable
{
    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private sealed record Chain(string Root, string Scratch, int Code, string Stdout)
    {
        public string RunDir(int round) => Path.Combine(Root, "runs", GateLoopTests.RunDirNames(Root)[round - 1]);

        public bool Ran(string name) => File.Exists(GateCommandScripts.Marker(Scratch, name));
    }

    /// <summary>
    /// Writes the commands of <paramref name="chain"/> (see <see cref="GateCommandScripts"/>), adds the
    /// shared keys of <paramref name="gateJson"/> to the gate, runs the launcher and checks its exit code
    /// and its "gate:" result lines.
    /// </summary>
    private async Task<Chain> RunChainAsync(string chain, int expectedCode, string[] expectedLines, string? gateJson = null, Action<Options>? configure = null, string stream = RunAsyncGolden.SuccessStream)
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-commands");
        JsonObject profile = await GateCommandScripts.ProfileOfAsync(scratch, chain);
        foreach ((string key, JsonNode? value) in gateJson is null ? [] : JsonNode.Parse(gateJson)!.AsObject())
        {
            GateCommandScripts.Gate(profile)[key] = value?.DeepClone();
        }

        RunAsyncGolden.WriteProfile(root, "test", profile);
        (int code, string stdout) = await GateCommandScripts.RunAsync(_dirs, root, configure: configure, stream: stream);
        Assert.True(code == expectedCode, stdout);
        Assert.Equal(expectedLines, GateLegacyGoldenTests.GateLines(stdout));
        return new Chain(root, scratch, code, stdout);
    }

    [Fact]
    public async Task Three_clean_commands_end_clean_with_per_command_records_and_an_aggregate_summaryAsync()
    {
        Chain chain = await RunChainAsync("a=0:0 b=0:-1 c=0:0", 0, ["gate:     clean after 1 round(s), findings 0"]);

        string runDir = chain.RunDir(1);
        Assert.Equal(["gate-a.json", "gate-a.log", "gate-b.json", "gate-b.log", "gate-c.json", "gate-c.log"], GateLegacyGoldenTests.GateFiles(runDir), StringComparer.Ordinal);
        JsonObject gate = GateCommandScripts.Section(runDir);
        GateCommandScripts.AssertFields(gate, /*lang=json,strict*/ """{"decision":"clean","outcome":"clean","exit_code":0,"count":0,"report_path":null,"error":null}""");
        Assert.Equal(["a", "b", "c"], GateCommandScripts.CommandNames(gate), StringComparer.Ordinal);
        JsonObject b = gate["commands"]![1]!.AsObject();
        Assert.Equal(["name", "outcome", "exit_code", "count", "report_path", "error", "duration_ms"], GateCommandScripts.Keys(b), StringComparer.Ordinal);
        GateCommandScripts.AssertFields(b, /*lang=json,strict*/ """{"name":"b","outcome":"clean","exit_code":0,"report_path":null,"error":null}""");

        string note = Assert.Single(RunAsyncGolden.Notes_(RunAsyncGolden.Summary(runDir)), n => n.StartsWith("gate: ", StringComparison.Ordinal));
        string dir = chain.Scratch + Path.DirectorySeparatorChar;
        Assert.Equal($"gate: a=sh {dir}a.sh; b=sh {dir}b.sh; c=sh {dir}c.sh (rounds 5, $6)", note);
    }

    [Fact]
    public async Task Findings_from_two_of_three_commands_feed_the_next_round_and_the_chain_ends_cleanAsync()
    {
        Chain chain = await RunChainAsync(
            "a=1:2,0:0 b=0:0 c=1:1,0:0", 0,
            ["gate:     round 1, 3 finding(s) (a 2, b 0, c 1); starting round 2", "gate:     clean after 2 round(s), findings 3→0"]);

        JsonObject gate1 = GateCommandScripts.Section(chain.RunDir(1));
        GateCommandScripts.AssertFields(gate1, /*lang=json,strict*/ """{"decision":"continue","outcome":"findings","exit_code":1,"count":3,"report_path":null}""");
        long summed = gate1["commands"]!.AsArray().Sum(c => c!["duration_ms"]!.GetValue<long>());
        Assert.InRange(gate1["duration_ms"]!.GetValue<long>() - summed, -2, 2);

        string task2 = await File.ReadAllTextAsync(Path.Combine(chain.RunDir(2), "task.md"), TestContext.Current.CancellationToken);
        Assert.Equal(2, task2.Split(Launcher.GateHeading, StringSplitOptions.None).Length);
        Assert.Contains("still report 3 finding(s) (round 1: a 2, b 0, c 1)", task2, StringComparison.Ordinal);
        int a = task2.IndexOf("### a: 2 finding(s)", StringComparison.Ordinal);
        int c = task2.IndexOf("### c: 1 finding(s)", StringComparison.Ordinal);
        Assert.True(a >= 0 && c > a, "a section for a, then one for c, in profile order");
        Assert.DoesNotContain("### b", task2, StringComparison.Ordinal);
        Assert.Contains("Full report: " + Path.Combine(chain.Scratch, "a-report1.json"), task2, StringComparison.Ordinal);
        Assert.Contains("Full report: " + Path.Combine(chain.Scratch, "c-report1.json"), task2, StringComparison.Ordinal);
        Assert.Contains("a output", task2, StringComparison.Ordinal);
        Assert.DoesNotContain("b output", task2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_exits_2_ends_the_round_and_the_chain_without_running_the_restAsync()
    {
        Chain chain = await RunChainAsync("a=1:2 b=2:-1 c=0:0", 5, ["gate:     ERROR: b: gate exited 2"]);

        Assert.False(chain.Ran("c"), "c must not run after b's error");
        Assert.Equal(["gate-a.json", "gate-a.log", "gate-b.json", "gate-b.log"], GateLegacyGoldenTests.GateFiles(chain.RunDir(1)), StringComparer.Ordinal);
        JsonObject gate = GateCommandScripts.Section(chain.RunDir(1));
        GateCommandScripts.AssertFields(gate, /*lang=json,strict*/ """{"decision":"error","outcome":"error","exit_code":2,"count":2,"error":"b: gate exited 2"}""");
        Assert.Equal(["a", "b"], GateCommandScripts.CommandNames(gate), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("a=0:0 b=1:-1 c=0:0", "gate:     ERROR: b: gate exited 1 but report path regex did not match the last stdout line 'b output'")]
    [InlineData("a=0:0 b=1:0 c=0:0", "gate:     ERROR: b: gate exited 1 but runs[0].results has 0 entries")]
    public async Task A_command_that_exits_1_without_findings_is_an_errorAsync(string commands, string line)
    {
        Chain chain = await RunChainAsync(commands, 5, [line]);

        Assert.False(chain.Ran("c"), "c must not run after b's error");
    }

    // Every cap is chain-wide: maxRounds and --gate-max-rounds count rounds, not command runs, and the cost cap is
    // one cap (each round's metered worker costs $0.0022, so two commands must not double the $0.003 cap: the
    // chain stops after round 2 although the total still falls). The stuck line ends with the last breakdown.
    [Theory]
    [InlineData("a=1:2,1:1 b=1:1", /*lang=json,strict*/ """{"maxRounds":2}""", null, "3 finding(s) (a 2, b 1)", "3→2 (max rounds; last: a 1, b 1)")]
    [InlineData("a=1:3,1:2,1:1 b=1:1", null, 2, "4 finding(s) (a 3, b 1)", "4→3 (max rounds; last: a 2, b 1)")]
    [InlineData("a=1:3,1:2,1:1 b=1:3,1:2,1:1", /*lang=json,strict*/ """{"maxTotalUsd":0.003}""", null, "6 finding(s) (a 3, b 3)", "6→4 (cost cap $0.003; last: a 2, b 2)")]
    public async Task A_chain_wide_cap_ends_the_chain_stuck_with_the_breakdownAsync(string commands, string? gateJson, int? gateMaxRounds, string round1, string stuck)
    {
        Chain chain = await RunChainAsync(
            commands, 4,
            ["gate:     round 1, " + round1 + "; starting round 2", "gate:     STUCK after 2 round(s), findings " + stuck],
            gateJson, o => o.GateMaxRounds = gateMaxRounds, GateTrustLoopTests.MeteredSuccessStream);

        Assert.Equal(2, GateLoopTests.RunDirNames(chain.Root).Length);
        Assert.Equal(["a", "b"], GateCommandScripts.CommandNames(GateCommandScripts.Section(chain.RunDir(2))), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_gate_or_a_worker_that_does_not_succeed_runs_no_commandAsync(bool noGate)
    {
        Chain chain = noGate
            ? await RunChainAsync("a=0:0 b=0:0", 0, [], configure: o => o.NoGate = true)
            : await RunChainAsync("a=0:0 b=0:0", 1, [], stream: RunAsyncGolden.ErrorStream);

        Assert.False(chain.Ran("a"), "a must not run");
        Assert.False(chain.Ran("b"), "b must not run");
        Assert.Null(RunAsyncGolden.Summary(chain.RunDir(1))["gate"]);
    }

    // A hand-built round-2 spec that carries only one of the two frozen values (b's executable is a decoy,
    // or b's PATH names no real directory) is resolved from scratch, as round 1 does: both values are
    // rebuilt, so the decoy never runs and b's script finds its tools on the real PATH.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_round_two_spec_missing_one_frozen_value_gets_both_resolved_againAsync(bool decoyExecutable)
    {
        string scratch = _dirs.Create("lw-gate-commands");
        string decoy = Path.Combine(scratch, "decoy");
        await GateLoopTests.WriteExecutableAsync(decoy, "#!/bin/sh\ntouch '" + GateCommandScripts.Marker(scratch, "decoy") + "'\nexit 0\n");
        List<GateSpec> specs = GateSpec.FromProfileAll(await GateCommandScripts.ProfileOfAsync(scratch, "a=0:0 b=0:0"), scratch)!;
        string gatePath = GateTrust.BuildGatePath();
        specs[0] = specs[0] with { ResolvedExecutable = GateTrust.ResolveExecutableStrict(specs[0].Command, scratch), GatePath = gatePath };
        specs[1] = decoyExecutable
            ? specs[1] with { ResolvedExecutable = decoy }
            : specs[1] with { GatePath = Path.Combine(scratch, "no-such-bin") };
        string root = _dirs.NewRoot();
        RunAsyncGolden.WriteProfile(root, "test", await GateCommandScripts.ProfileOfAsync(scratch, "x=0:0"));

        (int code, string stdout) = await GateCommandScripts.RunAsync(_dirs, root, gateContext: GateCommandScripts.RoundTwoContext(specs, "feedback"));

        Assert.True(code is 0, stdout);
        Assert.False(File.Exists(GateCommandScripts.Marker(scratch, "decoy")), "the decoy must not run");
        Assert.True(File.Exists(GateCommandScripts.Marker(scratch, "b")), "b must run with the real PATH");
    }

    [Fact]
    public async Task Round_two_runs_round_ones_commands_even_when_profiles_json_changedAsync()
    {
        string scratch = _dirs.Create("lw-gate-commands");
        List<GateSpec> round1Specs = GateSpec.FromProfileAll(await GateCommandScripts.ProfileOfAsync(scratch, "a=0:0 b=0:0"), scratch)!;
        // profiles.json now names other commands; round 2 must still run round 1's a and b.
        string root = _dirs.NewRoot();
        RunAsyncGolden.WriteProfile(root, "test", await GateCommandScripts.ProfileOfAsync(scratch, "x=0:0 y=0:0"));
        const string Feedback = "round 1 feedback for the commands form";

        (int code, string stdout) = await GateCommandScripts.RunAsync(_dirs, root, gateContext: GateCommandScripts.RoundTwoContext(round1Specs, Feedback));

        Assert.Equal(0, code);
        Assert.False(File.Exists(GateCommandScripts.Marker(scratch, "x")), "x must not run");
        Assert.False(File.Exists(GateCommandScripts.Marker(scratch, "y")), "y must not run");
        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        Assert.Equal(["gate-a.json", "gate-a.log", "gate-b.json", "gate-b.log"], GateLegacyGoldenTests.GateFiles(runDir), StringComparer.Ordinal);
        Assert.Equal(["a", "b"], GateCommandScripts.CommandNames(GateCommandScripts.Section(runDir)), StringComparer.Ordinal);
        string nl = Environment.NewLine;
        Assert.Equal(
            "do nothing" + nl + nl + Launcher.GateHeading + nl + nl + Feedback,
            await File.ReadAllTextAsync(Path.Combine(runDir, "task.md"), TestContext.Current.CancellationToken));
    }
}
