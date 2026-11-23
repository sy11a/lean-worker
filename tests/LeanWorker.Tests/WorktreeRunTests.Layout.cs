using Xunit;

namespace LeanWorker.Tests;

// The layout of the worktree of a run (lean-worker#139): <parent>/.worktrees/<repository>/<name>, the folders made
// for it and removed again, the refusal of a link or a file in their place, and the continue path of a worktree an
// earlier launcher put next to the repository. Same fixture as the other worktree tests: the helpers are theirs.
public sealed partial class WorktreeRunTests
{
    private string WorktreesFolder => Path.Combine(_root, ".worktrees");

    private string RepoFolder => Path.Combine(WorktreesFolder, "repo");

    private string OldPath => Path.Combine(_root, "repo-" + RunName);

    private async Task<WorktreeRun> CreateRunAsync(string branch, string name)
    {
        WorktreeRun run = await WorktreeRun.CreateAsync(_repo, branch, "HEAD", name, _setup);
        Assert.True(Directory.Exists(run.WorktreePath));
        return run;
    }

    private async Task<LaunchException> RefusedAsync()
    {
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(Branch, RunName));
        Assert.NotEmpty(ex.Message);
        return ex;
    }

    private async Task AssertNoWorktreeAsync()
    {
        Assert.False(Directory.Exists(WorktreePath));
        Assert.False(File.Exists(WorktreePath));
        Assert.False(await BranchExistsAsync(Branch));
        Assert.DoesNotContain(WorktreePath, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
    }

    // ---- creating ----

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task A_launch_with_a_name_that_is_no_folder_name_is_refused_and_creates_nothingAsync(string name)
    {
        _ = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(Branch, name));

        Assert.False(Directory.Exists(WorktreesFolder));
        Assert.False(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task A_launch_makes_the_repository_folder_below_worktrees_and_leaves_nothing_next_to_the_repositoryAsync()
    {
        WorktreeRun wt = await CreateRunAsync(Branch, RunName);

        Assert.Equal(WorktreePath, wt.WorktreePath);
        Assert.True(Directory.Exists(RepoFolder));
        Assert.True(Directory.Exists(WorktreePath));
        Assert.Contains(WorktreePath, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(OldPath));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), e => Path.GetFileName(e).StartsWith("repo-", StringComparison.Ordinal));
        Assert.Equal([".worktrees", "origin.git", "repo"], Directory.GetFileSystemEntries(_root).Select(e => Path.GetFileName(e)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    // ---- removing the folders again ----

    [Fact]
    public async Task A_clean_run_that_removed_its_worktree_leaves_neither_the_repository_folder_nor_worktreesAsync()
    {
        WorktreeRun wt = await CreateRunAsync(Branch, RunName);

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.False(Directory.Exists(RepoFolder));
        Assert.False(Directory.Exists(WorktreesFolder));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_clean_launch_through_the_launcher_leaves_neither_the_repository_folder_nor_worktreesAsync()
    {
        (int code, _, _) = await LaunchAsync(Success(), InWorktree());

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_repository_folder_that_holds_another_worktree_stays_and_worktrees_with_itAsync()
    {
        WorktreeRun first = await CreateRunAsync(Branch, RunName);
        WorktreeRun second = await CreateRunAsync("other-branch", "other");

        await first.FinishAsync();

        Assert.Equal("removed", first.Action);
        Assert.False(Directory.Exists(first.WorktreePath));
        Assert.True(Directory.Exists(second.WorktreePath));
        Assert.True(Directory.Exists(RepoFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_repository_folder_that_holds_another_file_stays_and_worktrees_with_itAsync()
    {
        WorktreeRun wt = await CreateRunAsync(Branch, RunName);
        WriteFile(RepoFolder, "notes.txt", "mine\n");

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Path.Combine(RepoFolder, "notes.txt"), TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_worktrees_folder_that_holds_another_repositorys_folder_stays_and_only_the_repository_folder_goesAsync()
    {
        WorktreeRun wt = await CreateRunAsync(Branch, RunName);
        WriteFile(Path.Combine(WorktreesFolder, "another-repo"), "wt/x.txt", "x\n");

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.False(Directory.Exists(RepoFolder));
        Assert.Equal("x\n", await File.ReadAllTextAsync(Path.Combine(WorktreesFolder, "another-repo", "wt", "x.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_worktree_that_is_kept_leaves_its_folders_where_they_areAsync()
    {
        WorktreeRun wt = await CreateRunAsync(Branch, RunName);
        WriteFile(wt.WorktreePath, "work.txt", "work\n");

        await AssertKeptAsync(wt, "changes");

        Assert.True(Directory.Exists(RepoFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    // ---- a link or a file in the place of a folder ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_symlink_in_the_place_of_a_worktree_folder_refuses_the_launch_and_nothing_is_made_behind_itAsync(bool atRepositoryFolder, bool dangling)
    {
        SkipWithoutSymlinks();
        string behind = Path.Combine(_root, "behind");
        if (!dangling)
        {
            _ = Directory.CreateDirectory(behind);
        }

        string link = atRepositoryFolder ? RepoFolder : WorktreesFolder;
        _ = Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        _ = Directory.CreateSymbolicLink(link, behind);

        LaunchException ex = await RefusedAsync();

        Assert.Contains("symlink", ex.Message, StringComparison.Ordinal);
        Assert.Equal(behind, new DirectoryInfo(link).LinkTarget);
        Assert.False(dangling ? Directory.Exists(behind) : Directory.GetFileSystemEntries(behind).Length > 0);
        await AssertNoWorktreeAsync();
    }

    [Fact]
    public async Task A_launch_whose_worktrees_folder_is_a_symlink_is_refused_before_the_worker_runsAsync()
    {
        SkipWithoutSymlinks();
        string behind = Path.Combine(_root, "behind");
        _ = Directory.CreateDirectory(behind);
        _ = Directory.CreateSymbolicLink(WorktreesFolder, behind);
        string workerMarker = Path.Combine(_root, "worker-ran");

        _ = await Assert.ThrowsAsync<LaunchException>(async () => await LaunchAsync(Success("touch '" + workerMarker + "'"), InWorktree()));

        Assert.False(File.Exists(workerMarker), "the worker must not run");
        Assert.Empty(Directory.GetFileSystemEntries(behind));
        Assert.False(await BranchExistsAsync(Branch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_in_the_place_of_a_worktree_folder_refuses_the_launch_and_is_left_as_it_wasAsync(bool atRepositoryFolder)
    {
        string file = atRepositoryFolder ? RepoFolder : WorktreesFolder;
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "mine\n", TestContext.Current.CancellationToken);

        LaunchException ex = await RefusedAsync();

        Assert.Contains("is a file", ex.Message, StringComparison.Ordinal);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        await AssertNoWorktreeAsync();
    }

    // ---- git refuses after the folders were made ----

    /// <summary>
    /// A launch whose <c>git worktree add</c> fails: the path is registered with git but its folder is gone, so git
    /// refuses it, after the folders of the layout were made. They are removed first, so only the launch makes them.
    /// </summary>
    private async Task<LaunchException> RefusedByGitAsync(Action? beforeLaunch = null)
    {
        _ = await GitAsync(_repo, "worktree", "add", "-b", "stale-branch", WorktreePath, "HEAD");
        Directory.Delete(WorktreePath, recursive: true);
        Directory.Delete(RepoFolder);
        Directory.Delete(WorktreesFolder);
        beforeLaunch?.Invoke();

        LaunchException ex = await RefusedAsync();

        Assert.Contains("git could not create the worktree", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(WorktreePath));
        return ex;
    }

    [Fact]
    public async Task A_git_that_refuses_the_worktree_leaves_neither_the_repository_folder_nor_worktreesAsync()
    {
        _ = await RefusedByGitAsync();

        Assert.False(Directory.Exists(RepoFolder));
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_git_that_refuses_the_worktree_leaves_a_repository_folder_that_holds_something_and_worktreesAsync()
    {
        _ = await RefusedByGitAsync(() => WriteFile(RepoFolder, "notes.txt", "mine\n"));

        Assert.Equal("mine\n", await File.ReadAllTextAsync(Path.Combine(RepoFolder, "notes.txt"), TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_git_that_refuses_the_worktree_leaves_worktrees_when_another_repository_has_a_folder_thereAsync()
    {
        _ = await RefusedByGitAsync(() => WriteFile(Path.Combine(WorktreesFolder, "another-repo"), "x.txt", "x\n"));

        Assert.False(Directory.Exists(RepoFolder));
        Assert.True(File.Exists(Path.Combine(WorktreesFolder, "another-repo", "x.txt")));
    }

    // ---- the continue path ----

    private async Task<string> OldLayoutWorktreeAsync(string branch)
    {
        _ = await GitAsync(_repo, "worktree", "add", "-b", branch, OldPath, "HEAD");
        return OldPath;
    }

    private async Task<WorktreeRun> ReuseAsync()
    {
        WorktreeRun run = await WorktreeRun.ReuseAsync(_repo, Branch, "HEAD", RunName, _setup);
        Assert.True(Directory.Exists(run.WorktreePath));
        return run;
    }

    private async Task AssertGoneAsync()
    {
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await ReuseAsync());

        Assert.Equal($"the worktree of the previous round is gone: {WorktreePath}", ex.Message);
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_that_git_lists_with_the_branch_is_continued_in_place_with_a_noteAsync()
    {
        string old = await OldLayoutWorktreeAsync(Branch);

        WorktreeRun wt = await ReuseAsync();

        Assert.Equal(old, wt.WorktreePath);
        Assert.NotNull(wt.ContinueNote);
        Assert.Contains("old layout", wt.ContinueNote, StringComparison.Ordinal);
        Assert.Contains(old, wt.ContinueNote, StringComparison.Ordinal);
        Assert.Contains(WorktreePath, wt.ContinueNote, StringComparison.Ordinal);
        Assert.False(Directory.Exists(WorktreesFolder));
        Assert.Contains(old, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_continued_worktree_of_the_old_layout_is_removed_when_empty_and_no_layout_folder_appearsAsync()
    {
        string old = await OldLayoutWorktreeAsync(Branch);
        WorktreeRun wt = await ReuseAsync();

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.False(Directory.Exists(old));
        Assert.False(await BranchExistsAsync(Branch));
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_worktree_at_the_new_path_is_continued_there_without_a_noteAsync()
    {
        WorktreeRun first = await CreateRunAsync(Branch, RunName);

        WorktreeRun again = await ReuseAsync();

        Assert.Equal(first.WorktreePath, again.WorktreePath);
        Assert.Null(again.ContinueNote);
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_is_not_continued_when_a_folder_is_at_the_new_pathAsync()
    {
        string old = await OldLayoutWorktreeAsync(Branch);
        WriteFile(WorktreePath, "mine.txt", "mine\n");

        WorktreeRun wt = await ReuseAsync();

        // The folder at the new path is what the round continues in: the old worktree is not taken instead.
        Assert.Equal(WorktreePath, wt.WorktreePath);
        Assert.Null(wt.ContinueNote);
        await wt.FinishAsync();
        Assert.Equal("kept", wt.Action);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Path.Combine(WorktreePath, "mine.txt"), TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_is_not_continued_when_a_file_is_at_the_new_pathAsync()
    {
        string old = await OldLayoutWorktreeAsync(Branch);
        WriteFile(RepoFolder, RunName, "mine\n");

        await AssertGoneAsync();

        Assert.Equal("mine\n", await File.ReadAllTextAsync(WorktreePath, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_is_not_continued_when_a_symlink_is_at_the_new_pathAsync()
    {
        SkipWithoutSymlinks();
        string old = await OldLayoutWorktreeAsync(Branch);
        _ = Directory.CreateDirectory(RepoFolder);
        _ = Directory.CreateSymbolicLink(WorktreePath, Path.Combine(_root, "nowhere"));

        await AssertGoneAsync();

        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_whose_path_is_a_symlink_is_not_continuedAsync()
    {
        SkipWithoutSymlinks();
        string old = await OldLayoutWorktreeAsync(Branch);
        string moved = old + "-moved";
        Directory.Move(old, moved);
        _ = Directory.CreateSymbolicLink(old, moved);

        await AssertGoneAsync();

        Assert.True(File.Exists(Path.Combine(moved, ".git")));
        Assert.Equal(moved, new DirectoryInfo(old).LinkTarget);
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_on_another_branch_is_not_continuedAsync()
    {
        string old = await OldLayoutWorktreeAsync("other-branch");

        await AssertGoneAsync();

        Assert.True(Directory.Exists(old));
        Assert.True(await BranchExistsAsync("other-branch"));
    }

    [Fact]
    public async Task A_folder_of_the_old_layout_that_is_no_worktree_is_not_continuedAsync()
    {
        WriteFile(OldPath, "mine.txt", "mine\n");

        await AssertGoneAsync();

        Assert.True(File.Exists(Path.Combine(OldPath, "mine.txt")));
    }

    [Fact]
    public async Task A_worktree_of_the_old_layout_that_belongs_to_another_repository_is_not_continuedAsync()
    {
        string other = Path.Combine(_root, "other");
        _ = await GitAsync(_root, "init", "-b", "main", other);
        _ = await GitAsync(other, "config", "user.name", "Test");
        _ = await GitAsync(other, "config", "user.email", "test@example.com");
        _ = await GitAsync(other, "config", "commit.gpgsign", "false");
        await CommitAsync(other, "a.txt", "a\n", "a");
        _ = await GitAsync(other, "worktree", "add", "-b", Branch, OldPath, "HEAD");

        await AssertGoneAsync();

        Assert.Contains(OldPath, await GitAsync(other, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.DoesNotContain(OldPath, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worktree_that_is_gone_in_both_layouts_is_the_gone_errorAsync()
    {
        await AssertGoneAsync();

        Assert.False(Directory.Exists(WorktreesFolder));
    }
}
