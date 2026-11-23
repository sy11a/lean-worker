using Xunit;

namespace LeanWorker.Tests;

// The run's own removal of the layout folders when one of them became a symlink while the worktree lived
// (lean-worker#157 shares that removal with the sweep: both stay when either folder is a link).
public sealed partial class WorktreeRunTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_layout_folder_that_became_a_symlink_while_the_worktree_lived_leaves_the_worktree_and_every_folderAsync(bool worktreesIsTheLink)
    {
        SkipWithoutSymlinks();
        WorktreeRun wt = await CreateRunAsync(Branch, RunName);
        string behind = Path.Combine(_root, "behind");
        string link = worktreesIsTheLink ? WorktreesFolder : RepoFolder;
        Directory.Move(link, behind);
        _ = Directory.CreateSymbolicLink(link, behind);

        // The path of the worktree runs through a link now: the launcher cannot verify it and keeps it, so no folder
        // above it is looked at either.
        await AssertKeptAsync(wt, "changes");

        Assert.Contains("worktree path check failed", wt.Note, StringComparison.Ordinal);
        Assert.Equal(behind, new DirectoryInfo(link).LinkTarget);
        Assert.True(Directory.Exists(wt.WorktreePath));
        Assert.True(Directory.Exists(RepoFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }
}
