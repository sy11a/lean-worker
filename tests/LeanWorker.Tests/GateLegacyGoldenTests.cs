using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Golden tests of the single-command gate (<c>gate.command</c>): the run directory's file names, the
/// summary's gate section and gate.json keys, the result lines, the note line, the next round's task text
/// and the profile parse messages. The <c>gate.commands</c> form must leave every one of them byte-identical,
/// so these use only what the single-command form had before it existed.
/// </summary>
[Collection("launcher-process-state")]
public sealed class GateLegacyGoldenTests : IDisposable
{
    private static readonly string[] _gateSectionKeys =
        ["chain_id", "round", "outcome", "exit_code", "count", "decision", "error", "report_path", "duration_ms"];

    private static readonly string[] _gateJsonKeys =
        ["outcome", "exit_code", "count", "report_path", "error", "duration_ms", "command", "env_names", "path"];

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private async Task<(string Root, string Scratch, string Script)> NewLegacySetupAsync(params (int ExitCode, int Count)[] rounds)
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-legacy");
        string script = await GateLoopTests.WriteSequenceScriptAsync(scratch, rounds);
        RunAsyncGolden.WriteProfile(root, "test", GateLoopTests.GateProfile(script));
        return (root, scratch, script);
    }

    private async Task<(int Code, string Stdout)> RunAsync(string root, Action<Options>? configure = null) =>
        await GateCommandScripts.RunAsync(_dirs, root, configure: configure);

    internal static string[] GateFiles(string runDir) =>
        [.. Directory.GetFiles(runDir).Select(f => Path.GetFileName(f)).Where(n => n.StartsWith("gate", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    internal static string[] GateLines(string stdout) =>
        [.. stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("gate:     ", StringComparison.Ordinal))];

    [Fact]
    public async Task A_two_round_single_command_chain_keeps_its_records_lines_and_task_textAsync()
    {
        (string root, string scratch, string script) = await NewLegacySetupAsync((1, 3), (0, 0));

        (int code, string stdout) = await RunAsync(root);

        Assert.Equal(0, code);
        Assert.Equal(
            ["gate:     round 1, 3 finding(s); starting round 2", "gate:     clean after 2 round(s), findings 3→0"],
            GateLines(stdout));

        string[] names = GateLoopTests.RunDirNames(root);
        Assert.Equal(2, names.Length);
        string round1 = Path.Combine(root, "runs", names[0]);
        string round2 = Path.Combine(root, "runs", names[1]);
        foreach (string runDir in new[] { round1, round2 })
        {
            Assert.Equal(["gate.json", "gate.log"], GateFiles(runDir), StringComparer.Ordinal);
            JsonObject gate = RunAsyncGolden.Summary(runDir)["gate"]!.AsObject();
            Assert.Equal(_gateSectionKeys, gate.Select(kv => kv.Key), StringComparer.Ordinal);
            JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
            Assert.Equal(_gateJsonKeys, gateJson.Select(kv => kv.Key), StringComparer.Ordinal);
        }

        string report = Path.Combine(scratch, "report1.json");
        JsonObject gate1 = RunAsyncGolden.Summary(round1)["gate"]!.AsObject();
        GateCommandScripts.AssertFields(gate1, /*lang=json,strict*/ """{"outcome":"findings","decision":"continue","exit_code":1,"count":3,"error":null}""");
        Assert.Equal(report, gate1["report_path"]!.GetValue<string>());
        Assert.Contains("gate: sh " + script + " (rounds 5, $6)", RunAsyncGolden.Notes_(RunAsyncGolden.Summary(round1)), StringComparer.Ordinal);

        string nl = Environment.NewLine;
        string expectedTask =
            "do nothing" + nl + nl + Launcher.GateHeading + nl + nl +
            "The previous worker finished, but the gate command still reports 3 finding(s) (round 1). Fix them; do not redo finished work. " +
            "Check the current state first (git status, git diff --stat)." + nl + nl +
            "Full report: " + report + nl + nl + "sarif: " + report;
        Assert.Equal(expectedTask, await File.ReadAllTextAsync(Path.Combine(round2, "task.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_single_command_error_and_stuck_keep_their_result_lines_and_errorAsync()
    {
        (string errorRoot, string _, string _) = await NewLegacySetupAsync((2, 0));
        (int errorCode, string errorStdout) = await RunAsync(errorRoot);

        Assert.Equal(5, errorCode);
        Assert.Equal(["gate:     ERROR: gate exited 2"], GateLines(errorStdout));
        Assert.Equal("gate exited 2", RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(errorStdout))["gate"]!["error"]!.GetValue<string>());

        (string stuckRoot, string _, string _) = await NewLegacySetupAsync((1, 3), (1, 3));
        (int stuckCode, string stuckStdout) = await RunAsync(stuckRoot, o => o.GateMaxRounds = 2);

        Assert.Equal(4, stuckCode);
        Assert.Equal(
            ["gate:     round 1, 3 finding(s); starting round 2", "gate:     STUCK after 2 round(s), findings 3→3 (max rounds)"],
            GateLines(stuckStdout));
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"command":[]}""", "profile gate.command must be a non-empty array of strings")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh",5]}""", "profile gate.command must be a non-empty array of non-empty strings")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"reportFromLastLine":5}""", "profile gate.reportFromLastLine must be a regex string")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"reportFromLastLine":"^x$"}""", "profile gate.reportFromLastLine must have at least one capture group")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"countPath":""}""", "profile gate.countPath must be a non-empty string")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"countPath":"a["}""", "profile gate.countPath has an invalid segment: 'a['")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"feedbackMaxChars":0}""", "profile gate.feedbackMaxChars must be a positive integer")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"timeoutMinutes":0}""", "profile gate.timeoutMinutes must be a positive integer")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"maxRounds":0}""", "profile gate.maxRounds must be a positive integer")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"maxTotalUsd":0}""", "profile gate.maxTotalUsd must be a number greater than 0")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"env":["bad name"]}""", "profile gate.env entry must match [A-Za-z_][A-Za-z0-9_]*[*]?")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"trust":[7]}""", "profile gate.trust[0] must be a non-empty string")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"outputs":"x"}""", "profile gate.outputs must be an array of paths")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"outputs":[7]}""", "profile gate.outputs[0] must be a non-empty string")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"outputs":["a/../b"]}""", "profile gate.outputs[0] must not contain a '..' segment: a/../b")]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"outputs":["*.sarif"]}""", "profile gate.outputs[0] must not contain glob characters: *.sarif")]

    // Two errors at once: the legacy parser read the shared keys before outputs, so maxRounds is reported first.
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"outputs":[7],"maxRounds":0}""", "profile gate.maxRounds must be a positive integer")]
    public void A_single_command_profile_keeps_its_parse_messages(string gateJson, string expected)
    {
        JsonObject profile = new() { ["gate"] = JsonNode.Parse(gateJson) };

        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(profile, Directory.GetCurrentDirectory()));

        Assert.Equal(expected, ex.Message);
    }
}
