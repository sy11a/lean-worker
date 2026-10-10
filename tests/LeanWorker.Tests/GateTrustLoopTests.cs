using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// End-to-end tests of the gate trust boundary through Launcher.RunAsync: a stubbed worker that tampers
/// with (or leaves alone) the files the gate trusts, and a gate script that records whether it ran.
/// </summary>
[Collection("launcher-process-state")]
public sealed class GateTrustLoopTests : IDisposable
{
    // A success stream with token usage, so the meter has something to price (haiku list price: $0.0022).
    private const string MeteredSuccessStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}'
        printf '%s\n' '{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":3,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":2,"permission_denials":[]}'
        """;

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private sealed record Setup(string Root, string Marker, string Script);

    private async Task<Setup> NewSetupAsync(Action<JsonObject>? tweakGate = null, string? scriptBody = null)
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-trust");
        string marker = Path.Combine(scratch, "gate-ran");
        string script = Path.Combine(scratch, "gate.sh");
        await GateLoopTests.WriteExecutableAsync(script, scriptBody ?? ("#!/bin/sh\ntouch '" + marker + "'\nexit 0\n"));
        JsonObject profile = GateLoopTests.GateProfile(script);
        tweakGate?.Invoke(profile["gate"]!.AsObject());
        RunAsyncGolden.WriteProfile(root, "test", profile);
        return new Setup(root, marker, script);
    }

    /// <summary>
    /// Runs the launcher with a worker that first executes <paramref name="workerShell"/> (in the launcher's
    /// working directory) and then reports success. <paramref name="prepare"/> runs inside the run's working
    /// directory before the launch, so it can create the files the worker will tamper with.
    /// </summary>
    private async Task<(int Code, string Stdout)> RunAsync(Setup setup, string workerShell, Action? prepare = null, Action<Options>? configure = null)
    {
        return await RunAsyncGolden.RunAsync(setup.Root, workerShell + "\n" + RunAsyncGolden.SuccessStream, o =>
        {
            _dirs.Track(Directory.GetCurrentDirectory());
            prepare?.Invoke();
            configure?.Invoke(o);
        });
    }

    private static void AssertViolation(Setup setup, int code, string stdout, string expectedPath)
    {
        Assert.Equal(5, code);
        Assert.False(File.Exists(setup.Marker), "the gate must not run against tampered inputs");
        Assert.Contains("gate:     ERROR:", stdout, StringComparison.Ordinal);
        Assert.Contains(expectedPath, stdout, StringComparison.Ordinal);
    }

    private static void AssertClean(Setup setup, int code, string stdout)
    {
        Assert.True(code is 0, stdout);
        Assert.True(File.Exists(setup.Marker), "the gate should have run");
        Assert.Contains("gate:     clean after 1 round(s), findings 0", stdout, StringComparison.Ordinal);
    }

    private static void GitInit(string dir)
    {
        ProcessStartInfo psi = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add(dir);
        using Process p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    // ---- config files the dotnet tooling reads -------------------------------------------------------------

    [Theory]
    [InlineData("dotnet-tools.json")]
    [InlineData(".config/dotnet-tools.json")]
    public async Task A_worker_that_creates_a_tool_manifest_in_the_working_directory_trips_the_trust_checkAsync(string relative)
    {
        Setup setup = await NewSetupAsync();

        (int code, string stdout) = await RunAsync(setup, "mkdir -p \"$(dirname '" + relative + "')\"\nprintf '{}' > '" + relative + "'");

        AssertViolation(setup, code, stdout, "dotnet-tools.json");
    }

    [Theory]
    [InlineData("global.json")]
    [InlineData("dotnet-tools.json")]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("nuget.config")]
    public async Task A_worker_that_creates_a_config_file_between_the_working_directory_and_the_git_root_trips_the_trust_checkAsync(string relative)
    {
        Assert.SkipWhen(Launcher.FindOnPath("git") is null, "git is not on PATH");

        Setup setup = await NewSetupAsync();
        string expected = string.Empty;

        (int code, string stdout) = await RunAsync(
            setup,
            "mkdir -p \"$(dirname '../" + relative + "')\"\nprintf '{}' > '../" + relative + "'",
            () =>
            {
                string repo = Directory.GetCurrentDirectory();
                GitInit(repo);
                string work = Path.Combine(repo, "a", "b");
                _ = Directory.CreateDirectory(work);
                Directory.SetCurrentDirectory(work);
                expected = Path.Combine(repo, "a", relative);
            });

        AssertViolation(setup, code, stdout, expected);
    }

    // ---- argv[0] resolved on PATH --------------------------------------------------------------------------

    private async Task<(Setup Setup, string Exe)> NewPathToolSetupAsync()
    {
        string toolDir = _dirs.Create("lw-gate-pathtool");
        string exe = Path.Combine(toolDir, "lw-gate-path-tool");
        Setup setup = await NewSetupAsync(gate => gate["command"] = new JsonArray(["lw-gate-path-tool"]));
        await GateLoopTests.WriteExecutableAsync(exe, "#!/bin/sh\ntouch '" + setup.Marker + "'\nexit 0\n");
        return (setup, exe);
    }

    private static void PutOnPath(string exe)
    {
        string dir = Path.GetDirectoryName(exe)!;
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    [Fact]
    public async Task A_gate_executable_found_on_PATH_runs_when_nothing_changedAsync()
    {
        (Setup setup, string exe) = await NewPathToolSetupAsync();

        (int code, string stdout) = await RunAsync(setup, "true", () => PutOnPath(exe));

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_worker_that_swaps_the_gate_executable_found_on_PATH_trips_the_trust_checkAsync()
    {
        (Setup setup, string exe) = await NewPathToolSetupAsync();

        (int code, string stdout) = await RunAsync(setup, "printf '#!/bin/sh\\nexit 0\\n' > '" + exe + "'", () => PutOnPath(exe));

        AssertViolation(setup, code, stdout, exe);
    }

    // ---- gate.trust ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_worker_that_edits_a_Directory_Build_props_listed_in_gate_trust_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["Directory.Build.props"]));

        (int code, string stdout) = await RunAsync(
            setup,
            "printf '<Project><PropertyGroup><NoWarn>all</NoWarn></PropertyGroup></Project>' > Directory.Build.props",
            () => File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "Directory.Build.props"), "<Project/>"));

        AssertViolation(setup, code, stdout, "Directory.Build.props");
    }

    [Fact]
    public async Task A_worker_that_edits_a_Directory_Build_props_not_listed_in_gate_trust_still_gets_the_gate_runAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["Directory.Build.targets"]));

        (int code, string stdout) = await RunAsync(
            setup,
            "printf '<Project><PropertyGroup/></Project>' > Directory.Build.props",
            () => File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "Directory.Build.props"), "<Project/>"));

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_worker_that_creates_a_literal_gate_trust_file_that_was_absent_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray([".editorconfig"]));

        (int code, string stdout) = await RunAsync(setup, "printf 'root = true' > .editorconfig");

        AssertViolation(setup, code, stdout, ".editorconfig");
    }

    [Fact]
    public async Task A_worker_that_edits_a_file_matched_by_a_gate_trust_glob_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["src/**/*.props"]));

        (int code, string stdout) = await RunAsync(
            setup,
            "printf '<Project><PropertyGroup/></Project>' > src/deep/x.props",
            () =>
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "src", "deep");
                _ = Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "x.props"), "<Project/>");
            });

        AssertViolation(setup, code, stdout, "x.props");
    }

    [Fact]
    public async Task A_worker_that_edits_a_file_the_gate_trust_glob_does_not_match_still_gets_the_gate_runAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["src/**/*.props"]));

        (int code, string stdout) = await RunAsync(
            setup,
            "printf 'changed' > src/deep/x.txt",
            () =>
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "src", "deep");
                _ = Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "x.txt"), "text");
            });

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task An_invalid_gate_trust_entry_stops_the_launch_naming_the_entryAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["../outside.props"]));

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync(setup, "true"));

        Assert.Contains("gate.trust[0]", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(setup.Marker));
    }

    // ---- trusted files changed while the gate ran ----------------------------------------------------------

    [Fact]
    public async Task A_gate_that_rewrites_a_trusted_file_while_it_runs_ends_in_error_and_keeps_the_gates_real_exit_codeAsync()
    {
        Setup setup = await NewSetupAsync(scriptBody:
            "#!/bin/sh\nprintf '{}' > global.json\nR=\"$(dirname \"$0\")/report.json\"\n" +
            "printf '{\"runs\":[{\"results\":[1,2]}]}' > \"$R\"\necho \"sarif: $R\"\nexit 1\n");

        (int code, string stdout) = await RunAsync(setup, "true");

        Assert.Equal(5, code);
        Assert.Contains("gate:     ERROR: trusted files changed while the gate ran: ", stdout, StringComparison.Ordinal);
        Assert.Contains("global.json", stdout, StringComparison.Ordinal);

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject gate = RunAsyncGolden.Summary(runDir)["gate"]!.AsObject();
        Assert.Equal("error", gate["decision"]!.GetValue<string>());
        Assert.Equal("error", gate["outcome"]!.GetValue<string>());
        Assert.Equal(1, gate["exit_code"]!.GetValue<int>());
        Assert.Equal(2, gate["count"]!.GetValue<int>());
        Assert.StartsWith("trusted files changed while the gate ran: ", gate["error"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal("error", gateJson["outcome"]!.GetValue<string>());
        Assert.Equal(1, gateJson["exit_code"]!.GetValue<int>());
        Assert.Equal(2, gateJson["count"]!.GetValue<int>());
    }

    // No argv entry is excluded by default: a pre-existing file the gate rewrites must be declared in
    // gate.outputs; a file that did not exist before the worker ran is not a trusted input at all.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_gate_that_writes_a_file_named_in_its_argv_is_cleanAsync(bool existedBefore)
    {
        Setup setup = await NewSetupAsync(
            gate =>
            {
                gate["command"]!.AsArray().Add("report.out");
                if (!existedBefore)
                {
                    return;
                }

                gate["outputs"] = new JsonArray(["report.out"]);
            },
            "#!/bin/sh\ntouch \"$(dirname \"$0\")/gate-ran\"\nprintf '{\"runs\":[{\"results\":[]}]}' > \"$1\"\necho \"sarif: $1\"\nexit 0\n");

        (int code, string stdout) = await RunAsync(
            setup, "true", () =>
            {
                if (!existedBefore)
                {
                    return;
                }

                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "report.out"), "old");
            });

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_worker_that_edits_an_argv_file_that_existed_before_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["command"]!.AsArray().Add("report.out"));

        (int code, string stdout) = await RunAsync(
            setup, "printf 'tampered' > report.out", () => File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "report.out"), "old"));

        AssertViolation(setup, code, stdout, "report.out");
    }

    // ---- the gate's report path must not hide a trusted input ----------------------------------------------

    // A gate that exits with code 3 and names `reportExpr` (a shell expression) as its report path.
    private static string ReportScript(string reportExpr) =>
        "#!/bin/sh\ntouch \"$(dirname \"$0\")/gate-ran\"\necho \"sarif: " + reportExpr + "\"\nexit 3\n";

    private static async Task AssertReportPathViolationAsync(int code, string stdout, string expectedPath)
    {
        Assert.Equal(5, code);
        string expectedError = "the gate's report path is a trusted input: " + expectedPath;
        Assert.Contains("gate:     ERROR: " + expectedError, stdout, StringComparison.Ordinal);

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject summaryGate = RunAsyncGolden.Summary(runDir)["gate"]!.AsObject();
        Assert.Equal("error", summaryGate["decision"]!.GetValue<string>());
        Assert.Equal("error", summaryGate["outcome"]!.GetValue<string>());
        Assert.Equal(3, summaryGate["exit_code"]!.GetValue<int>());
        Assert.Equal(expectedError, summaryGate["error"]!.GetValue<string>());

        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal("error", gateJson["outcome"]!.GetValue<string>());
        Assert.Equal(3, gateJson["exit_code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("global.json")]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("literal.props")]
    public async Task A_report_path_that_is_a_trusted_file_in_the_working_directory_ends_in_errorAsync(string relative)
    {
        Setup setup = await NewSetupAsync(
            gate => gate["trust"] = relative is "literal.props" ? new JsonArray([relative]) : [],
            ReportScript(relative));
        string expected = string.Empty;

        (int code, string stdout) = await RunAsync(
            setup, "true", () =>
            {
                string cwd = Directory.GetCurrentDirectory();
                expected = Path.Combine(cwd, relative);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
                File.WriteAllText(expected, "{}");
            });

        await AssertReportPathViolationAsync(code, stdout, expected);
    }

    [Fact]
    public async Task A_report_path_that_is_the_prices_file_ends_in_errorAsync()
    {
        string prices = Path.Combine(_dirs.Create("lw-gate-prices"), "my-prices.json");
        await File.WriteAllTextAsync(prices, "{}", TestContext.Current.CancellationToken);
        Setup setup = await NewSetupAsync(scriptBody: ReportScript(prices));

        (int code, string stdout) = await RunAsync(setup, "true", configure: o => o.PricesFile = prices);

        await AssertReportPathViolationAsync(code, stdout, prices);
    }

    // ---- a gate script inside the repository, plain (`sh gate.sh`) or behind a wrapper (`env X=1 sh gate.sh`) --
    // These tests keep the script in the working directory; the scratch-directory script is covered by the
    // "outside the git root" tests below.

    private async Task<Setup> NewRepoScriptSetupAsync(bool wrapped) =>
        await NewSetupAsync(gate => gate["command"] = wrapped ? new JsonArray(["env", "X=1", "sh", "gate.sh"]) : new JsonArray(["sh", "gate.sh"]));

    // Writes the gate script into the working directory; returns its full path.
    private static string PlantRepoScript(string body)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "gate.sh");
        File.WriteAllText(path, body);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_report_path_that_is_the_gate_script_ends_in_errorAsync(bool wrapped)
    {
        Setup setup = await NewRepoScriptSetupAsync(wrapped);
        string expected = string.Empty;

        (int code, string stdout) = await RunAsync(setup, "true", () => expected = PlantRepoScript(ReportScript("$0")));

        await AssertReportPathViolationAsync(code, stdout, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_worker_that_changes_the_script_trips_the_trust_checkAsync(bool wrapped)
    {
        Setup setup = await NewRepoScriptSetupAsync(wrapped);
        string expected = string.Empty;
        string body = "#!/bin/sh\ntouch '" + setup.Marker + "'\nexit 0\n";

        (int code, string stdout) = await RunAsync(
            setup, "printf '#!/bin/sh\\nexit 0\\n' > gate.sh", () => expected = PlantRepoScript(body));

        AssertViolation(setup, code, stdout, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_gate_script_the_worker_leaves_alone_is_cleanAsync(bool wrapped)
    {
        Setup setup = await NewRepoScriptSetupAsync(wrapped);
        string body = "#!/bin/sh\ntouch '" + setup.Marker + "'\nexit 0\n";

        (int code, string stdout) = await RunAsync(setup, "true", () => PlantRepoScript(body));

        AssertClean(setup, code, stdout);
    }

    // ---- declared gate outputs -----------------------------------------------------------------------------

    private const string OutputScript =
        "#!/bin/sh\ntouch \"$(dirname \"$0\")/gate-ran\"\nmkdir -p out\n" +
        "printf '{\"runs\":[{\"results\":[]}]}' > out/report.sarif\necho 'sarif: out/report.sarif'\nexit 0\n";

    private static void WriteOldReport()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "out");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.sarif"), "old");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_gate_that_rewrites_a_declared_output_is_cleanAsync(bool inArgv, bool existedBefore)
    {
        Setup setup = await NewSetupAsync(
            gate =>
            {
                gate["outputs"] = new JsonArray(["out/report.sarif"]);
                if (!inArgv)
                {
                    return;
                }

                gate["command"]!.AsArray().Add("--output");
                gate["command"]!.AsArray().Add("out/report.sarif");
            },
            OutputScript);

        (int code, string stdout) = await RunAsync(
            setup, "true", () =>
            {
                if (!existedBefore)
                {
                    return;
                }

                WriteOldReport();
            });

        AssertClean(setup, code, stdout);

        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(["out/report.sarif"], gateJson["outputs"]!.AsArray().Select(n => n!.GetValue<string>()), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_worker_that_changes_a_declared_output_that_is_also_an_argv_entry_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(
            gate =>
            {
                gate["outputs"] = new JsonArray(["out/report.sarif"]);
                gate["command"]!.AsArray().Add("--output");
                gate["command"]!.AsArray().Add("out/report.sarif");
            },
            OutputScript);

        (int code, string stdout) = await RunAsync(setup, "printf 'tampered' > out/report.sarif", () => WriteOldReport());

        AssertViolation(setup, code, stdout, "report.sarif");
    }

    [Fact]
    public async Task Gate_json_has_no_outputs_when_none_are_declaredAsync()
    {
        Setup setup = await NewSetupAsync();

        (int code, string stdout) = await RunAsync(setup, "true");

        AssertClean(setup, code, stdout);
        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.False(gateJson.ContainsKey("outputs"));
    }

    [Fact]
    public async Task A_gate_that_rewrites_an_undeclared_argv_output_that_existed_before_ends_in_errorAsync()
    {
        // The report path points elsewhere (an untrusted file), so only the after-gate comparison can see it.
        Setup setup = await NewSetupAsync(
            gate => gate["command"]!.AsArray().Add("report.out"),
            "#!/bin/sh\ntouch \"$(dirname \"$0\")/gate-ran\"\nprintf 'new' > \"$1\"\nR=\"$(dirname \"$0\")/rep.json\"\n" +
            "printf '{\"runs\":[{\"results\":[]}]}' > \"$R\"\necho \"sarif: $R\"\nexit 0\n");

        (int code, string stdout) = await RunAsync(
            setup, "true", () => File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "report.out"), "old"));

        Assert.Equal(5, code);
        Assert.Contains("gate:     ERROR: trusted files changed while the gate ran: ", stdout, StringComparison.Ordinal);
        Assert.Contains("report.out", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_path_under_the_run_directory_is_cleanAsync()
    {
        Setup setup = await NewSetupAsync();
        await GateLoopTests.WriteExecutableAsync(
            setup.Script,
            "#!/bin/sh\ntouch '" + setup.Marker + "'\nfor d in '" + setup.Root + "'/runs/*/; do D=\"${d%/}\"; done\n" +
            "printf '{\"runs\":[{\"results\":[]}]}' > \"$D/gate-report.json\"\necho \"sarif: $D/gate-report.json\"\nexit 0\n");

        (int code, string stdout) = await RunAsync(setup, "true");

        AssertClean(setup, code, stdout);
    }

    // A gate that rewrites its declared output (argv[2], also named in gate.outputs) on every round: round 1
    // finds 3, round 2 finds none. The report is the output itself.
    private const string RewritingGateScript =
        "#!/bin/sh\nD=\"$(dirname \"$0\")\"\ntouch \"$D/gate-ran\"\nN=$(cat \"$D/n\" 2>/dev/null || echo 0); N=$((N+1)); echo $N > \"$D/n\"\n" +
        "mkdir -p out\nif [ $N = 1 ]; then printf '{\"runs\":[{\"results\":[1,2,3]}]}' > \"$1\"; echo \"sarif: $1\"; exit 1; fi\n" +
        "printf '{\"runs\":[{\"results\":[]}]}' > \"$1\"\necho \"sarif: $1\"\nexit 0\n";

    private async Task<(Setup Setup, int Code, string Stdout)> RunRewritingGateAsync(string workerShell)
    {
        Setup setup = await NewSetupAsync(
            gate =>
            {
                gate["outputs"] = new JsonArray(["out/report.sarif"]);
                gate["command"]!.AsArray().Add("out/report.sarif");
            },
            RewritingGateScript);
        string counter = Path.Combine(Path.GetDirectoryName(setup.Script)!, "w");
        string worker = "N=$(cat '" + counter + "' 2>/dev/null || echo 0); N=$((N+1)); echo $N > '" + counter + "'\n" + workerShell;

        (int code, string stdout) = await RunAsync(setup, worker, () => WriteOldReport());
        return (setup, code, stdout);
    }

    [Fact]
    public async Task A_pre_existing_declared_argv_output_the_worker_leaves_alone_survives_into_round_twoAsync()
    {
        (Setup setup, int code, string stdout) = await RunRewritingGateAsync("true");

        Assert.True(code is 0, stdout);
        Assert.True(File.Exists(setup.Marker), "the gate should have run");
        Assert.Contains("gate:     clean after 2 round(s), findings 3→0", stdout, StringComparison.Ordinal);
        Assert.Equal(2, GateLoopTests.RunDirNames(setup.Root).Length);
    }

    [Fact]
    public async Task A_worker_that_edits_a_declared_argv_output_in_round_two_trips_the_trust_checkAsync()
    {
        (Setup setup, int code, string stdout) = await RunRewritingGateAsync("if [ $N = 2 ]; then printf 'tampered' > out/report.sarif; fi");

        Assert.Equal(5, code);
        Assert.Contains("gate:     ERROR:", stdout, StringComparison.Ordinal);
        Assert.Contains("report.sarif", stdout, StringComparison.Ordinal);
        string[] names = GateLoopTests.RunDirNames(setup.Root);
        Assert.Equal(2, names.Length);
        JsonObject gate = RunAsyncGolden.Summary(Path.Combine(setup.Root, "runs", names[1]))["gate"]!.AsObject();
        Assert.Equal(2, gate["round"]!.GetValue<int>());
        Assert.Equal("error", gate["decision"]!.GetValue<string>());
    }

    // ---- an argv file outside the git root is trusted like argv[0] ------------------------------------------

    [Fact]
    public async Task A_worker_that_edits_a_gate_script_outside_the_git_root_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync();
        (int code, string stdout) = await RunAsync(setup, "printf '#!/bin/sh\\nexit 0\\n' > '" + setup.Script + "'");

        AssertViolation(setup, code, stdout, setup.Script);
    }

    [Fact]
    public async Task A_gate_script_outside_the_git_root_that_the_worker_leaves_alone_is_cleanAsync()
    {
        Setup setup = await NewSetupAsync();

        (int code, string stdout) = await RunAsync(setup, "true");

        AssertClean(setup, code, stdout);
    }

    // ---- a report path is normalised before the run-directory and trusted-source checks ------------------------

    [Fact]
    public async Task A_report_path_that_reaches_a_trusted_file_through_dotdot_ends_in_errorAsync()
    {
        Setup setup = await NewSetupAsync();
        await GateLoopTests.WriteExecutableAsync(
            setup.Script,
            "#!/bin/sh\ntouch '" + setup.Marker + "'\nfor d in '" + setup.Root + "'/runs/*/; do D=\"${d%/}\"; done\n" +
            "echo \"sarif: $D/../../profiles.json\"\nexit 3\n");

        (int code, string stdout) = await RunAsync(setup, "true");

        await AssertReportPathViolationAsync(code, stdout, Path.GetFullPath(Path.Combine(setup.Root, "profiles.json")));
    }

    // ---- gate.trust literals ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_gate_trust_literal_that_names_a_directory_stops_the_launch_naming_the_entryAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["other.props", "tools"]));
        string workerMarker = Path.Combine(Path.GetDirectoryName(setup.Marker)!, "worker-ran");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync(
            setup, "touch '" + workerMarker + "'", () => Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "tools"))));

        Assert.Contains("gate.trust[1]", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(workerMarker), "the worker must not run");
        Assert.False(File.Exists(setup.Marker), "the gate must not run");
    }

    // ---- gate.outputs validated against the trusted inputs -------------------------------------------------

    private static readonly string[] _runsRootFiles = ["profiles.json", "project.md", "task.md"];

    // Creates the trusted file a declared output collides with; the "runsroot" case moves the runs root
    // under the working directory (rr/) so a repo-relative path can name one of its files.
    private static void PlantTrustedFile(string kind, string path, string originalRoot)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, kind is "argv0" ? "#!/bin/sh\nexit 0\n" : "{}");
        if (kind is "argv0" && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (kind is not "runsroot")
        {
            return;
        }

        foreach (string name in _runsRootFiles)
        {
            File.Copy(Path.Combine(originalRoot, name), Path.Combine(Directory.GetCurrentDirectory(), "rr", name), overwrite: true);
        }
    }

    private static void PointOptionsAtTrustedFile(string kind, Options o, string output)
    {
        string cwd = Directory.GetCurrentDirectory();
        if (kind is "prices")
        {
            o.PricesFile = Path.Combine(cwd, output);
        }

        if (kind is not "runsroot")
        {
            return;
        }

        o.RunsRoot = Path.Combine(cwd, "rr");
        o.TaskFile = Path.Combine(cwd, "rr", "task.md");
    }

    [Theory]
    [InlineData("argv0")]
    [InlineData("trust")]
    [InlineData("prices")]
    [InlineData("global")]
    [InlineData("runsroot")]
    public async Task A_declared_output_that_is_a_trusted_input_is_a_launch_error_before_the_worker_runsAsync(string kind)
    {
        string output = kind switch
        {
            "argv0" => "tool.sh",
            "trust" => "literal.props",
            "prices" => "prices.json",
            "global" => "global.json",
            _ => "rr/project.md",
        };
        Setup setup = await NewSetupAsync(gate =>
        {
            gate["outputs"] = new JsonArray(["other.out", output]);
            gate["command"] = kind is "argv0" ? new JsonArray(["./tool.sh"]) : gate["command"]!.DeepClone();
            gate["trust"] = kind is "trust" ? new JsonArray([output]) : [];
        });
        string workerMarker = Path.Combine(Path.GetDirectoryName(setup.Marker)!, "worker-ran");
        string expected = string.Empty;

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync(
            setup,
            "touch '" + workerMarker + "'",
            () =>
            {
                expected = Path.Combine(Directory.GetCurrentDirectory(), output);
                PlantTrustedFile(kind, expected, setup.Root);
            },
            o => PointOptionsAtTrustedFile(kind, o, output)));

        Assert.Contains("gate.outputs[1] '" + expected + "' is a trusted gate input", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(workerMarker), "the worker must not run");
        Assert.False(File.Exists(setup.Marker), "the gate must not run");
    }

    [Fact]
    public async Task A_malformed_gate_outputs_entry_stops_the_launch_naming_the_entryAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["outputs"] = new JsonArray(["../outside.sarif"]));

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync(setup, "true"));

        Assert.Contains("gate.outputs[0]", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(setup.Marker));
    }

    // ---- generated files and globs -------------------------------------------------------------------------

    [Fact]
    public async Task A_worker_that_generates_a_file_matched_by_a_gate_trust_glob_does_not_trip_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["**/*.props"]));

        (int code, string stdout) = await RunAsync(setup, "mkdir -p obj\nprintf '<Project/>' > obj/x.nuget.g.props");

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_gate_that_generates_a_file_matched_by_a_gate_trust_glob_is_cleanAsync()
    {
        Setup setup = await NewSetupAsync(
            gate => gate["trust"] = new JsonArray(["**/*.props"]),
            "#!/bin/sh\ntouch \"$(dirname \"$0\")/gate-ran\"\nmkdir -p obj\nprintf '<Project/>' > obj/x.nuget.g.props\nexit 0\n");

        (int code, string stdout) = await RunAsync(setup, "true");

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_worker_that_edits_a_pre_existing_file_matched_by_a_recursive_glob_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["**/*.props"]));

        (int code, string stdout) = await RunAsync(
            setup,
            "printf '<Project><PropertyGroup/></Project>' > Directory.Build.props",
            () => File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "Directory.Build.props"), "<Project/>"));

        AssertViolation(setup, code, stdout, "Directory.Build.props");
    }

    // ---- chains of symlinks and non-regular trusted paths ------------------------------------------------

    [Fact]
    public async Task A_gate_executable_behind_a_two_link_chain_runs_cleanAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        Setup setup = await NewSetupAsync();
        string dir = Path.GetDirectoryName(setup.Script)!;
        string link1 = Path.Combine(dir, "gate-link1");
        string link2 = Path.Combine(dir, "gate-link2");
        _ = File.CreateSymbolicLink(link1, setup.Script);
        _ = File.CreateSymbolicLink(link2, link1);
        RunAsyncGolden.WriteProfile(setup.Root, "test", new JsonObject { ["gate"] = new JsonObject { ["command"] = new JsonArray([link2]) } });

        (int code, string stdout) = await RunAsync(setup, "true");

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_trusted_file_behind_a_two_link_chain_is_clean_until_the_worker_edits_its_targetAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        static void Prepare()
        {
            string cwd = Directory.GetCurrentDirectory();
            File.WriteAllText(Path.Combine(cwd, "real.cfg"), "one");
            _ = File.CreateSymbolicLink(Path.Combine(cwd, "l1.cfg"), "real.cfg");
            _ = File.CreateSymbolicLink(Path.Combine(cwd, "tools.cfg"), "l1.cfg");
        }

        Setup clean = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["tools.cfg"]));
        (int cleanCode, string cleanStdout) = await RunAsync(clean, "true", () => Prepare());
        AssertClean(clean, cleanCode, cleanStdout);

        Setup edited = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["tools.cfg"]));
        (int editedCode, string editedStdout) = await RunAsync(edited, "printf 'two' > real.cfg", () => Prepare());
        AssertViolation(edited, editedCode, editedStdout, "tools.cfg");
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("dangling")]
    public async Task A_pre_existing_non_regular_trusted_path_ends_in_error_naming_the_pathAsync(string kind)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        Setup setup = await NewSetupAsync();
        string expected = string.Empty;

        (int code, string stdout) = await RunAsync(setup, "true", () =>
        {
            expected = Path.Combine(Directory.GetCurrentDirectory(), "global.json");
            if (kind is "directory")
            {
                _ = Directory.CreateDirectory(expected);
            }
            else
            {
                _ = File.CreateSymbolicLink(expected, Path.Combine(Directory.GetCurrentDirectory(), "lw-missing-target"));
            }
        });

        Assert.Equal(5, code);
        Assert.False(File.Exists(setup.Marker), "the gate must not run against a non-regular trusted path");
        Assert.Contains("trusted path '" + expected + "' is not a regular file", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("the worker changed files", stdout, StringComparison.Ordinal);
    }

    // ---- tampering between rounds ----------------------------------------------------------------------------

    [Theory]
    [InlineData("global.json")]
    [InlineData("profiles.json")]
    public async Task A_process_that_tampers_between_round_one_and_round_two_ends_round_two_in_errorAsync(string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");

        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-between");
        string script = await GateLoopTests.WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (0, 0));
        RunAsyncGolden.WriteProfile(root, "test", GateLoopTests.GateProfile(script));
        string tamper = target is "global.json"
            ? "printf '{}' > global.json"
            : "printf ' ' >> '" + Path.Combine(root, "profiles.json") + "'";
        string counter = Path.Combine(scratch, "w");
        string tampered = Path.Combine(scratch, "tampered");

        // Round 1's worker leaves a background process that waits until round 1 is recorded, then
        // tampers and does not restore. Round 2's worker waits for it, so the change is on disk before
        // round 2's gate check no matter how the launcher is scheduled.
        string worker =
            "N=$(cat '" + counter + "' 2>/dev/null || echo 0); N=$((N+1)); echo $N > '" + counter + "'\n" +
            "if [ $N = 1 ]; then\n" +
            "  ( i=0; while [ $i -lt 400 ]; do\n" +
            "      if ls '" + root + "'/runs/*/summary.json >/dev/null 2>&1; then " + tamper + "; touch '" + tampered + "'; exit 0; fi\n" +
            "      sleep 0.05; i=$((i+1)); done ) >/dev/null 2>&1 </dev/null &\n" +
            "else\n" +
            "  i=0; while [ ! -e '" + tampered + "' ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done\n" +
            "fi\n" + RunAsyncGolden.SuccessStream;

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, worker, _ => _dirs.Track(Directory.GetCurrentDirectory()));

        Assert.True(File.Exists(tampered), "the background process never tampered");
        Assert.Equal(5, code);
        Assert.Equal("1", (await File.ReadAllTextAsync(Path.Combine(scratch, "n"), TestContext.Current.CancellationToken)).Trim());
        string[] names = GateLoopTests.RunDirNames(root);
        Assert.Equal(2, names.Length);
        JsonObject gate = RunAsyncGolden.Summary(Path.Combine(root, "runs", names[1]))["gate"]!.AsObject();
        Assert.Equal(2, gate["round"]!.GetValue<int>());
        Assert.Equal("error", gate["decision"]!.GetValue<string>());
        Assert.Contains(target, gate["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("gate:     ERROR:", stdout, StringComparison.Ordinal);
    }

    // ---- unreadable directories and FIFOs -------------------------------------------------------------------

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_directory_under_the_runs_root_does_not_fail_the_launchAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs a non-root POSIX user");

        Setup setup = await NewSetupAsync();
        string locked = Path.Combine(setup.Root, "locked");
        _ = Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            (int code, string stdout) = await RunAsync(setup, "true");

            Assert.True(code is 0, stdout);
            Assert.True(File.Exists(setup.Marker), "the gate should have run");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_directory_under_the_git_root_does_not_fail_the_launchAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs a non-root POSIX user");
        Assert.SkipWhen(Launcher.FindOnPath("git") is null, "git is not on PATH");

        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["**/*.props"]));
        string locked = string.Empty;
        try
        {
            (int code, string stdout) = await RunAsync(setup, "true", () =>
            {
                string repo = Directory.GetCurrentDirectory();
                GitInit(repo);
                locked = Path.Combine(repo, "locked");
                _ = Directory.CreateDirectory(locked);
                File.SetUnixFileMode(locked, UnixFileMode.None);
            });

            Assert.True(code is 0, stdout);
            Assert.True(File.Exists(setup.Marker), "the gate should have run");
        }
        finally
        {
            if (locked.Length > 0 && Directory.Exists(locked))
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task Several_fifos_a_worker_plants_under_the_runs_root_do_not_cost_a_timeout_eachAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Unix only");

        Setup setup = await NewSetupAsync();
        string r = setup.Root;
        Stopwatch watch = Stopwatch.StartNew();

        Task<(int Code, string Stdout)> run = RunAsync(
            setup, "mkfifo '" + r + "/f1' '" + r + "/f2' '" + r + "/f3' '" + r + "/f4' '" + r + "/f5'");
        (int code, string stdout) = await run.WaitAsync(TimeSpan.FromSeconds(60), TimeProvider.System, TestContext.Current.CancellationToken);
        watch.Stop();

        AssertViolation(setup, code, stdout, "/f1");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), "the trust check took " + watch.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
    }

    [Fact]
    public async Task A_gate_that_leaves_the_trusted_files_alone_is_not_flagged_by_the_third_hashAsync()
    {
        Setup setup = await NewSetupAsync(scriptBody: "#!/bin/sh\nprintf 'scratch' > not-trusted.txt\nexit 0\n");

        (int code, string stdout) = await RunAsync(setup, "true");

        Assert.Equal(0, code);
        Assert.Contains("gate:     clean after 1 round(s), findings 0", stdout, StringComparison.Ordinal);
    }

    // ---- snapshot keys by trusted path ---------------------------------------------------------------------

    [Fact]
    public async Task A_worker_that_plants_a_dangling_link_at_a_trusted_path_is_reported_under_the_link_pathAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        Setup setup = await NewSetupAsync();

        (int code, string stdout) = await RunAsync(setup, "ln -s \"$PWD/lw-missing-target\" global.json");

        AssertViolation(setup, code, stdout, "global.json");
        Assert.DoesNotContain("lw-missing-target", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_that_swaps_a_trusted_file_for_a_link_to_identical_content_trips_the_trust_checkAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        Setup setup = await NewSetupAsync();

        (int code, string stdout) = await RunAsync(
            setup,
            "rm global.json\nln -s other.json global.json",
            () =>
            {
                string cwd = Directory.GetCurrentDirectory();
                File.WriteAllText(Path.Combine(cwd, "global.json"), "{}");
                File.WriteAllText(Path.Combine(cwd, "other.json"), "{}");
            });

        AssertViolation(setup, code, stdout, "global.json");
    }

    // ---- runs root and prices file -------------------------------------------------------------------------

    [Fact]
    public async Task Files_the_launcher_owns_in_the_runs_root_never_count_as_tamperingAsync()
    {
        Setup setup = await NewSetupAsync();
        string r = setup.Root;

        (int code, string stdout) = await RunAsync(
            setup,
            "echo '{}' >> '" + r + "/runs.jsonl'\n" +
            "mkdir -p '" + r + "/system' '" + r + "/inbox/t' '" + r + "/runs/other'\n" +
            "echo x > '" + r + "/system/zz.md'\n" +
            "echo y > '" + r + "/inbox/t/f'\n" +
            "echo z > '" + r + "/runs/other/summary.json'");

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_worker_that_edits_a_file_in_a_runs_root_subdirectory_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync();
        string nested = Path.Combine(setup.Root, "sub", "deep");
        _ = Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "notes.txt"), "before", TestContext.Current.CancellationToken);

        (int code, string stdout) = await RunAsync(setup, "printf 'after' > '" + Path.Combine(nested, "notes.txt") + "'");

        AssertViolation(setup, code, stdout, "notes.txt");
    }

    [Fact]
    public async Task A_prices_file_outside_the_runs_root_that_the_worker_changes_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync();
        string prices = Path.Combine(_dirs.Create("lw-gate-prices"), "my-prices.json");
        await File.WriteAllTextAsync(prices, "{}", TestContext.Current.CancellationToken);

        (int code, string stdout) = await RunAsync(setup, "printf '{ }' > '" + prices + "'", configure: o => o.PricesFile = prices);

        AssertViolation(setup, code, stdout, prices);
    }

    [Fact]
    public async Task A_prices_file_outside_the_runs_root_that_stays_the_same_lets_the_gate_runAsync()
    {
        Setup setup = await NewSetupAsync();
        string prices = Path.Combine(_dirs.Create("lw-gate-prices"), "my-prices.json");
        await File.WriteAllTextAsync(prices, "{}", TestContext.Current.CancellationToken);

        (int code, string stdout) = await RunAsync(setup, "true", configure: o => o.PricesFile = prices);

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_prices_file_under_the_runs_root_inbox_that_the_worker_changes_trips_the_trust_checkAsync()
    {
        Setup setup = await NewSetupAsync();
        string inbox = Path.Combine(setup.Root, "inbox", "t");
        _ = Directory.CreateDirectory(inbox);
        string prices = Path.Combine(inbox, "my-prices.json");
        await File.WriteAllTextAsync(prices, "{}", TestContext.Current.CancellationToken);

        (int code, string stdout) = await RunAsync(setup, "printf '{ }' > '" + prices + "'", configure: o => o.PricesFile = prices);

        AssertViolation(setup, code, stdout, prices);
    }

    [Fact]
    public async Task A_prices_file_under_the_runs_root_inbox_that_stays_the_same_lets_the_gate_runAsync()
    {
        Setup setup = await NewSetupAsync();
        string inbox = Path.Combine(setup.Root, "inbox", "t");
        _ = Directory.CreateDirectory(inbox);
        string prices = Path.Combine(inbox, "my-prices.json");
        await File.WriteAllTextAsync(prices, "{}", TestContext.Current.CancellationToken);

        (int code, string stdout) = await RunAsync(setup, "true", configure: o => o.PricesFile = prices);

        AssertClean(setup, code, stdout);
    }

    // ---- symlinked files, symlink loops, nested .git, unmatched globs ---------------------------------------

    [Theory]
    [InlineData("same")]
    [InlineData("different")]
    public async Task A_worker_that_replaces_a_gate_trust_glob_match_with_a_symlink_trips_the_trust_checkAsync(string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["*.cfg"]));
        string content = target is "same" ? "one" : "two";

        (int code, string stdout) = await RunAsync(
            setup,
            "printf '" + content + "' > target.txt\nrm tools.cfg\nln -s target.txt tools.cfg",
            () => File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "tools.cfg"), "one"));

        AssertViolation(setup, code, stdout, "tools.cfg");
    }

    [Theory]
    [InlineData("same")]
    [InlineData("different")]
    public async Task A_worker_that_replaces_a_runs_root_file_with_a_symlink_trips_the_trust_checkAsync(string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        Setup setup = await NewSetupAsync();
        string project = Path.Combine(setup.Root, "project.md");
        await File.WriteAllTextAsync(project, "one", TestContext.Current.CancellationToken);
        string content = target is "same" ? "one" : "two";
        string elsewhere = Path.Combine(_dirs.Create("lw-gate-link-target"), "target.md");

        (int code, string stdout) = await RunAsync(
            setup, "printf '" + content + "' > '" + elsewhere + "'\nrm '" + project + "'\nln -s '" + elsewhere + "' '" + project + "'");

        AssertViolation(setup, code, stdout, "project.md");
    }

    [Fact]
    public async Task A_symlinked_directory_that_loops_back_does_not_fail_the_git_root_walkAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");
        Assert.SkipWhen(Launcher.FindOnPath("git") is null, "git is not on PATH");

        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["**/*.props"]));

        (int code, string stdout) = await RunAsync(setup, "true", () =>
        {
            string repo = Directory.GetCurrentDirectory();
            GitInit(repo);
            File.WriteAllText(Path.Combine(repo, "a.props"), "<Project/>");
            _ = File.CreateSymbolicLink(Path.Combine(repo, "loop"), "..");
        });

        AssertClean(setup, code, stdout);
    }

    [Fact]
    public async Task A_trusted_glob_match_inside_a_nested_dot_git_directory_is_protectedAsync()
    {
        Assert.SkipWhen(Launcher.FindOnPath("git") is null, "git is not on PATH");

        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["**/*.props"]));

        (int code, string stdout) = await RunAsync(
            setup,
            "printf '<Project><PropertyGroup/></Project>' > vendor/.git/inner.props",
            () =>
            {
                string repo = Directory.GetCurrentDirectory();
                GitInit(repo);
                _ = Directory.CreateDirectory(Path.Combine(repo, "vendor", ".git"));
                File.WriteAllText(Path.Combine(repo, "vendor", ".git", "inner.props"), "<Project/>");
            });

        AssertViolation(setup, code, stdout, "inner.props");
    }

    [Fact]
    public async Task A_gate_trust_glob_that_matches_no_files_is_a_warning_not_an_errorAsync()
    {
        Setup setup = await NewSetupAsync(gate => gate["trust"] = new JsonArray(["**/*.lw-no-such-extension"]));
        const string Message = "gate.trust[0] '**/*.lw-no-such-extension' matched no files";

        (int code, string stdout) = await RunAsync(setup, "true");

        AssertClean(setup, code, stdout);
        Assert.Contains("note:", stdout, StringComparison.Ordinal);
        Assert.Contains(Message, stdout, StringComparison.Ordinal);
        string runDir = Path.Combine(setup.Root, "runs", GateLoopTests.RunDirNames(setup.Root)[0]);
        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        JsonArray warnings = gateJson["warnings"]!.AsArray();
        Assert.Contains(warnings, w => w!.GetValue<string>() == Message);
    }

    // ---- round bookkeeping ---------------------------------------------------------------------------------

    [Fact]
    public async Task Every_round_summary_carries_the_same_chain_id_and_its_round_numberAsync()
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-rounds");
        string script = await GateLoopTests.WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (0, 0));
        RunAsyncGolden.WriteProfile(root, "test", GateLoopTests.GateProfile(script));

        (int code, string _) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream, _ => _dirs.Track(Directory.GetCurrentDirectory()));

        Assert.Equal(0, code);
        string[] names = GateLoopTests.RunDirNames(root);
        Assert.Equal(3, names.Length);
        string[] decisions = ["continue", "continue", "clean"];
        string? chainId = null;
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            Assert.EndsWith(i is 0 ? "-golden" : "-golden-gate" + (i + 1).ToString(CultureInfo.InvariantCulture), name, StringComparison.Ordinal);
            JsonObject gate = RunAsyncGolden.Summary(Path.Combine(root, "runs", name))["gate"]!.AsObject();
            Assert.Equal(i + 1, gate["round"]!.GetValue<int>());
            Assert.Equal(decisions[i], gate["decision"]!.GetValue<string>());
            string id = gate["chain_id"]!.GetValue<string>();
            Assert.NotEmpty(id);
            chainId ??= id;
            Assert.Equal(chainId, id);
        }
    }

    [Fact]
    public async Task The_cost_cap_ends_the_chain_stuck_end_to_endAsync()
    {
        string root = _dirs.NewRoot();
        string scratch = _dirs.Create("lw-gate-cap");
        // Findings fall 3 -> 2 -> 1, so only the cap can stop it. The worker's cost is metered from token
        // usage (list price $0.0022 for this stream), not from the reported total_cost_usd.
        string script = await GateLoopTests.WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (1, 1));
        JsonObject profile = GateLoopTests.GateProfile(script);
        profile["gate"]!["maxTotalUsd"] = 0.003m;
        RunAsyncGolden.WriteProfile(root, "test", profile);

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, MeteredSuccessStream, _ => _dirs.Track(Directory.GetCurrentDirectory()));

        Assert.Equal(4, code);
        Assert.Equal(2, GateLoopTests.RunDirNames(root).Length);
        Assert.Contains("STUCK", stdout, StringComparison.Ordinal);
        Assert.Contains("cost cap $0.003", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("max rounds", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("no decrease", stdout, StringComparison.Ordinal);
    }
}
