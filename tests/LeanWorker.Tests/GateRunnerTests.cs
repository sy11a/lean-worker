using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Runs Gate.RunAsync against real sh scripts. Touches the current directory (relative report paths resolve
/// against it) and the process environment (the allowlist tests), so these run without parallelism.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class GateRunnerTests : IDisposable
{
    private readonly TempDirs _tracked = new();

    public void Dispose() => _tracked.Dispose();

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

    private string NewDir() => _tracked.Create("lw-gate");

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

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);

    [GeneratedRegex(@"^path\[(?<p>\s+)\]$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceCaptureRegex();

    private static async Task<Gate.GateResult> RunBoundedAsync(string cwd, GateSpec spec, string runDir) =>
        await RunInAsync(cwd, spec, runDir).WaitAsync(_bound, TimeProvider.System, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_report_path_that_is_a_symlink_to_dev_zero_is_an_error_without_hangingAsync()
    {
        Assert.SkipUnless(File.Exists("/dev/zero"), "no /dev/zero on this platform");

        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        string link = Path.Combine(dir, "rep.json");
        _ = File.CreateSymbolicLink(link, "/dev/zero");
        string script = WriteScript(dir, "echo 'sarif: rep.json'\nexit 1\n");

        Gate.GateResult r = await RunBoundedAsync(dir, Spec(script), dirs.Create("lw-gate"));

        Assert.Equal("error", r.Outcome);
        Assert.Contains("not a regular file", r.Error, StringComparison.Ordinal);
        Assert.Contains(link, r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_path_that_is_a_fifo_is_an_error_without_hangingAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Unix only");

        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        string script = WriteScript(dir, "mkfifo rep.fifo\necho 'sarif: rep.fifo'\nexit 1\n");

        Gate.GateResult r = await RunBoundedAsync(dir, Spec(script), dirs.Create("lw-gate"));

        Assert.Equal("error", r.Outcome);
        Assert.Contains("rep.fifo", r.Error, StringComparison.Ordinal);
        Assert.True(
            r.Error!.Contains("not a regular file", StringComparison.Ordinal) || r.Error.Contains("could not be read in time", StringComparison.Ordinal),
            r.Error);
    }

    [Fact]
    public async Task A_report_over_64_MiB_is_an_errorAsync()
    {
        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        await using (FileStream fs = File.Create(Path.Combine(dir, "big.json")))
        {
            fs.SetLength((64L * 1024L * 1024L) + 1L);
        }

        string script = WriteScript(dir, "echo 'sarif: big.json'\nexit 1\n");

        Gate.GateResult r = await RunBoundedAsync(dir, Spec(script), dirs.Create("lw-gate"));

        Assert.Equal("error", r.Outcome);
        Assert.Contains("is too large", r.Error, StringComparison.Ordinal);
        Assert.Contains("big.json", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_whitespace_only_report_capture_is_an_empty_report_pathAsync()
    {
        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        string script = WriteScript(dir, "echo 'path[   ]'\nexit 1\n");

        Gate.GateResult r = await RunBoundedAsync(dir, Spec(script, reportRegex: WhitespaceCaptureRegex()), dirs.Create("lw-gate"));

        Assert.Equal("error", r.Outcome);
        Assert.Equal("gate exited 1 but report path was empty", r.Error);
    }

    [Fact]
    public async Task A_bare_argv0_not_on_PATH_is_a_start_error_with_exit_code_minus_oneAsync()
    {
        using TempDirs dirs = new();
        string runDir = dirs.Create("lw-gate");
        GateSpec spec = new(["lw-gate-does-not-exist-xyz"], DefaultRegex(), "runs[0].results", 8000, 30, 5, 10m, []);

        Gate.GateResult r = await Gate.RunAsync(spec, runDir, CancellationToken.None);

        Assert.Equal("error", r.Outcome);
        Assert.Equal(-1, r.ExitCode);
        Assert.Equal("gate executable 'lw-gate-does-not-exist-xyz' not found on PATH", r.Error);
        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(-1, gateJson["exit_code"]!.GetValue<int>());
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_bare_argv0_is_not_searched_for_in_the_working_directoryAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix executable bit");

        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        string marker = Path.Combine(dir, "planted-ran");
        string planted = Path.Combine(dir, "lw-planted-tool");
        WriteFile(planted, "#!/bin/sh\ntouch '" + marker + "'\nexit 0\n");
        File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GateSpec spec = new(["lw-planted-tool"], DefaultRegex(), "runs[0].results", 8000, 30, 5, 10m, []);

        Gate.GateResult r = await RunBoundedAsync(dir, spec, dirs.Create("lw-gate"));

        Assert.Equal("error", r.Outcome);
        Assert.Contains("not found on PATH", r.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Symlinks_planted_at_gate_log_and_gate_json_are_replaced_not_followedAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        string runDir = dirs.Create("lw-gate");
        string victimLog = Path.Combine(dir, "victim-log.txt");
        string victimJson = Path.Combine(dir, "victim-json.txt");
        WriteFile(victimLog, "untouched-log");
        WriteFile(victimJson, "untouched-json");
        _ = File.CreateSymbolicLink(Path.Combine(runDir, "gate.log"), victimLog);
        _ = File.CreateSymbolicLink(Path.Combine(runDir, "gate.json"), victimJson);
        string script = WriteScript(dir, "echo hello-from-the-gate\nexit 0\n");

        Gate.GateResult r = await RunBoundedAsync(dir, Spec(script), runDir);

        Assert.Equal("clean", r.Outcome);
        Assert.Equal("untouched-log", await File.ReadAllTextAsync(victimLog, TestContext.Current.CancellationToken));
        Assert.Equal("untouched-json", await File.ReadAllTextAsync(victimJson, TestContext.Current.CancellationToken));
        Assert.Null(new FileInfo(Path.Combine(runDir, "gate.log")).LinkTarget);
        Assert.Null(new FileInfo(Path.Combine(runDir, "gate.json")).LinkTarget);
        string log = await File.ReadAllTextAsync(Path.Combine(runDir, "gate.log"), TestContext.Current.CancellationToken);
        Assert.Contains("hello-from-the-gate", log, StringComparison.Ordinal);
        JsonObject gateJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "gate.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal("clean", gateJson["outcome"]!.GetValue<string>());
        Assert.DoesNotContain(Directory.GetFiles(runDir), f => Path.GetFileName(f).StartsWith(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_gate_is_an_error_and_the_process_is_killedAsync()
    {
        // The configured timeout is whole minutes (no knob below one), so the timeout path is not
        // reachable in a unit test without a production hook; the caller's token takes the sibling
        // path (same kill, a different error message).
        using TempDirs dirs = new();
        string dir = dirs.Create("lw-gate");
        string marker = Path.Combine(dir, "survived");
        string script = WriteScript(dir, "sleep 3\ntouch '" + marker + "'\nexit 0\n");
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        string oldCwd = Directory.GetCurrentDirectory();
        Gate.GateResult r;
        try
        {
            Directory.SetCurrentDirectory(dir);
            r = await Gate.RunAsync(Spec(script), dirs.Create("lw-gate"), cts.Token).WaitAsync(_bound, TimeProvider.System, TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
        }

        Assert.Equal("error", r.Outcome);
        Assert.Equal(-1, r.ExitCode);
        Assert.Equal("gate cancelled", r.Error);
        Assert.True(r.Duration < _bound);

        // A script that had not been killed would write the marker once its sleep ends.
        await Task.Delay(TimeSpan.FromSeconds(4), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker), "the gate process kept running after the cancellation");
    }
}
