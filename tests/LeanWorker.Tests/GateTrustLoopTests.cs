using System.Diagnostics;
using System.Globalization;
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
        Assert.Equal(0, code);
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
    public async Task A_gate_that_rewrites_a_trusted_file_while_it_runs_ends_in_error_with_exit_code_minus_oneAsync()
    {
        Setup setup = await NewSetupAsync(scriptBody: "#!/bin/sh\nprintf '{}' > global.json\nexit 0\n");

        (int code, string stdout) = await RunAsync(setup, "true");

        Assert.Equal(5, code);
        Assert.Contains("gate:     ERROR: trusted files changed while the gate ran: ", stdout, StringComparison.Ordinal);
        Assert.Contains("global.json", stdout, StringComparison.Ordinal);

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject gate = RunAsyncGolden.Summary(runDir)["gate"]!.AsObject();
        Assert.Equal("error", gate["decision"]!.GetValue<string>());
        Assert.Equal("error", gate["outcome"]!.GetValue<string>());
        Assert.Equal(-1, gate["exit_code"]!.GetValue<int>());
        Assert.StartsWith("trusted files changed while the gate ran: ", gate["error"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(-1, gateJson["exit_code"]!.GetValue<int>());
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
