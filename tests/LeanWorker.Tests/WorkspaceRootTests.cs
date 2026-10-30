using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The workspace root follows the task, not the shell the launcher was typed into: a wrong
/// launcher cwd (a typo'd cd, a konsole that fell back to $HOME) must not become the worker's
/// project directory or the write-scope anchor.
/// </summary>
[Collection("launcher-process-state")]
public class WorkspaceRootTests
{
    [Fact]
    public void StartInfo_uses_the_prepared_working_directory()
    {
        string workspace = Directory.CreateTempSubdirectory("lw-ws").FullName.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            Prepared prep = new(Executable: "echo", Args: [], Env: [], CommandText: "echo", ScratchDirectory: null, WorkingDirectory: workspace);

            System.Diagnostics.ProcessStartInfo psi = Launcher.StartInfo(prep);

            Assert.Equal(workspace, psi.WorkingDirectory);
            Assert.Equal(workspace, psi.Environment["PWD"]);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    [Fact]
    public async Task ResolveAsync_prefers_the_tasks_tree_over_the_launchers_cwdAsync()
    {
        string repoA = Directory.CreateTempSubdirectory("lw-ws-a").FullName;
        string repoB = Directory.CreateTempSubdirectory("lw-ws-b").FullName;
        try
        {
            RunGit(repoA, "init", "-q");
            RunGit(repoB, "init", "-q");
            string taskDir = Directory.CreateDirectory(Path.Combine(repoA, "lean-worker", "inbox", "t")).FullName;
            string cwd = Directory.CreateDirectory(Path.Combine(repoB, "sub")).FullName;

            string resolved = await Workspace.ResolveAsync(taskDir, cwd);

            Assert.Equal(Resolve(repoA), resolved);
        }
        finally
        {
            Directory.Delete(repoA, recursive: true);
            Directory.Delete(repoB, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_the_cwd_itself_when_the_task_is_outside_any_treeAsync()
    {
        string outside = Directory.CreateTempSubdirectory("lw-ws-out").FullName;
        string repo = Directory.CreateTempSubdirectory("lw-ws-repo").FullName;
        try
        {
            RunGit(repo, "init", "-q");
            string cwd = Directory.CreateDirectory(Path.Combine(repo, "sub")).FullName;

            string resolved = await Workspace.ResolveAsync(outside, cwd);

            Assert.Equal(cwd, resolved);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_returns_the_cwd_when_neither_side_is_in_a_treeAsync()
    {
        string outside = Directory.CreateTempSubdirectory("lw-ws-out2").FullName;
        string cwd = Directory.CreateTempSubdirectory("lw-ws-cwd").FullName;
        try
        {
            Assert.Equal(cwd, await Workspace.ResolveAsync(outside, cwd));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveRelativeAsync_finds_a_repo_root_relative_task_from_a_subdirectoryAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-rel").FullName;
        try
        {
            RunGit(repo, "init", "-q");
            string taskDir = Directory.CreateDirectory(Path.Combine(repo, ".lean-worker", "inbox", "t")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            string cwd = Directory.CreateDirectory(Path.Combine(repo, "labs", "one")).FullName;

            string resolved = await Workspace.ResolveRelativeAsync(".lean-worker/inbox/t/task.md", cwd);

            Assert.Equal(Path.Combine(taskDir, "task.md"), resolved);
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task ResolveRelativeAsync_returns_the_input_unchanged_when_nothing_matchesAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("lw-ws-miss").FullName;
        try
        {
            Assert.Equal("no/such/task.md", await Workspace.ResolveRelativeAsync("no/such/task.md", cwd));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    /// <summary>
    /// The incident this class exists for: a worker launched with a cwd deep inside the checkout
    /// (or a wrong directory entirely) still runs with the task's repo root as its working
    /// directory — the stub "claude" records the cwd it was actually given.
    /// </summary>
    [Fact]
    public async Task Worker_runs_in_the_tasks_repo_root_even_when_the_launcher_cwd_is_a_subdirectoryAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-incident").FullName;
        string stubDir = Directory.CreateTempSubdirectory("lw-ws-stub").FullName;
        string capture = Directory.CreateTempSubdirectory("lw-ws-cap").FullName;
        try
        {
            RunGit(repo, "init", "-q");
            string runsRoot = Path.Combine(repo, ".lean-worker");
            string taskDir = Directory.CreateDirectory(Path.Combine(runsRoot, "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            string launcherCwd = Directory.CreateDirectory(Path.Combine(repo, "labs", "deep", "sub")).FullName;
            string pwdCapture = Path.Combine(capture, "pwd.txt");

            string stubPath = Path.Combine(stubDir, "claude");
            await File.WriteAllTextAsync(stubPath, "#!/bin/sh\npwd -P > " + Quote(pwdCapture) + "\ncat >/dev/null\nexit 0\n", TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            string? oldPath = Environment.GetEnvironmentVariable("PATH");
            string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            string oldCwd = Directory.GetCurrentDirectory();
            try
            {
                Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + oldPath);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
                Directory.SetCurrentDirectory(launcherCwd);
                Options o = new() { TaskFile = ".lean-worker/inbox/wk/task.md", RunsRoot = runsRoot, Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "workspace-anchor" };
                _ = await Launcher.RunAsync(o);
            }
            finally
            {
                Directory.SetCurrentDirectory(oldCwd);
                Environment.SetEnvironmentVariable("PATH", oldPath);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
            }

            Assert.True(File.Exists(pwdCapture), "the worker stub never ran");
            Assert.Equal(Resolve(repo), (await File.ReadAllTextAsync(pwdCapture, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(stubDir, recursive: true);
            Directory.Delete(capture, recursive: true);
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Resolve(string path) =>
        new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? new DirectoryInfo(path).FullName;

    private static void RunGit(string dir, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo psi = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-C", dir, "-c", "user.email=t@t", "-c", "user.name=t" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
