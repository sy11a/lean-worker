using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The path a --worktree run gets (lean-worker#139): <c>&lt;parent&gt;/.worktrees/&lt;repository&gt;/&lt;safe name&gt;</c>.
/// Nothing here touches git or the disk, so the paths are plain strings under the temp folder.
/// </summary>
public sealed class WorktreeRunLayoutTests
{
    private static readonly string _parent = Path.Combine(Path.GetTempPath(), "lw-pathfor");
    private static readonly string _repo = Path.Combine(_parent, "repo");

    [Fact]
    public void The_path_is_the_parent_folder_then_worktrees_then_the_repository_then_the_safe_name()
    {
        Assert.Equal(Path.Combine(_parent, ".worktrees", "repo", "a-b_c.d"), WorktreeRun.PathFor(_repo, "a b_c.d"));
        Assert.Equal(Path.Combine(_parent, ".worktrees", "repo", "job"), WorktreeRun.PathFor(_repo + Path.DirectorySeparatorChar, "job"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void A_name_that_is_no_folder_name_of_its_own_is_refused(string name)
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => WorktreeRun.PathFor(_repo, name));

        Assert.Contains("cannot name a worktree folder", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape", "..-escape")]
    [InlineData("a/b", "a-b")]
    [InlineData("...", "...")]
    public void A_name_with_separators_or_dots_stays_one_folder_below_the_repository_folder(string name, string folder)
    {
        string path = WorktreeRun.PathFor(_repo, name);

        Assert.Equal(Path.Combine(_parent, ".worktrees", "repo", folder), path);
        Assert.Equal(Path.Combine(_parent, ".worktrees", "repo"), Path.GetDirectoryName(path));
    }
}
