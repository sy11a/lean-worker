using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// End-to-end tests of the gate round loop through Launcher.RunAsync: a stubbed "claude" worker (as in
/// RunAsyncGolden) plus a real "sh" gate script that reads a counter file to vary its outcome per round.
/// </summary>
[Collection("launcher-process-state")]
public class GateLoopTests
{
    private static string NewScratch() => Directory.CreateTempSubdirectory("lw-gate-loop").FullName;

    private static async Task WriteReportFileAsync(string path, int count)
    {
        JsonArray results = new([.. Enumerable.Range(0, count).Select(i => (JsonNode)i)]);
        JsonObject doc = new() { ["runs"] = new JsonArray { new JsonObject { ["results"] = results } } };
        await File.WriteAllTextAsync(path, doc.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private static async Task WriteExecutableAsync(string path, string body)
    {
        await File.WriteAllTextAsync(path, body, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Writes a gate script at &lt;scratch&gt;/gate.sh that, on its Nth invocation, exits with
    /// <c>rounds[N-1].ExitCode</c> and (when ExitCode is 1 and Count &gt; 0) prints a "sarif: &lt;report&gt;"
    /// line for a report with that many results. Invocations past the list repeat the last entry.
    /// </summary>
    private static async Task<string> WriteSequenceScriptAsync(string scratch, params (int ExitCode, int Count)[] rounds)
    {
        string counter = Path.Combine(scratch, "n");
        StringBuilder sb = new();
        _ = sb.Append("#!/bin/sh\n");
        _ = sb.Append("N=$(cat '").Append(counter).Append("' 2>/dev/null || echo 0); N=$((N+1)); echo $N > '").Append(counter).Append("'\n");
        _ = sb.Append("case $N in\n");
        for (int i = 0; i < rounds.Length; i++)
        {
            (int exitCode, int count) = rounds[i];
            string label = (i + 1).ToString(CultureInfo.InvariantCulture);
            _ = sb.Append(i == rounds.Length - 1 ? "*) " : label + ") ");
            if (exitCode is 1 && count > 0)
            {
                string report = Path.Combine(scratch, "report" + label + ".json");
                await WriteReportFileAsync(report, count);
                _ = sb.Append("echo 'sarif: ").Append(report).Append("'; ");
            }
            _ = sb.Append("exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).Append(" ;;\n");
        }
        _ = sb.Append("esac\n");

        string path = Path.Combine(scratch, "gate.sh");
        await WriteExecutableAsync(path, sb.ToString());
        return path;
    }

    private static JsonObject GateProfile(string scriptPath, int? maxTotalUsd = null)
    {
        return new()
        {
            ["gate"] = new JsonObject
            {
                ["command"] = new JsonArray(["sh", scriptPath]),
                ["maxTotalUsd"] = maxTotalUsd,
            },
        };
    }

    private static string[] RunDirNames(string root) =>
        [.. Directory.GetDirectories(Path.Combine(root, "runs")).Select(d => Path.GetFileName(d) ?? string.Empty).Order(StringComparer.Ordinal)];

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public async Task Clean_in_round_one_ends_the_chainAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string script = await WriteSequenceScriptAsync(scratch, (0, 0));
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream);

        Assert.Equal(0, code);
        _ = Assert.Single(RunDirNames(root));
        Assert.Contains("gate:     clean after 1 round(s), findings 0", stdout, StringComparison.Ordinal);

        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        JsonObject gate = s["gate"]!.AsObject();
        Assert.Equal("clean", gate["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task Three_two_zero_runs_three_rounds_and_ends_cleanAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string script = await WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (0, 0));
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream);

        Assert.Equal(0, code);
        string[] names = RunDirNames(root);
        Assert.Equal(3, names.Length);
        Assert.Contains(names, n => n.EndsWith("-golden", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("-golden-gate2", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("-golden-gate3", StringComparison.Ordinal));
        Assert.Contains("gate:     clean after 3 round(s), findings 3→2→0", stdout, StringComparison.Ordinal);

        string round2Dir = Path.Combine(root, "runs", names.Single(n => n.EndsWith("-golden-gate2", StringComparison.Ordinal)));
        string task2 = await File.ReadAllTextAsync(Path.Combine(round2Dir, "task.md"), TestContext.Current.CancellationToken);
        Assert.Contains(Launcher.GateHeading, task2, StringComparison.Ordinal);
        Assert.Contains("3 finding(s) (round 1)", task2, StringComparison.Ordinal);
        Assert.Contains("Full report:", task2, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(scratch, "report1.json"), task2, StringComparison.Ordinal);
        Assert.Contains("sarif: " + Path.Combine(scratch, "report1.json"), task2, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(task2, "do nothing"));
    }

    [Fact]
    public async Task Three_three_three_is_stuckAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string script = await WriteSequenceScriptAsync(scratch, (1, 3), (1, 3), (1, 3));
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream);

        Assert.Equal(4, code);
        Assert.Equal(3, RunDirNames(root).Length);
        Assert.Contains("STUCK", stdout, StringComparison.Ordinal);
        Assert.Contains("findings 3→3→3", stdout, StringComparison.Ordinal);
        Assert.Contains("no decrease in 2 rounds", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_exit_2_ends_the_chain_with_error_after_one_worker_runAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string script = await WriteSequenceScriptAsync(scratch, (2, 0));
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream);

        Assert.Equal(5, code);
        _ = Assert.Single(RunDirNames(root));
        Assert.Contains("gate:     ERROR:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_error_result_skips_the_gateAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string marker = Path.Combine(scratch, "gate-ran");
        string script = Path.Combine(scratch, "gate.sh");
        await WriteExecutableAsync(script, "#!/bin/sh\ntouch '" + marker + "'\nexit 0\n");
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));

        (int code, string _) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.ErrorStream);

        Assert.Equal(1, code);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task No_gate_flag_disables_the_gate_and_matches_a_gate_less_profileAsync()
    {
        string rootGated = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string marker = Path.Combine(scratch, "gate-ran");
        string script = Path.Combine(scratch, "gate.sh");
        await WriteExecutableAsync(script, "#!/bin/sh\ntouch '" + marker + "'\nexit 0\n");
        RunAsyncGolden.WriteProfile(rootGated, "test", GateProfile(script));
        (int gatedCode, string gatedStdout) = await RunAsyncGolden.RunAsync(rootGated, RunAsyncGolden.SuccessStream, o => o.NoGate = true);
        Assert.False(File.Exists(marker));

        string rootPlain = RunAsyncGolden.NewRoot();
        RunAsyncGolden.WriteProfile(rootPlain, "test", []);
        (int plainCode, string plainStdout) = await RunAsyncGolden.RunAsync(rootPlain, RunAsyncGolden.SuccessStream);

        Assert.Equal(plainCode, gatedCode);
        Assert.Equal(RunAsyncGolden.Normalize(plainStdout, rootPlain), RunAsyncGolden.Normalize(gatedStdout, rootGated));

        string runDir = RunAsyncGolden.RunDirFrom(gatedStdout);
        JsonObject s = RunAsyncGolden.Summary(runDir);
        Assert.Null(s["gate"]);
    }

    [Fact]
    public async Task Gate_max_rounds_override_makes_two_rounds_stuckAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string script = await WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (1, 1));
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream, o => o.GateMaxRounds = 2);

        Assert.Equal(4, code);
        Assert.Equal(2, RunDirNames(root).Length);
        Assert.Contains("STUCK", stdout, StringComparison.Ordinal);
        Assert.Contains("max rounds", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_max_rounds_without_a_profile_gate_throwsAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(
            async () => await RunAsyncGolden.RunAsync(root, "exit 0", o => o.GateMaxRounds = 3));
        Assert.Equal("--gate-max-rounds needs a profile with a gate", ex.Message);
    }

    [Fact]
    public async Task Continue_from_a_gate_round_cuts_the_task_at_the_gate_headingAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string scratch = NewScratch();
        string script = await WriteSequenceScriptAsync(scratch, (1, 3), (1, 2), (1, 1));
        RunAsyncGolden.WriteProfile(root, "test", GateProfile(script));
        _ = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream, o => o.GateMaxRounds = 2);
        string[] names = RunDirNames(root);
        string round2Dir = Path.Combine(root, "runs", names.Single(n => n.EndsWith("-golden-gate2", StringComparison.Ordinal)));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TimeProvider.System, TestContext.Current.CancellationToken);

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream,
            o => { o.TaskFile = null; o.Name = null; o.ContinueFrom = round2Dir; o.NoGate = true; });

        Assert.Equal(0, code);
        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        string task = await File.ReadAllTextAsync(Path.Combine(runDir, "task.md"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Launcher.GateHeading, task, StringComparison.Ordinal);
        Assert.Contains(Launcher.ContinuationHeading, task, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(task, "do nothing"));
    }
}
