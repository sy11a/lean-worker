using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Full runs of Launcher.Run against a stub "claude" executable: the stable system-file path and the /// first
/// call's cache-read fields. mode=bare keeps the lean-mode settings/hooks machinery out of the way.
/// </summary>
[Collection("launcher-process-state")]
public class SystemPathAndFirstCallCacheTests
{
    private static string Sha12(string content) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content)), 0, 6).ToLowerInvariant();

    private static string Stub(string script, string binaryName = "claude")
    {
        string dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        string path = Path.Combine(dir, binaryName);
        File.WriteAllText(path, "#!/bin/sh\ncat >/dev/null\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    private const string OpencodeOneTextLine = """
        printf '%s\n' '{"type":"text","sessionID":"s1","part":{"messageID":"m1","text":"DONE"}}'
        """;

    private const string OneCallStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}'
        printf '%s\n' '{"type":"result","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
        """;

    private const string NoCallStream = """
        printf '%s\n' '{"type":"result","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0,"num_turns":1,"permission_denials":[]}'
        """;

    /// <summary>
    /// Runs the launcher with a stub "claude" on PATH, a dummy key, and no git tree to scan; returns stdout.
    /// </summary>
    private static async Task<string> RunLauncherAsync(string runsRoot, string taskFile, string projectNotes, string script, string binaryName = "claude", Action<Options>? configure = null)
    {
        _ = Directory.CreateDirectory(runsRoot);
        await File.WriteAllTextAsync(Path.Combine(runsRoot, "project.md"), projectNotes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), TestContext.Current.CancellationToken);
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        string cleanCwd = Directory.CreateTempSubdirectory("lw-cwd").FullName;
        using StateDirScope state = new();
        await using StringWriter outWriter = new();
        TextWriter oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("PATH", Stub(script, binaryName) + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
            Directory.SetCurrentDirectory(cleanCwd);
            Console.SetOut(outWriter);
            // A fresh name per run: runs.jsonl/run-dir names are keyed by name + second-resolution timestamp.
            Options o = new() { TaskFile = taskFile, RunsRoot = runsRoot, Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = Guid.NewGuid().ToString("N") };
            configure?.Invoke(o);
            int code = await Launcher.RunAsync(o);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
        return outWriter.ToString();
    }

    private static string RunDirFrom(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("run:", StringComparison.Ordinal)).Split("run:", 2)[1].Trim();

    [Fact]
    public async Task Same_system_content_reuses_the_same_stable_path_across_runs_and_profilesAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);
        string expected = Path.Combine(runsRoot, "system", $"{Sha12("same notes")}.md");

        string out1 = await RunLauncherAsync(runsRoot, task, "same notes", NoCallStream);
        string run1 = RunDirFrom(out1);
        Assert.True(File.Exists(expected));
        Assert.Equal("same notes", await File.ReadAllTextAsync(expected, TestContext.Current.CancellationToken));
        Assert.Contains(expected, await File.ReadAllTextAsync(Path.Combine(run1, "command.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("same notes", await File.ReadAllTextAsync(Path.Combine(run1, "system.md"), TestContext.Current.CancellationToken));
        DateTime writeTime1 = File.GetLastWriteTimeUtc(expected);

        string out2 = await RunLauncherAsync(runsRoot, task, "same notes", NoCallStream);
        string run2 = RunDirFrom(out2);
        Assert.NotEqual(run1, run2, StringComparer.Ordinal);
        Assert.Equal(writeTime1, File.GetLastWriteTimeUtc(expected)); // reused, not rewritten
        Assert.Equal("same notes", await File.ReadAllTextAsync(expected, TestContext.Current.CancellationToken));
        Assert.Contains(expected, await File.ReadAllTextAsync(Path.Combine(run2, "command.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("same notes", await File.ReadAllTextAsync(Path.Combine(run2, "system.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Different_system_content_gets_a_different_pathAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);

        string out1 = await RunLauncherAsync(runsRoot, task, "notes A", NoCallStream);
        string out2 = await RunLauncherAsync(runsRoot, task, "notes B", NoCallStream);
        string pathA = Path.Combine(runsRoot, "system", $"{Sha12("notes A")}.md");
        string pathB = Path.Combine(runsRoot, "system", $"{Sha12("notes B")}.md");
        Assert.NotEqual(pathA, pathB, StringComparer.Ordinal);
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        Assert.Contains(pathA, await File.ReadAllTextAsync(Path.Combine(RunDirFrom(out1), "command.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains(pathB, await File.ReadAllTextAsync(Path.Combine(RunDirFrom(out2), "command.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claude_replace_system_prompt_uses_the_stable_path_not_the_run_dir_copyAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);
        string expected = Path.Combine(runsRoot, "system", $"{Sha12("replace notes")}.md");

        string stdout = await RunLauncherAsync(runsRoot, task, "replace notes", NoCallStream, configure: o => o.ReplaceSystemPrompt = true);
        string runDir = RunDirFrom(stdout);
        string command = await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);
        string[] args = command.Split(' ');
        int i = Array.IndexOf(args, "--system-prompt-file");
        Assert.True(i >= 0 && i + 1 < args.Length, command);
        Assert.Equal(expected, args[i + 1]);
        Assert.NotEqual(Path.Combine(runDir, "system.md"), args[i + 1], StringComparer.Ordinal);
        Assert.DoesNotContain(Path.Combine(runDir, "system.md"), command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opencode_instructions_point_to_the_stable_path_not_the_run_dir_copyAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);
        string expected = Path.Combine(runsRoot, "system", $"{Sha12("opencode notes")}.md");

        string stdout = await RunLauncherAsync(runsRoot, task, "opencode notes", OpencodeOneTextLine, binaryName: "opencode",
            configure: o => { o.Runtime = "opencode"; o.Mode = "auto"; });
        string runDir = RunDirFrom(stdout);
        JsonObject config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "opencode-config.json"), TestContext.Current.CancellationToken))!.AsObject();
        List<string> instructions = [.. config["instructions"]!.AsArray().Select(n => n!.GetValue<string>())];
        Assert.Equal([expected], instructions);
        Assert.DoesNotContain(Path.Combine(runDir, "system.md"), instructions, StringComparer.Ordinal);
    }

    [Fact]
    public async Task First_call_cache_read_fields_and_the_context_line_are_reportedAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);

        string stdout = await RunLauncherAsync(runsRoot, task, "notes", OneCallStream);
        string runDir = RunDirFrom(stdout);
        JsonObject summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "summary.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(9200, summary["first_call_cache_read"]!.GetValue<long>());
        Assert.Equal(0.902, summary["first_call_cache_read_share"]!.GetValue<double>());
        Assert.Equal(10200, summary["context_first_call"]!.GetValue<long>());

        string ledgerLine = (await File.ReadAllLinesAsync(Path.Combine(runsRoot, "runs.jsonl"), TestContext.Current.CancellationToken))[^1];
        JsonObject ledger = JsonNode.Parse(ledgerLine)!.AsObject();
        Assert.Equal(9200, ledger["first_call_cache_read"]!.GetValue<long>());
        Assert.Equal(0.902, ledger["first_call_cache_read_share"]!.GetValue<double>());

        Assert.Contains("context:  first call 10,200 (cache read 90%) | peak 10,200", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_calls_leaves_the_cache_read_fields_null_and_the_context_line_has_no_shareAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);

        string stdout = await RunLauncherAsync(runsRoot, task, "notes", NoCallStream);
        string runDir = RunDirFrom(stdout);
        JsonObject summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "summary.json"), TestContext.Current.CancellationToken))!.AsObject();
        Assert.True(summary["first_call_cache_read"] is null || summary["first_call_cache_read"]!.GetValueKind() is System.Text.Json.JsonValueKind.Null);
        Assert.True(summary["first_call_cache_read_share"] is null || summary["first_call_cache_read_share"]!.GetValueKind() is System.Text.Json.JsonValueKind.Null);

        string ledgerLine = (await File.ReadAllLinesAsync(Path.Combine(runsRoot, "runs.jsonl"), TestContext.Current.CancellationToken))[^1];
        JsonObject ledger = JsonNode.Parse(ledgerLine)!.AsObject();
        Assert.True(ledger["first_call_cache_read"] is null || ledger["first_call_cache_read"]!.GetValueKind() is System.Text.Json.JsonValueKind.Null);

        Assert.Contains("context:  first call 0 | peak 0", stdout, StringComparison.Ordinal);
    }
}
