using Xunit;

namespace LeanWorker.Tests;

// The refusal of the --worktree name through the command line (lean-worker#166): a real launcher process in the
// invoking repository, the exit code and the line Program prints, and what the refusal leaves behind compared with the
// refusal of a --worktree without --name.
public sealed partial class WorktreeRunTests
{
    private async Task<LauncherProcess.Result> LaunchProcessAsync(params string[] worktreeArgs)
    {
        WriteFile(_setup, "task.md", "do nothing");
        string[] args = ["--task", Path.Combine(_setup, "task.md"), "--runs-root", _setup, .. worktreeArgs];
        System.Diagnostics.ProcessStartInfo psi = LauncherProcess.StartInfo(args, _repo, _dirs.Create("lw-wtname-state"));
        LauncherProcess.Result result = await LauncherProcess.RunAsync(psi, TimeSpan.FromSeconds(60));
        return result;
    }

    private string[] SetupEntries() =>
        [.. Directory.GetFileSystemEntries(_setup, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(_setup, e)).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData("-x")]
    [InlineData("--force")]
    [InlineData("a..b")]
    [InlineData("bad name")]
    [InlineData("")]
    public async Task A_refused_branch_name_is_a_launch_error_with_the_exit_code_of_the_other_worktree_refusalsAsync(string branch)
    {
        WriteFile(_setup, "task.md", "do nothing");
        string[] entriesBefore = SetupEntries();
        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();
        LauncherProcess.Result reference = await LaunchProcessAsync("--worktree", Branch);
        string[] entriesAfterReference = SetupEntries();

        LauncherProcess.Result result = await LaunchProcessAsync("--worktree", branch, "--name", RunName);

        Assert.StartsWith("LEAN-WORKER LAUNCH FAILED: --worktree needs --name", reference.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, reference.ExitCode);
        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith($"LEAN-WORKER LAUNCH FAILED: --worktree '{branch}' cannot name a branch: ", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(entriesBefore, entriesAfterReference);
        Assert.Equal(entriesAfterReference, SetupEntries());
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        AssertLayoutGone();
    }

    [Fact]
    public async Task The_previous_branch_spelled_as_at_minus_one_is_a_launch_error_through_the_command_line_and_that_branch_is_untouchedAsync()
    {
        (string previous, _) = await CheckOutTwoBranchesAsync();
        string previousSha = await ShaAsync("refs/heads/" + previous);
        WriteFile(_setup, "task.md", "do nothing");
        string[] entriesBefore = SetupEntries();
        string refsBefore = await RefsAsync();

        LauncherProcess.Result result = await LaunchProcessAsync("--worktree", "@{-1}", "--name", RunName);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("LEAN-WORKER LAUNCH FAILED: " + ReadsAs("@{-1}", previous), result.Stdout.TrimEnd());
        Assert.Equal(previousSha, await ShaAsync("refs/heads/" + previous));
        Assert.Equal(entriesBefore, SetupEntries());
        Assert.Equal(refsBefore, await RefsAsync());
        AssertLayoutGone();
    }
}
