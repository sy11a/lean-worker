using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// What Program does with the new arguments, in a real child process: a bad --owner is a launch error with exit 2
/// and a clean message, and `status` reads its caller's identity and the run index from the process's own environment.
/// </summary>
[Collection("launcher-process-state")]
public sealed class LauncherProgramProcessTests : IDisposable
{
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(60);

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private async Task<LauncherProcess.Result> RunAsync(string[] args, params (string Name, string? Value)[] env)
    {
        System.Diagnostics.ProcessStartInfo psi = LauncherProcess.StartInfo(args, _dirs.Create("lw-prog-cwd"), _dirs.Create("lw-prog-state"), env);
        LauncherProcess.Result result = await LauncherProcess.RunAsync(psi, _limit);
        return result;
    }

    [Fact]
    public async Task A_launch_with_a_bad_owner_key_exits_2_with_a_clean_message_and_starts_nothingAsync()
    {
        string root = _dirs.NewRoot();
        string state = _dirs.Create("lw-prog-state2");
        string key = (char)27 + "[31mred" + (char)27 + "[0m";
        System.Diagnostics.ProcessStartInfo psi = LauncherProcess.StartInfo(["--task", Path.Combine(root, "task.md"), "--runs-root", root, "--owner", key], root, state);

        LauncherProcess.Result result = await LauncherProcess.RunAsync(psi, _limit);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("LEAN-WORKER LAUNCH FAILED: --owner needs a key", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(TerminalAssert.PlainProblems(result.Stdout));
        Assert.Equal(string.Empty, result.Stderr);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
        Assert.False(Directory.Exists(Path.Combine(state, "runs")));
    }

    [Fact]
    public async Task Status_whoami_key_prints_the_session_key_of_the_process_environmentAsync()
    {
        LauncherProcess.Result result = await RunAsync(["status", "--whoami", "--key"], ("CLAUDE_CODE_SESSION_ID", "abc-1"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("claude-session:abc-1", result.Stdout.TrimEnd());
    }

    [Fact]
    public async Task Status_without_a_scope_and_without_an_identity_exits_2_with_the_no_identity_messageAsync()
    {
        LauncherProcess.Result result = await RunAsync(["status"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("lean-worker status: cannot tell which session you are; pass --owner, --here or --all", result.Stdout.TrimEnd());
    }

    [Fact]
    public async Task Status_all_reads_the_index_the_environment_names_and_lists_what_a_pointer_namesAsync()
    {
        string state = _dirs.Create("lw-prog-state3");
        string index = Path.Combine(state, "runs");
        _ = Directory.CreateDirectory(index);
        string gone = Path.Combine(_dirs.Create("lw-prog-runs"), "20261122-115000-vanished");
        JsonObject launcher = new() { ["pid"] = 1, ["start_ticks"] = 1, ["boot_id"] = "not-this-boot" };
        string pointer = StatusIndexRig.Pointer(gone, "c", parent: null, ["claude-session:abc-1"], launcher, DateTimeOffset.UtcNow).ToJsonString();
        await File.WriteAllTextAsync(Path.Combine(index, "20261122-115000-1-r1.json"), pointer, TestContext.Current.CancellationToken);
        System.Diagnostics.ProcessStartInfo psi = LauncherProcess.StartInfo(["status", "--all", "--since", "9999d"], _dirs.Create("lw-prog-cwd"), state);

        System.Diagnostics.ProcessStartInfo mineInfo = LauncherProcess.StartInfo(["status"], _dirs.Create("lw-prog-cwd"), state, ("CLAUDE_CODE_SESSION_ID", "abc-1"));

        LauncherProcess.Result all = await LauncherProcess.RunAsync(psi, _limit);
        LauncherProcess.Result mine = await LauncherProcess.RunAsync(mineInfo, _limit);

        Assert.Equal(0, all.ExitCode);
        string[] lines = TerminalAssert.AssertPlain(all.Stdout, 1);
        Assert.StartsWith("lean-worker status: all runs in " + index + ", 0 live, 1 ended", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^gone\s+vanished\s", lines[1]);
        Assert.Equal(0, mine.ExitCode);
        Assert.Equal("lean-worker status: owner cc:abc-1, 0 live, 1 ended, 0 need attention", StatusIndexRig.Lines(mine.Stdout)[0]);
    }
}
