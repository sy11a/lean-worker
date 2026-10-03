using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// End-to-end opencode runs: the temporary config home (XDG_CONFIG_HOME) is a unique directory outside
/// the run directory and the repository, exists with the plugin while the worker runs, and is removed
/// once the worker exits, whether it succeeds or fails.
/// </summary>
[Collection("launcher-process-state")]
public class OpencodeScratchDirectoryTests
{
    private static string Stub(string script)
    {
        string dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        string path = Path.Combine(dir, "opencode");
        File.WriteAllText(path, "#!/bin/sh\ncat >/dev/null\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    /// <summary>
    /// Runs the launcher with a stub "opencode" on PATH and a dummy key; returns (stdout, exit code).
    /// </summary>
    private static async Task<(string Stdout, int Code)> RunLauncherAsync(string runsRoot, string taskFile, string script)
    {
        _ = Directory.CreateDirectory(runsRoot);
        await File.WriteAllTextAsync(Path.Combine(runsRoot, "project.md"), "notes", TestContext.Current.CancellationToken);
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        string cleanCwd = Directory.CreateTempSubdirectory("lw-cwd").FullName;
        await using StringWriter outWriter = new();
        TextWriter oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("PATH", Stub(script) + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
            Directory.SetCurrentDirectory(cleanCwd);
            Console.SetOut(outWriter);
            Options o = new() { TaskFile = taskFile, RunsRoot = runsRoot, Runtime = "opencode", Model = "anthropic/claude-haiku-4-5", Mode = "auto", Name = Guid.NewGuid().ToString("N") };
            int code = await Launcher.RunAsync(o);
            return (outWriter.ToString(), code);
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    private static string RunDirFrom(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("run:", StringComparison.Ordinal)).Split("run:", 2)[1].Trim();

    private static string RecordScript(string xdgCapture, string pluginCapture, string tail)
    {
        return $"""
            printf '%s' "$XDG_CONFIG_HOME" > '{xdgCapture}'
            if [ -f "$XDG_CONFIG_HOME/opencode/plugins/lean-worker.ts" ]; then printf yes > '{pluginCapture}'; else printf no > '{pluginCapture}'; fi
            {tail}
            """;
    }

    [Fact]
    public async Task Successful_run_uses_a_removed_temp_directory_outside_the_run_dir_and_repoAsync()
    {
        string repoRoot = Directory.GetCurrentDirectory();
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);
        string capture = Directory.CreateTempSubdirectory("lw-capture").FullName;
        string xdgCapture = Path.Combine(capture, "xdg.txt");
        string pluginCapture = Path.Combine(capture, "plugin.txt");
        string script = RecordScript(xdgCapture, pluginCapture,
            """printf '%s\n' '{"type":"text","sessionID":"s1","part":{"messageID":"m1","text":"DONE"}}'""");

        (string stdout, int code) = await RunLauncherAsync(runsRoot, task, script);
        Assert.Equal(0, code);
        string runDir = RunDirFrom(stdout);
        string recordedXdg = await File.ReadAllTextAsync(xdgCapture, TestContext.Current.CancellationToken);
        string runDirName = Path.GetFileName(runDir);

        Assert.Equal("yes", await File.ReadAllTextAsync(pluginCapture, TestContext.Current.CancellationToken));
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), $"lean-worker-{runDirName}-").TrimEnd(Path.DirectorySeparatorChar), recordedXdg, StringComparison.Ordinal);
        Assert.DoesNotContain(runDir, recordedXdg, StringComparison.Ordinal);
        Assert.DoesNotContain(repoRoot, recordedXdg, StringComparison.Ordinal);
        Assert.False(Directory.Exists(recordedXdg));
        Assert.False(Directory.Exists(Path.Combine(runDir, "opencode-config")));
        Assert.True(File.Exists(Path.Combine(runDir, "opencode-config.json")));
    }

    [Fact]
    public async Task A_non_zero_exit_still_removes_the_temp_directoryAsync()
    {
        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);
        string capture = Directory.CreateTempSubdirectory("lw-capture").FullName;
        string xdgCapture = Path.Combine(capture, "xdg.txt");
        string pluginCapture = Path.Combine(capture, "plugin.txt");
        string script = RecordScript(xdgCapture, pluginCapture,
            """
            printf '%s\n' '{"type":"text","sessionID":"s1","part":{"messageID":"m1","text":"DONE"}}'
            exit 1
            """);

        (string stdout, int code) = await RunLauncherAsync(runsRoot, task, script);
        Assert.NotEqual(0, code);
        string runDir = RunDirFrom(stdout);
        string recordedXdg = await File.ReadAllTextAsync(xdgCapture, TestContext.Current.CancellationToken);

        Assert.Equal("yes", await File.ReadAllTextAsync(pluginCapture, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(recordedXdg));
        Assert.False(Directory.Exists(Path.Combine(runDir, "opencode-config")));
        Assert.True(File.Exists(Path.Combine(runDir, "opencode-config.json")));
    }
}
