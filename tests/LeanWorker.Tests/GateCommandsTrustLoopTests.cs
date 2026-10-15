using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// End-to-end tests of the gate trust boundary for the <c>gate.commands</c> form: workers and command
/// scripts that really rewrite trusted files, PATH directories or each other's outputs, and the launcher's
/// refusals of profiles whose commands would collide.
/// </summary>
[Collection("launcher-process-state")]
public sealed class GateCommandsTrustLoopTests : IDisposable
{
    private const string Rewrite = "printf '#!/bin/sh\\nexit 0\\n' > '";

    private static readonly string[] _twoCommands = ["one", "two"];

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private sealed record Setup(string Root, string Scratch)
    {
        public string Script(string name) => Path.Combine(Scratch, name + ".sh");

        public string WorkerMarker => Path.Combine(Scratch, "worker-ran");

        public bool Ran(string name) => File.Exists(GateCommandScripts.Marker(Scratch, name));
    }

    private sealed record Run(Setup Setup, int Code, string Stdout);

    private Setup NewSetup() => new(_dirs.NewRoot(), _dirs.Create("lw-gate-commands-trust"));

    /// <summary>
    /// Writes the profile with two commands, "one" and "two", clean in every round; the preludes run inside
    /// them before they report, and <paramref name="tweakGate"/> may change the gate object (each entry's
    /// <c>command</c> is <c>["sh", script]</c>).
    /// </summary>
    private static async Task WriteTwoCommandsAsync(Setup setup, string onePrelude = "", string twoPrelude = "", Action<JsonObject>? tweakGate = null)
    {
        string one = await GateCommandScripts.WriteAsync(setup.Scratch, "one", "0:0", onePrelude);
        string two = await GateCommandScripts.WriteAsync(setup.Scratch, "two", "0:0", twoPrelude);
        JsonObject profile = GateCommandScripts.Profile(GateCommandScripts.Entry("one", one), GateCommandScripts.Entry("two", two));
        tweakGate?.Invoke(GateCommandScripts.Gate(profile));
        RunAsyncGolden.WriteProfile(setup.Root, "test", profile);
    }

    /// <summary>
    /// Writes the two clean commands with the preludes <paramref name="shells"/> gives for the new setup and
    /// runs the launcher with the worker shell it gives; <paramref name="prepare"/> runs in the working directory.
    /// </summary>
    private async Task<Run> RunTwoCommandsAsync(Func<Setup, (string OnePrelude, string TwoPrelude, string Worker)> shells, Action? prepare = null)
    {
        Setup setup = NewSetup();
        (string onePrelude, string twoPrelude, string worker) = shells(setup);
        await WriteTwoCommandsAsync(setup, onePrelude, twoPrelude);
        (int code, string stdout) = await RunAsync(setup, worker, prepare);
        return new Run(setup, code, stdout);
    }

