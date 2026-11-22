using System.Globalization;
using System.Runtime.CompilerServices;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The guard that keeps the suite out of the developer's own run index: every launch writes a pointer file under
/// $LEAN_WORKER_STATE_DIR/runs, else $XDG_STATE_HOME/lean-worker/runs, else ~/.local/state/lean-worker/runs, and the
/// tests that launch (in this process or in a child) must point the first variable at a folder of their own. The
/// helpers are checked by launching with HOME and XDG_STATE_HOME pointed at empty folders (the "real" index of these
/// tests) and finding both still empty; the launching test files are checked by reading their source.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StateIsolationGuardTests : IDisposable
{
    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static bool HasPointers(string home)
    {
        bool folder = Directory.EnumerateDirectories(home, "lean-worker", SearchOption.AllDirectories).Any();
        return folder || Directory.EnumerateFiles(home, "*.json", SearchOption.AllDirectories).Any();
    }

    [Fact]
    public async Task A_launch_through_the_run_helper_leaves_the_home_and_xdg_state_folders_emptyAsync()
    {
        string home = _dirs.Create("lw-guard-home");
        string xdg = _dirs.Create("lw-guard-xdg");
        using EnvScope env = EnvScope.Clean(("HOME", home), ("XDG_STATE_HOME", xdg), (StateDirScope.Variable, null));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(_dirs.NewRoot(), RunAsyncGolden.SuccessStream);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "status.json")));
        Assert.False(HasPointers(home), "a pointer reached $HOME/.local/state");
        Assert.False(HasPointers(xdg), "a pointer reached $XDG_STATE_HOME");
        Assert.Null(Environment.GetEnvironmentVariable(StateDirScope.Variable));
    }

    [Fact]
    public async Task The_same_launch_without_the_helpers_folder_does_write_to_the_xdg_folder_so_the_guard_can_failAsync()
    {
        string home = _dirs.Create("lw-guard-home");
        string xdg = _dirs.Create("lw-guard-xdg");
        using EnvScope env = EnvScope.Clean(("HOME", home), ("XDG_STATE_HOME", xdg));

        (int code, _) = await RunAsyncGolden.RunAsync(_dirs.NewRoot(), RunAsyncGolden.SuccessStream, environment: [(StateDirScope.Variable, null)]);

        Assert.Equal(0, code);
        Assert.True(HasPointers(xdg));
        Assert.False(HasPointers(home));
    }

    [Fact]
    public async Task A_launch_through_the_run_helper_writes_its_pointer_to_the_folder_the_test_gaveAsync()
    {
        string state = _dirs.Create("lw-guard-state");

        _ = await RunAsyncGolden.RunAsync(_dirs.NewRoot(), RunAsyncGolden.SuccessStream, stateDir: state);

        _ = Assert.Single(Directory.GetFiles(Path.Combine(state, "runs")));
    }

    [Fact]
    public void The_command_sandbox_points_the_run_index_into_itself_and_gives_the_old_value_back()
    {
        string? before = Environment.GetEnvironmentVariable(StateDirScope.Variable);
        string indexInside;
        string stateDir;
        using (CommandSandbox sandbox = new())
        {
            stateDir = sandbox.StateDir;
            indexInside = StatusIndex.Folder(name => Environment.GetEnvironmentVariable(name))!;
            Assert.Equal(sandbox.StateDir, Environment.GetEnvironmentVariable(StateDirScope.Variable));
        }

        Assert.Equal(Path.Combine(stateDir, "runs"), indexInside);
        Assert.Equal(before, Environment.GetEnvironmentVariable(StateDirScope.Variable));
    }

    [Fact]
    public void A_state_dir_scope_sets_the_variable_and_gives_the_old_value_back_and_removes_its_own_folder()
    {
        using EnvScope outer = new((StateDirScope.Variable, "/outer/state"));
        string root;
        using (StateDirScope scope = new())
        {
            root = scope.Root;
            Assert.Equal(root, Environment.GetEnvironmentVariable(StateDirScope.Variable));
            Assert.Equal(Path.Combine(root, "runs"), StatusIndex.Folder(name => Environment.GetEnvironmentVariable(name)));
            Assert.True(Directory.Exists(root));
        }

        Assert.Equal("/outer/state", Environment.GetEnvironmentVariable(StateDirScope.Variable));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_state_dir_scope_over_a_folder_of_the_test_leaves_the_folder_alone()
    {
        string mine = _dirs.Create("lw-guard-mine");

        using (StateDirScope scope = new(mine))
        {
            Assert.Equal(mine, scope.Root);
        }

        Assert.True(Directory.Exists(mine));
    }

    [Fact]
    public void Every_test_file_that_launches_in_this_process_or_in_a_child_points_the_run_index_at_a_folder_of_its_own()
    {
        string dir = SourceDir();
        Assert.SkipUnless(Directory.Exists(dir), "the test sources are not next to this build");
        string[] launching = [.. Directory.GetFiles(dir, "*.cs").Where(f => LaunchesALauncher(File.ReadAllText(f)))];

        Assert.True(launching.Length >= 10, "the scan found only " + launching.Length.ToString(CultureInfo.InvariantCulture) + " launching files; it looks at the wrong folder");
        string[] unguarded = [.. launching.Where(f => !IsolatesTheIndex(File.ReadAllText(f))).Select(f => Path.GetFileName(f))];
        Assert.Empty(unguarded);
    }

    // A file that starts the launcher: a call of Launcher.RunAsync, a LaunchRun, the public RunStatus.Open, the launcher's dll.
    private static bool LaunchesALauncher(string source)
    {
        string[] calls = ["Launcher.RunAsync(", "new LaunchRun(", "RunStatus.Open(", "\"LeanWorker.dll\""];
        return calls.Any(c => source.Contains(c, StringComparison.Ordinal));
    }

    // A file that gives the launcher its own index folder: a StateDirScope, or the variable itself.
    private static bool IsolatesTheIndex(string source) =>
        source.Contains("StateDirScope", StringComparison.Ordinal) || source.Contains(StateDirScope.Variable, StringComparison.Ordinal);
}
