using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Runs Gate.RunAsync against real sh scripts. Touches the current directory (relative report paths resolve
/// against it) and the process environment (the allowlist tests), so these run without parallelism.
/// </summary>
[Collection("launcher-process-state")]
public partial class GateRunnerTests
{
    [GeneratedRegex(@"^sarif: (?<report>.+)$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DefaultRegex();

    private static GateSpec Spec(string script, string countPath = "runs[0].results", int feedbackMaxChars = 8000,
        int timeoutMinutes = 30, int maxRounds = 5, decimal? maxTotalUsd = 10m, List<string>? env = null, Regex? reportRegex = null) =>
        new(["sh", script], reportRegex ?? DefaultRegex(), countPath, feedbackMaxChars, timeoutMinutes, maxRounds, maxTotalUsd, env ?? []);

    private static void WriteFile(string path, string content) => File.WriteAllText(path, content);

    private static string WriteScript(string dir, string body)
    {
        string path = Path.Combine(dir, "gate.sh");
        WriteFile(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static string NewDir() => Directory.CreateTempSubdirectory("lw-gate").FullName;

    private static async Task<Gate.GateResult> RunInAsync(string cwd, GateSpec spec, string runDir)
    {
        string oldCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(cwd);
            return await Gate.RunAsync(spec, runDir, CancellationToken.None);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
        }
    }

    private static void WriteReport(string path, int count)
    {
        JsonArray results = new([.. Enumerable.Range(0, count).Select(i => (JsonNode)i)]);
        JsonObject doc = new() { ["runs"] = new JsonArray { new JsonObject { ["results"] = results } } };
        WriteFile(path, doc.ToJsonString());
    }

    [Fact]
    public async Task Exit_0_with_no_report_is_clean_with_count_zeroAsync()
    {
        string dir = NewDir();
        string script = WriteScript(dir, "exit 0\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("clean", r.Outcome);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public async Task Exit_0_with_a_report_counts_its_resultsAsync()
    {
        string dir = NewDir();
        WriteReport(Path.Combine(dir, "report.json"), 2);
        string script = WriteScript(dir, "echo 'sarif: report.json'\nexit 0\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("clean", r.Outcome);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public async Task Exit_1_with_findings_reports_the_count_from_the_last_sarif_lineAsync()
    {
        string dir = NewDir();
        WriteReport(Path.Combine(dir, "wrong.json"), 99);
        WriteReport(Path.Combine(dir, "report.json"), 3);
        string script = WriteScript(dir, "echo 'sarif: wrong.json'\necho 'sarif: report.json'\nexit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("findings", r.Outcome);
        Assert.Equal(3, r.Count);
        Assert.Equal(Path.Combine(dir, "report.json"), r.ReportPath);
    }

    [Fact]
    public async Task Relative_report_path_resolves_against_the_working_directoryAsync()
    {
        string dir = NewDir();
        WriteReport(Path.Combine(dir, "report.json"), 1);
        string script = WriteScript(dir, "echo 'sarif: report.json'\nexit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal(Path.Combine(dir, "report.json"), r.ReportPath);
    }

    [Fact]
    public async Task Exit_1_with_no_report_line_is_an_errorAsync()
    {
        string dir = NewDir();
        string script = WriteScript(dir, "exit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("error", r.Outcome);
        Assert.Contains("no non-empty stdout line", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_1_with_a_missing_report_file_is_an_errorAsync()
    {
        string dir = NewDir();
        string script = WriteScript(dir, "echo 'sarif: missing.json'\nexit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("error", r.Outcome);
        Assert.Contains("does not exist", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_1_with_invalid_json_is_an_errorAsync()
    {
        string dir = NewDir();
        WriteFile(Path.Combine(dir, "report.json"), "{not json");
        string script = WriteScript(dir, "echo 'sarif: report.json'\nexit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("error", r.Outcome);
        Assert.Contains("is not valid JSON", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_1_with_a_non_array_count_path_is_an_errorAsync()
    {
        string dir = NewDir();
        WriteFile(Path.Combine(dir, "report.json"), """{"runs":[{"results":"not an array"}]}""");
        string script = WriteScript(dir, "echo 'sarif: report.json'\nexit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("error", r.Outcome);
        Assert.Contains("is not an array", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_1_with_zero_results_is_an_errorAsync()
    {
        string dir = NewDir();
        WriteReport(Path.Combine(dir, "report.json"), 0);
        string script = WriteScript(dir, "echo 'sarif: report.json'\nexit 1\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("error", r.Outcome);
        Assert.Contains("runs[0].results", r.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Unexpected_exit_codes_are_errorsAsync(int exitCode)
    {
        string dir = NewDir();
        string script = WriteScript(dir, $"exit {exitCode.ToString(CultureInfo.InvariantCulture)}\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script), NewDir());
        Assert.Equal("error", r.Outcome);
        Assert.Equal($"gate exited {exitCode.ToString(CultureInfo.InvariantCulture)}", r.Error);
    }

    [Fact]
    public async Task A_non_existent_executable_is_an_error_not_an_exceptionAsync()
    {
        GateSpec spec = new(["lw-gate-does-not-exist-xyz"], DefaultRegex(), "runs[0].results", 8000, 30, 5, 10m, []);
        Gate.GateResult r = await Gate.RunAsync(spec, NewDir(), CancellationToken.None);
        Assert.Equal("error", r.Outcome);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Large_stdout_and_stderr_do_not_deadlock_and_are_both_loggedAsync()
    {
        string dir = NewDir();
        string outFile = Path.Combine(dir, "big_out.txt");
        string errFile = Path.Combine(dir, "big_err.txt");
        WriteFile(outFile, string.Join('\n', Enumerable.Repeat(new string('o', 90), 25_000)));
        WriteFile(errFile, string.Join('\n', Enumerable.Repeat(new string('e', 90), 25_000)));
        string script = WriteScript(dir, $"cat '{outFile}'\ncat '{errFile}' 1>&2\nexit 0\n");
        string runDir = NewDir();

        Gate.GateResult r = await RunInAsync(dir, Spec(script), runDir);

        Assert.Equal("clean", r.Outcome);
        string log = await File.ReadAllTextAsync(Path.Combine(runDir, "gate.log"), TestContext.Current.CancellationToken);
        Assert.Contains(new string('o', 90), log, StringComparison.Ordinal);
        Assert.Contains("[stderr] " + new string('e', 90), log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feedback_is_capped_and_marks_truncation_without_stderrAsync()
    {
        string dir = NewDir();
        string script = WriteScript(dir, "printf 'a%.0s' $(seq 1 500)\necho\necho 'oops' 1>&2\nexit 0\n");
        Gate.GateResult r = await RunInAsync(dir, Spec(script, feedbackMaxChars: 50), NewDir());

        Assert.True(r.Feedback.Length > 50);
        Assert.Contains("[truncated; full gate output:", r.Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("oops", r.Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("[stderr]", r.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_json_has_the_documented_fieldsAsync()
    {
        string dir = NewDir();
        WriteReport(Path.Combine(dir, "report.json"), 2);
        string script = WriteScript(dir, "echo 'sarif: report.json'\nexit 1\n");
        string runDir = NewDir();

        _ = await RunInAsync(dir, Spec(script), runDir);

        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal("findings", gateJson["outcome"]!.GetValue<string>());
        Assert.Equal(1, gateJson["exit_code"]!.GetValue<int>());
        Assert.Equal(2, gateJson["count"]!.GetValue<int>());
        Assert.Equal(Path.Combine(dir, "report.json"), gateJson["report_path"]!.GetValue<string>());
        Assert.Null(gateJson["error"]);
        Assert.True(gateJson["duration_ms"]!.GetValue<long>() >= 0);
        Assert.Equal(["sh", script], gateJson["command"]!.AsArray().Select(n => n!.GetValue<string>()), StringComparer.Ordinal);
        _ = gateJson["env_names"]!.AsArray();
    }

    [Fact]
    public async Task Default_environment_hides_secrets_but_keeps_the_allowlistAsync()
    {
        string? oldSecret = Environment.GetEnvironmentVariable("LW_TEST_SECRET");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string? oldDotnetX = Environment.GetEnvironmentVariable("DOTNET_X");
        string? oldCreds = Environment.GetEnvironmentVariable("NuGetPackageSourceCredentials_github");
        try
        {
            Environment.SetEnvironmentVariable("LW_TEST_SECRET", "x");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-secret");
            Environment.SetEnvironmentVariable("DOTNET_X", "1");
            Environment.SetEnvironmentVariable("NuGetPackageSourceCredentials_github", "cred");

            string dir = NewDir();
            string script = WriteScript(dir, "env\nexit 0\n");
            string runDir = NewDir();
            Gate.GateResult r = await RunInAsync(dir, Spec(script), runDir);

            Assert.DoesNotContain("LW_TEST_SECRET=", r.Feedback, StringComparison.Ordinal);
            Assert.DoesNotContain("ANTHROPIC_API_KEY=", r.Feedback, StringComparison.Ordinal);
            Assert.Contains("PATH=", r.Feedback, StringComparison.Ordinal);
            Assert.Contains("HOME=", r.Feedback, StringComparison.Ordinal);
            Assert.Contains("DOTNET_X=1", r.Feedback, StringComparison.Ordinal);
            Assert.Contains("NuGetPackageSourceCredentials_github=cred", r.Feedback, StringComparison.Ordinal);

            JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
            List<string> envNames = [.. gateJson["env_names"]!.AsArray().Select(n => n!.GetValue<string>())];
            Assert.DoesNotContain("LW_TEST_SECRET", envNames, StringComparer.Ordinal);
            Assert.DoesNotContain("ANTHROPIC_API_KEY", envNames, StringComparer.Ordinal);
            Assert.Contains("DOTNET_X", envNames, StringComparer.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LW_TEST_SECRET", oldSecret);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
            Environment.SetEnvironmentVariable("DOTNET_X", oldDotnetX);
            Environment.SetEnvironmentVariable("NuGetPackageSourceCredentials_github", oldCreds);
        }
    }

    [Fact]
    public async Task Gate_env_allowlist_entry_lets_a_variable_throughAsync()
    {
        string? oldSecret = Environment.GetEnvironmentVariable("LW_TEST_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("LW_TEST_SECRET", "x");
            string dir = NewDir();
            string script = WriteScript(dir, "env\nexit 0\n");
            string runDir = NewDir();
            Gate.GateResult r = await RunInAsync(dir, Spec(script, env: ["LW_TEST_*"]), runDir);

            Assert.Contains("LW_TEST_SECRET=x", r.Feedback, StringComparison.Ordinal);
            JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
            List<string> envNames = [.. gateJson["env_names"]!.AsArray().Select(n => n!.GetValue<string>())];
            Assert.Contains("LW_TEST_SECRET", envNames, StringComparer.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LW_TEST_SECRET", oldSecret);
        }
    }
}