    /// <summary>
    /// Runs the two clean commands with <paramref name="dir"/> first on the PATH; <paramref name="shells"/>
    /// gets the shell line that plants a file in it and returns the two preludes and the worker shell.
    /// </summary>
    private async Task<Run> RunWithDirectoryOnPathAsync(string dir, Func<string, (string OnePrelude, string TwoPrelude, string Worker)> shells)
    {
        return await RunTwoCommandsAsync(
            _ => shells("touch '" + Path.Combine(dir, "planted") + "'"),
            () => Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")));
    }

    /// <summary>
    /// Command "one" (rounds <paramref name="oneRounds"/>) writes its report to its declared output
    /// <c>out/one.sarif</c>, which is also its argv entry and exists before the worker runs, so it is a
    /// trusted input; command "two" runs <paramref name="twoPrelude"/> and reports clean.
    /// </summary>
    private async Task<Run> RunDeclaredOutputAsync(string twoPrelude, string oneRounds)
    {
        Setup setup = NewSetup();
        string one = await GateCommandScripts.WriteAsync(setup.Scratch, "one", oneRounds, "mkdir -p out\n", "$1");
        string two = await GateCommandScripts.WriteAsync(setup.Scratch, "two", "0:0", twoPrelude);
        JsonObject oneEntry = GateCommandScripts.Entry("one", one, "out/one.sarif");
        oneEntry["outputs"] = new JsonArray(["out/one.sarif"]);
        RunAsyncGolden.WriteProfile(setup.Root, "test", GateCommandScripts.Profile(oneEntry, GateCommandScripts.Entry("two", two)));

        (int code, string stdout) = await RunAsync(setup, prepare: () =>
        {
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "out");
            _ = Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "one.sarif"), "old");
        });
        return new Run(setup, code, stdout);
    }

    private async Task<(int Code, string Stdout)> RunAsync(Setup setup, string workerShell = "true", Action? prepare = null) =>
        await GateCommandScripts.RunAsync(_dirs, setup.Root, workerShell, prepare);

    private async Task<LaunchException> RunRefusedAsync(Setup setup, Action? prepare = null)
    {
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync(setup, "touch '" + setup.WorkerMarker + "'", prepare));
        Assert.False(File.Exists(setup.WorkerMarker), "the worker must not run");
        Assert.False(setup.Ran("one"), "no command may run");
        return ex;
    }

    private static JsonObject CommandJson(string stdout, string name) =>
        GateCommandScripts.ReadJson(Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "gate-" + name + ".json"));

    // A directory where the command's log should go makes the gate runner fail before the command starts.
    private static string LogDirectoryShell(Setup setup, string name) =>
        "for d in '" + setup.Root + "'/runs/*/; do mkdir \"${d%/}/gate-" + name + ".log\"; done\n";

    /// <summary>
    /// The chain ended in an error whose message starts with <paramref name="expectedError"/> (the
    /// failing command's name first, except for a violation found before the round's first command) and
    /// names <paramref name="expectedPath"/>. The summary records the commands up to
    /// <paramref name="failing"/>, whose gate-&lt;name&gt;.json has <paramref name="failingFields"/>; the
    /// command processes that ran are exactly <paramref name="processesRan"/>.
    /// </summary>
    private static void AssertError(Run run, string failing, string expectedError, string expectedPath, string failingFields, params string[] processesRan)
    {
        Assert.True(run.Code is 5, run.Stdout);
        Assert.Contains("gate:     ERROR: " + expectedError, run.Stdout, StringComparison.Ordinal);
        Assert.Contains(expectedPath, run.Stdout, StringComparison.Ordinal);
        JsonObject gate = GateCommandScripts.Section(RunAsyncGolden.RunDirFrom(run.Stdout));
        Assert.Equal("error", gate["decision"]!.GetValue<string>());
        Assert.StartsWith(expectedError, gate["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(_twoCommands[..(Array.IndexOf(_twoCommands, failing) + 1)], GateCommandScripts.CommandNames(gate), StringComparer.Ordinal);
        GateCommandScripts.AssertFields(CommandJson(run.Stdout, failing), failingFields);
        Assert.Equal(processesRan, _twoCommands.Where(n => run.Setup.Ran(n)), StringComparer.Ordinal);
    }

    // A check before the round's first command failed: no command ran, and the message names none.
    private static void AssertPreRoundError(Run run, string expectedError, string expectedPath) =>
        AssertError(run, "one", expectedError, expectedPath, /*lang=json,strict*/ """{"outcome":"error","exit_code":-1}""");

    // The gate runner could not start command <paramref name="name"/>; the message names it.
    private static void AssertStartFailure(Run run, string name, params string[] processesRan) =>
        AssertError(run, name, name + ": the gate could not be run: ", "gate-" + name + ".log", /*lang=json,strict*/ """{"outcome":"error","exit_code":-1}""", processesRan);

    [Fact]
    public async Task A_worker_that_changes_the_second_commands_script_stops_the_round_before_the_first_command_naming_no_commandAsync()
    {
        Run run = await RunTwoCommandsAsync(s => (string.Empty, string.Empty, Rewrite + s.Script("two") + "'"));

        AssertPreRoundError(run, "the worker changed files the gate trusts: ", run.Setup.Script("two"));
    }

    [Fact]
    public async Task A_first_command_that_rewrites_the_second_commands_script_ends_the_round_naming_itselfAsync()
    {
        Run run = await RunTwoCommandsAsync(s => (Rewrite + s.Script("two") + "'\n", string.Empty, "true"));

        AssertError(run, "one", "one: trusted files changed while the gate ran: ", run.Setup.Script("two"), /*lang=json,strict*/ """{"outcome":"error","exit_code":0}""", "one");
    }

    [Fact]
    public async Task A_command_that_rewrites_its_own_declared_argv_output_passes_the_later_checks_and_the_next_roundAsync()
    {
        // Round 1: one finds 3 and writes them to out/one.sarif; two's checks must accept that write. Round 2's
        // pre-check must accept it too, and one then writes a clean report over it.
        Run run = await RunDeclaredOutputAsync(string.Empty, "1:3,0:0");

        Assert.True(run.Code is 0, run.Stdout);
        Assert.Equal(
            ["gate:     round 1, 3 finding(s) (one 3, two 0); starting round 2", "gate:     clean after 2 round(s), findings 3→0"],
            GateLegacyGoldenTests.GateLines(run.Stdout));
        Assert.True(run.Setup.Ran("two"), "two should have run");
    }

    [Fact]
    public async Task A_second_command_that_writes_the_first_commands_declared_output_is_a_violationAsync()
    {
        Run run = await RunDeclaredOutputAsync("printf 'x' > out/one.sarif\n", "0:0");

        AssertError(run, "two", "two: trusted files changed while the gate ran: ", "one.sarif", /*lang=json,strict*/ """{"outcome":"error","exit_code":0}""", "one", "two");
    }

    // Without a trust entry, command one declares the same output as command two; with one, command two's
    // output is a gate.trust literal. Either way the launch stops before the worker, naming command two.
    [Theory]
    [InlineData(null, "is declared by two gate commands")]
    [InlineData("out/r.sarif", "is a trusted gate input")]
    public async Task A_second_commands_output_that_collides_is_a_launch_error_naming_itAsync(string? trust, string reason)
    {
        Setup setup = NewSetup();
        await WriteTwoCommandsAsync(setup, tweakGate: gate =>
        {
            gate["trust"] = new JsonArray([trust ?? "unrelated.props"]);
            gate["commands"]![0]!["outputs"] = new JsonArray([trust is null ? "out/r.sarif" : "out/other.sarif"]);
            gate["commands"]![1]!["outputs"] = new JsonArray(["out/r.sarif"]);
        });
        string expected = string.Empty;

        LaunchException ex = await RunRefusedAsync(setup, () => expected = Path.Combine(Directory.GetCurrentDirectory(), "out", "r.sarif"));

        Assert.Equal("gate.commands[1].outputs[0] '" + expected + "' " + reason, ex.Message);
    }

    [Fact]
    public async Task A_second_command_whose_report_path_is_a_trusted_input_is_a_violation_naming_itAsync()
    {
        Run run = await RunTwoCommandsAsync(s => (string.Empty, "echo 'sarif: " + s.Script("one") + "'\nexit 3\n", "true"));

        string one = run.Setup.Script("one");
        AssertError(run, "two", "two: the gate's report path is a trusted input: " + one, one, /*lang=json,strict*/ """{"outcome":"error","exit_code":3}""", "one", "two");
    }

    [Fact]
    public async Task A_PATH_directory_that_changes_while_the_first_command_runs_stops_the_round_before_the_secondAsync()
    {
        string dirA = _dirs.Create("lw-gate-commands-path");

        Run run = await RunWithDirectoryOnPathAsync(dirA, plant => (plant + "\n", string.Empty, "true"));

        AssertError(run, "one", "one: a gate PATH directory changed while the gate ran: ", dirA, /*lang=json,strict*/ """{"outcome":"error","exit_code":0}""", "one");
    }

    [Fact]
    public async Task A_worker_that_changes_a_PATH_directory_stops_the_round_before_the_first_command_naming_no_commandAsync()
    {
        string dirA = _dirs.Create("lw-gate-commands-path");

        Run run = await RunWithDirectoryOnPathAsync(dirA, plant => (string.Empty, string.Empty, plant));

        AssertPreRoundError(run, "the worker changed a gate PATH directory: ", dirA);
    }

    [Fact]
    public async Task A_second_command_that_cannot_be_started_ends_the_round_naming_itAsync()
    {
        Run run = await RunTwoCommandsAsync(s => (LogDirectoryShell(s, "two"), string.Empty, "true"));

        AssertStartFailure(run, "two", "one");
    }

    [Fact]
    public async Task A_first_command_that_cannot_be_started_is_named_although_no_command_ranAsync()
    {
        Run run = await RunTwoCommandsAsync(s => (string.Empty, string.Empty, LogDirectoryShell(s, "one")));

        AssertStartFailure(run, "one");
    }

    [Fact]
    public async Task An_unresolvable_argv0_of_the_third_command_is_a_launch_error_before_the_workerAsync()
    {
        Setup setup = NewSetup();
        await WriteTwoCommandsAsync(setup, tweakGate: gate =>
            gate["commands"]!.AsArray().Add(new JsonObject { ["name"] = "three", ["command"] = new JsonArray(["lw-gate-commands-no-such-tool"]) }));

        LaunchException ex = await RunRefusedAsync(setup);

        Assert.Contains("lw-gate-commands-no-such-tool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_command_gets_the_gate_env_patterns_and_none_gets_the_api_keyAsync()
    {
        await WithEnvironmentAsync("LW_TEST_GATECMD", "x", async () =>
        {
            const string Prelude = "echo \"seen=$LW_TEST_GATECMD key=$ANTHROPIC_API_KEY.\"\n";
            Setup setup = NewSetup();
            await WriteTwoCommandsAsync(setup, Prelude, Prelude, gate => gate["env"] = new JsonArray(["LW_TEST_GATECMD*"]));

            (int code, string stdout) = await RunAsync(setup);

            Assert.True(code is 0, stdout);
            foreach (string command in _twoCommands)
            {
                string[] envNames = [.. CommandJson(stdout, command)["env_names"]!.AsArray().Select(n => n!.GetValue<string>())];
                Assert.True(envNames.Contains("LW_TEST_GATECMD", StringComparer.Ordinal) && !envNames.Contains("ANTHROPIC_API_KEY", StringComparer.Ordinal), string.Join(',', envNames));
                string log = await File.ReadAllTextAsync(Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "gate-" + command + ".log"), TestContext.Current.CancellationToken);
                Assert.Contains("seen=x key=.", log, StringComparison.Ordinal);
            }
        });
    }

    private static async Task WithEnvironmentAsync(string name, string value, Func<Task> body)
    {
        string? old = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, old);
        }
    }
}
