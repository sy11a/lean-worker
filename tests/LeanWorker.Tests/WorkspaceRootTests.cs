using System.Text.Json.Nodes;
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

    /// <summary>
    /// The default relative runs root (".lean-worker") also resolves against the workspace root
    /// when the launcher's cwd is a subdirectory: the profile under the repo's .lean-worker must
    /// load and the run must be recorded there, not under the subdirectory.
    /// </summary>
    [Fact]
    public async Task Relative_runs_root_resolves_against_the_workspace_root_from_a_subdirectoryAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-runsroot").FullName;
        string stubDir = Directory.CreateTempSubdirectory("lw-ws-stub2").FullName;
        try
        {
            RunGit(repo, "init", "-q");
            string runsRoot = Path.Combine(repo, ".lean-worker");
            string taskDir = Directory.CreateDirectory(Path.Combine(runsRoot, "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runsRoot, "profiles.json"),
                """{"defaultProfile":"p1","profiles":{"p1":{"runtime":"claude"}}}""", TestContext.Current.CancellationToken);

            string stubPath = Path.Combine(stubDir, "claude");
            await File.WriteAllTextAsync(stubPath, "#!/bin/sh\ncat >/dev/null\nexit 0\n", TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            string? oldPath = Environment.GetEnvironmentVariable("PATH");
            string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            string oldCwd = Directory.GetCurrentDirectory();
            int code;
            await using StringWriter outWriter = new();
            TextWriter oldOut = Console.Out;
            try
            {
                Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + oldPath);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
                Directory.SetCurrentDirectory(Directory.CreateDirectory(Path.Combine(repo, "labs", "deep")).FullName);
                Console.SetOut(outWriter);
                Options o = new() { TaskFile = ".lean-worker/inbox/wk/task.md", Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "runs-root-anchor", Profile = "p1" };
                code = await Launcher.RunAsync(o);
            }
            finally
            {
                Console.SetOut(oldOut);
                Directory.SetCurrentDirectory(oldCwd);
                Environment.SetEnvironmentVariable("PATH", oldPath);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
            }

            Assert.NotEqual(2, code);
            string stdout = outWriter.ToString();
            Assert.DoesNotContain("profiles.json does not exist", stdout, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(repo, "labs", "deep", ".lean-worker")), "a runs root leaked into the subdirectory");
            Assert.True(Directory.Exists(Path.Combine(runsRoot, "runs")), "the run was not recorded under the workspace runs root");
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// The workspace note must reach the output: it is the one line that says where the worker
    /// ran and which directory was ignored. The price book's warnings used to replace the whole
    /// notes list, so the note never reached the result block nor the summary.
    /// </summary>
    [Fact]
    public async Task The_workspace_note_reaches_the_outputAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-note").FullName;
        string outside = Directory.CreateTempSubdirectory("lw-ws-note-outside").FullName;
        string stubDir = WriteStub("claude", "cat >/dev/null\nexit 0\n");
        try
        {
            RunGit(repo, "init", "-q");
            string taskDir = Directory.CreateDirectory(Path.Combine(repo, ".lean-worker", "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "note" };
            (int code, string stdout, string cwd) = await RunWithStubAsync(o, outside, stubDir);

            Assert.NotEqual(2, code);
            string note = $"workspace {GitRoot(repo)} (anchored on the task's location; the launcher's own cwd {cwd} is ignored)";
            Assert.Contains("note:     " + note, stdout, StringComparison.Ordinal);

            // The note is the first one: it frames everything the run read from the workspace.
            JsonObject summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RunDirFrom(stdout), "summary.json"), TestContext.Current.CancellationToken))!.AsObject();
            Assert.Equal(note, summary["notes"]!.AsArray()[0]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(outside, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// The runs root follows the task's repo, not the shell: when the cwd is another checkout with
    /// its own ".lean-worker", the profiles, the prices and the run record come from the task's
    /// repo — the workspace comes first — and the cwd's runs root is not read at all.
    /// </summary>
    [Fact]
    public async Task The_workspaces_runs_root_wins_over_a_cwd_with_its_own_lean_workerAsync()
    {
        string taskRepo = Directory.CreateTempSubdirectory("lw-ws-taskrepo").FullName;
        string otherRepo = Directory.CreateTempSubdirectory("lw-ws-otherrepo").FullName;
        string stubDir = WriteStub("claude", "cat >/dev/null\nexit 0\n");
        try
        {
            RunGit(taskRepo, "init", "-q");
            RunGit(otherRepo, "init", "-q");
            string taskRuns = Path.Combine(taskRepo, ".lean-worker");
            string otherRuns = Path.Combine(otherRepo, ".lean-worker");
            _ = Directory.CreateDirectory(taskRuns);
            _ = Directory.CreateDirectory(otherRuns);
            string taskDir = Directory.CreateDirectory(Path.Combine(taskRuns, "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(taskRuns, "profiles.json"),
                """{"defaultProfile":"from-task-repo","profiles":{"from-task-repo":{"runtime":"claude"}}}""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(otherRuns, "profiles.json"),
                """{"defaultProfile":"from-cwd-repo","profiles":{"from-cwd-repo":{"runtime":"claude"}}}""", TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "runs-root-order" };
            (int code, string stdout, _) = await RunWithStubAsync(o, otherRepo, stubDir);

            Assert.NotEqual(2, code);
            Assert.Contains("profile from-task-repo", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("from-cwd-repo", stdout, StringComparison.Ordinal);
            Assert.StartsWith(Path.Combine(taskRuns, "runs"), RunDirFrom(stdout), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(otherRuns, "runs")), "the run was recorded under the cwd's runs root");
        }
        finally
        {
            Directory.Delete(taskRepo, recursive: true);
            Directory.Delete(otherRepo, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// A relative runs root that misses under the workspace root falls back exactly as before: the
    /// cwd's own ".lean-worker" is read, because the task's repo has none.
    /// </summary>
    [Fact]
    public async Task A_runs_root_that_misses_under_the_workspace_falls_back_to_the_cwdAsync()
    {
        string taskRepo = Directory.CreateTempSubdirectory("lw-ws-noruns").FullName;
        string cwdRepo = Directory.CreateTempSubdirectory("lw-ws-cwdrepo").FullName;
        string stubDir = WriteStub("claude", "cat >/dev/null\nexit 0\n");
        try
        {
            RunGit(taskRepo, "init", "-q");
            RunGit(cwdRepo, "init", "-q");
            string taskDir = Directory.CreateDirectory(Path.Combine(taskRepo, "inbox")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            string cwdRuns = Path.Combine(cwdRepo, ".lean-worker");
            _ = Directory.CreateDirectory(cwdRuns);
            await File.WriteAllTextAsync(Path.Combine(cwdRuns, "profiles.json"),
                """{"defaultProfile":"from-cwd-repo","profiles":{"from-cwd-repo":{"runtime":"claude"}}}""", TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "runs-root-fallback" };
            (int code, string stdout, _) = await RunWithStubAsync(o, cwdRepo, stubDir);

            Assert.NotEqual(2, code);
            Assert.Contains("profile from-cwd-repo", stdout, StringComparison.Ordinal);
            // The relative root stays relative (the cwd's own), exactly as before.
            Assert.StartsWith(".lean-worker/runs/", RunDirFrom(stdout), StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(cwdRuns, "runs")), "the run was not recorded under the cwd's runs root");
        }
        finally
        {
            Directory.Delete(taskRepo, recursive: true);
            Directory.Delete(cwdRepo, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// A task outside any tree keeps the cwd bit-for-bit: the workspace is the cwd, the relative
    /// runs root stays relative (the run dir is printed as ".lean-worker/runs/...") and the worker
    /// runs in the cwd.
    /// </summary>
    [Fact]
    public async Task A_task_outside_any_tree_keeps_the_cwd_bit_for_bitAsync()
    {
        string cwdDir = Directory.CreateTempSubdirectory("lw-ws-plaincwd").FullName;
        string taskDir = Directory.CreateTempSubdirectory("lw-ws-plaintask").FullName;
        string capture = Directory.CreateTempSubdirectory("lw-ws-plaincap").FullName;
        string stubDir = WriteStub("claude", "pwd -P > " + Quote(Path.Combine(capture, "pwd.txt")) + "\ncat >/dev/null\nexit 0\n");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            string cwdRuns = Path.Combine(cwdDir, ".lean-worker");
            _ = Directory.CreateDirectory(cwdRuns);
            await File.WriteAllTextAsync(Path.Combine(cwdRuns, "profiles.json"),
                """{"defaultProfile":"from-cwd","profiles":{"from-cwd":{"runtime":"claude"}}}""", TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "plain-cwd" };
            (int code, string stdout, _) = await RunWithStubAsync(o, cwdDir, stubDir);

            Assert.NotEqual(2, code);
            Assert.Contains("profile from-cwd", stdout, StringComparison.Ordinal);
            // The relative runs root is not re-anchored: the run dir stays relative.
            Assert.StartsWith(".lean-worker/runs/", RunDirFrom(stdout), StringComparison.Ordinal);
            Assert.Equal(Resolve(cwdDir), (await File.ReadAllTextAsync(Path.Combine(capture, "pwd.txt"), TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            Directory.Delete(cwdDir, recursive: true);
            Directory.Delete(taskDir, recursive: true);
            Directory.Delete(capture, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// The incident's exact shape: the launcher's cwd is outside every repo (a scratch directory)
    /// and the task path is absolute. The worker still runs at the task's repo root, and the run is
    /// recorded under that repo's runs root.
    /// </summary>
    [Fact]
    public async Task Worker_runs_in_the_tasks_repo_root_when_the_launcher_cwd_is_outside_any_repoAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-incident2").FullName;
        string scratch = Directory.CreateTempSubdirectory("lw-ws-scratch").FullName;
        string capture = Directory.CreateTempSubdirectory("lw-ws-cap2").FullName;
        string stubDir = WriteStub("claude", "pwd -P > " + Quote(Path.Combine(capture, "pwd.txt")) + "\ncat >/dev/null\nexit 0\n");
        try
        {
            RunGit(repo, "init", "-q");
            string taskDir = Directory.CreateDirectory(Path.Combine(repo, ".lean-worker", "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "outside-cwd" };
            (int code, string stdout, _) = await RunWithStubAsync(o, scratch, stubDir);

            Assert.NotEqual(2, code);
            Assert.Equal(Resolve(repo), (await File.ReadAllTextAsync(Path.Combine(capture, "pwd.txt"), TestContext.Current.CancellationToken)).Trim());
            Assert.StartsWith(Path.Combine(repo, ".lean-worker", "runs"), RunDirFrom(stdout), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(scratch, recursive: true);
            Directory.Delete(capture, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// The opencode runtime gets the same workspace as the claude one: the worker's working
    /// directory and its PWD are the task's repo root (opencode reads its project directory from
    /// PWD), even when the launcher's cwd is somewhere else entirely.
    /// </summary>
    [Fact]
    public async Task The_opencode_worker_runs_in_the_tasks_repo_root_with_that_pwdAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-oc").FullName;
        string scratch = Directory.CreateTempSubdirectory("lw-ws-oc-scratch").FullName;
        string capture = Directory.CreateTempSubdirectory("lw-ws-oc-cap").FullName;
        string stubDir = WriteStub("opencode",
            "pwd -P > " + Quote(Path.Combine(capture, "pwd.txt")) + "\n" +
            "printf '%s' \"$PWD\" > " + Quote(Path.Combine(capture, "pwd-env.txt")) + "\n" +
            "cat >/dev/null\nexit 0\n");
        try
        {
            RunGit(repo, "init", "-q");
            string taskDir = Directory.CreateDirectory(Path.Combine(repo, ".lean-worker", "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Runtime = "opencode", Model = "anthropic/claude-haiku-4-5", Mode = "auto", Name = "opencode-workspace" };
            (int code, _, _) = await RunWithStubAsync(o, scratch, stubDir);

            Assert.NotEqual(2, code);
            Assert.Equal(Resolve(repo), (await File.ReadAllTextAsync(Path.Combine(capture, "pwd.txt"), TestContext.Current.CancellationToken)).Trim());
            Assert.Equal(Resolve(repo), (await File.ReadAllTextAsync(Path.Combine(capture, "pwd-env.txt"), TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(scratch, recursive: true);
            Directory.Delete(capture, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// The gate's child process runs in the workspace root, like the worker: a gate that reads the
    /// repo it checks must see the task's repo, not the directory the launcher was typed in.
    /// </summary>
    [Fact]
    public async Task The_gate_child_runs_in_the_workspace_rootAsync()
    {
        string repo = Directory.CreateTempSubdirectory("lw-ws-gate").FullName;
        string scratch = Directory.CreateTempSubdirectory("lw-ws-gate-scratch").FullName;
        string capture = Directory.CreateTempSubdirectory("lw-ws-gate-cap").FullName;
        string gateDir = Directory.CreateTempSubdirectory("lw-ws-gate-script").FullName;
        string stubDir = WriteStub("claude", "cat >/dev/null\n" + RunAsyncGolden.SuccessStream);
        try
        {
            RunGit(repo, "init", "-q");
            string runsRoot = Path.Combine(repo, ".lean-worker");
            string taskDir = Directory.CreateDirectory(Path.Combine(runsRoot, "inbox", "wk")).FullName;
            await File.WriteAllTextAsync(Path.Combine(taskDir, "task.md"), "do nothing", TestContext.Current.CancellationToken);
            string gateScript = Path.Combine(gateDir, "gate.sh");
            await File.WriteAllTextAsync(gateScript, "#!/bin/sh\npwd -P > " + Quote(Path.Combine(capture, "gate-pwd.txt")) + "\nexit 0\n", TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(gateScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            JsonObject profile = new()
            {
                ["runtime"] = "claude",
                ["gate"] = new JsonObject { ["command"] = new JsonArray("sh", gateScript) },
            };
            JsonObject profiles = new()
            {
                ["defaultProfile"] = "gated",
                ["profiles"] = new JsonObject { ["gated"] = profile },
            };
            await File.WriteAllTextAsync(Path.Combine(runsRoot, "profiles.json"), profiles.ToJsonString(), TestContext.Current.CancellationToken);

            Options o = new() { TaskFile = Path.Combine(taskDir, "task.md"), Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "gate-workspace" };
            (int code, string stdout, _) = await RunWithStubAsync(o, scratch, stubDir);

            Assert.Equal(0, code);
            Assert.Contains("gate:     clean after 1 round(s)", stdout, StringComparison.Ordinal);
            string gatePwd = Path.Combine(capture, "gate-pwd.txt");
            Assert.True(File.Exists(gatePwd), "the gate never ran");
            Assert.Equal(Resolve(repo), (await File.ReadAllTextAsync(gatePwd, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(scratch, recursive: true);
            Directory.Delete(capture, recursive: true);
            Directory.Delete(gateDir, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    /// <summary>
    /// Runs the launcher with a stub worker on PATH and the given working directory; returns the
    /// exit code, the printed block and the directory the launcher ran in.
    /// </summary>
    private static async Task<(int Code, string Stdout, string Cwd)> RunWithStubAsync(Options o, string cwd, string stubDir)
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        await using StringWriter outWriter = new();
        TextWriter oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
            Directory.SetCurrentDirectory(cwd);
            Console.SetOut(outWriter);
            int code = await Launcher.RunAsync(o);
            return (code, outWriter.ToString(), Directory.GetCurrentDirectory());
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    /// <summary>
    /// A stub worker executable named <paramref name="name"/> on PATH; the body is run by /bin/sh.
    /// </summary>
    private static string WriteStub(string name, string body)
    {
        string dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    /// <summary>
    /// The git working tree root of a directory, as the launcher sees it.
    /// </summary>
    private static string GitRoot(string dir)
    {
        System.Diagnostics.ProcessStartInfo psi = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-C", dir, "rev-parse", "--show-toplevel" })
        {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    private static string RunDirFrom(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("run:", StringComparison.Ordinal)).Split("run:", 2)[1].Trim();

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
