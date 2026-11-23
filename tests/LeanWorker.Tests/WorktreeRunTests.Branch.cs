using Xunit;

namespace LeanWorker.Tests;

// The branch git made before it refused the worktree (lean-worker#156): git makes the branch first and the worktree
// after, so a failed `git worktree add -b` leaves the branch behind. The launcher deletes it again only when it is
// provably the one this call made, and says what became of it. Same fixture as the other worktree tests: the helpers
// are theirs (RefusedAsync, StalePathAsync below is RefusedByGitAsync's way of making git refuse the add).
public sealed partial class WorktreeRunTests
{
    private string FakeGitFolder => _dirs.Create("lw-fakegit");

    /// <summary>
    /// Makes <c>git worktree add</c> refuse the path of the launch after it made the branch: the path is registered
    /// with git but its folder is gone ("a missing but already registered worktree"). The layout folders are removed
    /// as well, so only the launch makes them.
    /// </summary>
    private async Task StalePathAsync()
    {
        _ = await GitAsync(_repo, "worktree", "add", "-b", "stale-branch", WorktreePath, "HEAD");
        Directory.Delete(WorktreePath, recursive: true);
        Directory.Delete(RepoFolder);
        Directory.Delete(WorktreesFolder);
    }

    private async Task<string> RefsAsync() =>
        (await GitAsync(_repo, "for-each-ref", "--format=%(refname) %(objectname) %(symref)")).ReplaceLineEndings("\n");

    private async Task<string> WorktreesAsync() =>
        (await GitAsync(_repo, "worktree", "list", "--porcelain")).ReplaceLineEndings("\n");

    private async Task<string> ShaAsync(string rev) =>
        (await GitAsync(_repo, "rev-parse", "--verify", rev)).ToLowerInvariant();

    /// <summary>
    /// A commit that is not the base: the same tree on top of the base.
    /// </summary>
    private async Task<string> OtherCommitAsync()
    {
        string head = await ShaAsync("HEAD");
        return await GitAsync(_repo, "commit-tree", head + "^{tree}", "-p", head, "-m", "other");
    }

    /// <summary>
    /// A detached worktree of the repository outside the layout, locked or not.
    /// </summary>
    private async Task<string> DetachedWorktreeAsync(bool locked)
    {
        string path = Path.Combine(_root, "elsewhere");
        _ = await GitAsync(_repo, "worktree", "add", "--detach", path, "HEAD");
        if (locked)
        {
            _ = await GitAsync(_repo, "worktree", "lock", path);
        }

        return path;
    }

    private static string Stays(string why) => $"; the branch {Branch} stays: {why}";

    private void AssertLayoutGone()
    {
        Assert.False(Directory.Exists(RepoFolder));
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    /// <summary>
    /// The launch of the fixture with the git of <paramref name="before"/> and <paramref name="after"/> in front of the
    /// real one for as long as the launch runs.
    /// </summary>
    private async Task<LaunchException> RefusedWithGitAsync(string before = ":", string after = ":")
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        using GitInterposer git = new(FakeGitFolder, _repo, before, after);
        return await RefusedAsync();
    }

    // ---- the branch git made is deleted again ----

    [Fact]
    public async Task A_branch_git_made_before_it_refused_the_worktree_is_deleted_again_and_the_same_launch_works_after_the_causeAsync()
    {
        await StalePathAsync();
        string refsBefore = await RefsAsync();

        LaunchException ex = await RefusedAsync();

        Assert.StartsWith("git could not create the worktree: ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith($"; the branch {Branch} git made for it was deleted again", ex.Message, StringComparison.Ordinal);
        Assert.False(await BranchExistsAsync(Branch));
        Assert.Equal(refsBefore, await RefsAsync());
        AssertLayoutGone();

        // The cause goes (git forgets the registered path whose folder is gone): the same launch works.
        _ = await GitAsync(_repo, "worktree", "prune");
        WorktreeRun again = await CreateRunAsync(Branch, RunName);

        Assert.Equal(WorktreePath, again.WorktreePath);
        Assert.Equal(await ShaAsync("HEAD"), await ShaAsync("refs/heads/" + Branch));
    }

    [Fact]
    public async Task A_branch_deleted_again_leaves_every_other_branch_and_worktree_where_they_wereAsync()
    {
        await StalePathAsync();
        _ = await GitAsync(_repo, "branch", "other-branch", "HEAD");
        string elsewhere = await DetachedWorktreeAsync(locked: false);
        string worktreesBefore = await WorktreesAsync();

        LaunchException ex = await RefusedAsync();

        Assert.EndsWith("was deleted again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        Assert.True(await BranchExistsAsync("other-branch"));
        Assert.True(await BranchExistsAsync("stale-branch"));
        Assert.True(Directory.Exists(elsewhere));
        AssertLayoutGone();
    }

    // ---- the branch existed before: refused, untouched ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_that_existed_before_the_launch_is_refused_as_taken_and_is_left_as_it_isAsync(bool pathIsRegistered)
    {
        string first = await ShaAsync("HEAD");
        _ = await GitAsync(_repo, "branch", Branch, first);
        await CommitAsync(_repo, "more.txt", "more\n", "more");
        if (pathIsRegistered)
        {
            await StalePathAsync();
        }

        string refsBefore = await RefsAsync();

        LaunchException ex = await RefusedAsync();

        Assert.Equal($"the branch is taken: {Branch}", ex.Message);
        Assert.Equal(first, await ShaAsync("refs/heads/" + Branch));
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    // ---- git failed before it made a branch: no tail ----

    [Theory]
    [InlineData("a..b")]
    [InlineData("bad name")]
    [InlineData("x.lock")]
    public async Task A_branch_name_git_refuses_before_it_makes_anything_has_no_branch_tail_and_changes_nothingAsync(string branch)
    {
        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(branch, RunName));

        // Since lean-worker#166 `git check-ref-format --branch` refuses the name before the add: the launcher's own
        // message, git's answer in the brackets, and still no tail about a branch.
        Assert.StartsWith(GitRefusalStart(branch), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("; the branch", ex.Message, StringComparison.Ordinal);
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_branch_git_cannot_lock_has_no_branch_tail_and_the_lock_stays_where_it_isAsync()
    {
        string gitDir = await GitAsync(_repo, "rev-parse", "--absolute-git-dir");
        string heads = Path.Combine(gitDir, "refs", "heads");
        string lockFile = Path.Combine(heads, Branch + ".lock");
        await File.WriteAllTextAsync(lockFile, string.Empty, TestContext.Current.CancellationToken);
        string refsBefore = await RefsAsync();

        LaunchException ex = await RefusedAsync();

        Assert.StartsWith("git could not create the worktree: ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("; the branch", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(lockFile));
        Assert.False(await BranchExistsAsync(Branch));
        Assert.Equal(refsBefore, await RefsAsync());
        AssertLayoutGone();
    }

    // ---- the branch stays, and the error says why ----

    [Fact]
    public async Task A_locked_worktree_without_a_branch_keeps_the_branch_and_the_error_names_itAsync()
    {
        await StalePathAsync();
        string elsewhere = await DetachedWorktreeAsync(locked: true);

        LaunchException ex = await RefusedAsync();

        Assert.EndsWith(Stays($"the locked worktree {elsewhere} has no branch yet (git may still be creating it)"), ex.Message, StringComparison.Ordinal);
        Assert.Equal(await ShaAsync("HEAD"), await ShaAsync("refs/heads/" + Branch));
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_detached_worktree_that_is_not_locked_does_not_keep_the_branchAsync()
    {
        await StalePathAsync();
        _ = await DetachedWorktreeAsync(locked: false);

        LaunchException ex = await RefusedAsync();

        Assert.EndsWith("was deleted again", ex.Message, StringComparison.Ordinal);
        Assert.False(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task A_locked_worktree_that_has_a_branch_does_not_keep_the_branchAsync()
    {
        await StalePathAsync();
        string path = Path.Combine(_root, "elsewhere");
        _ = await GitAsync(_repo, "worktree", "add", "-b", "locked-branch", path, "HEAD");
        _ = await GitAsync(_repo, "worktree", "lock", path);

        LaunchException ex = await RefusedAsync();

        Assert.EndsWith("was deleted again", ex.Message, StringComparison.Ordinal);
        Assert.False(await BranchExistsAsync(Branch));
        Assert.True(await BranchExistsAsync("locked-branch"));
    }

    [Fact]
    public async Task A_worktree_whose_head_names_the_branch_before_it_exists_keeps_the_branchAsync()
    {
        // An unborn branch: the probe finds no ref, git then makes it, and the worktree has been "on" it all along.
        await StalePathAsync();
        string elsewhere = await DetachedWorktreeAsync(locked: false);
        _ = await GitAsync(elsewhere, "symbolic-ref", "HEAD", "refs/heads/" + Branch);

        LaunchException ex = await RefusedAsync();

        Assert.EndsWith(Stays($"the worktree {elsewhere} has it checked out"), ex.Message, StringComparison.Ordinal);
        Assert.Equal(await ShaAsync("HEAD"), await ShaAsync("refs/heads/" + Branch));
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_symbolic_ref_in_the_place_of_the_branch_is_refused_before_the_add_and_the_branch_it_points_to_is_not_madeAsync()
    {
        // A dangling symbolic ref reads as "no such branch" to the rev-parse probe; since lean-worker#166 the
        // symbolic-ref probe after it refuses the launch, so git never writes through the link (it used to make the
        // branch the link points to, and the error said so).
        await StalePathAsync();
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/" + Branch, "refs/heads/gone");
        string refsBefore = await RefsAsync();

        LaunchException ex = await RefusedAsync();

        Assert.Equal($"the branch is taken: {Branch} (it is a symbolic ref to refs/heads/gone)", ex.Message);
        Assert.Equal("refs/heads/gone", await GitAsync(_repo, "symbolic-ref", "refs/heads/" + Branch));
        Assert.False(await BranchExistsAsync("gone"));
        Assert.Equal(refsBefore, await RefsAsync());
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_branch_that_moved_after_git_made_it_is_kept_and_the_error_says_where_it_pointsAsync()
    {
        await StalePathAsync();
        string other = await OtherCommitAsync();
        string baseSha = await ShaAsync("HEAD");

        LaunchException ex = await RefusedWithGitAsync(after: $"case \" $* \" in *\" worktree add \"*) \"$real\" -C \"$repo\" update-ref refs/heads/{Branch} {other} >/dev/null 2>&1;; esac");

        Assert.EndsWith(Stays($"it points at {other}, not at the base {baseSha}"), ex.Message, StringComparison.Ordinal);
        Assert.Equal(other, await ShaAsync("refs/heads/" + Branch));
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_worktree_that_got_the_branch_checked_out_after_git_made_it_keeps_itAsync()
    {
        await StalePathAsync();
        string elsewhere = Path.Combine(_root, "elsewhere");

        LaunchException ex = await RefusedWithGitAsync(after: $"case \" $* \" in *\" worktree add \"*) \"$real\" -C \"$repo\" worktree add '{elsewhere}' {Branch} >/dev/null 2>&1;; esac");

        Assert.EndsWith(Stays($"the worktree {elsewhere} has it checked out"), ex.Message, StringComparison.Ordinal);
        Assert.Equal(await ShaAsync("HEAD"), await ShaAsync("refs/heads/" + Branch));
        Assert.True(Directory.Exists(elsewhere));
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_branch_that_moves_between_the_check_and_the_delete_is_not_deleted_and_the_error_says_soAsync()
    {
        await StalePathAsync();
        string other = await OtherCommitAsync();

        LaunchException ex = await RefusedWithGitAsync(before: $"case \" $* \" in *\" update-ref \"*) \"$real\" -C \"$repo\" update-ref refs/heads/{Branch} {other} >/dev/null 2>&1;; esac");

        Assert.Contains(Stays("git did not delete it ("), ex.Message, StringComparison.Ordinal);
        Assert.Equal(other, await ShaAsync("refs/heads/" + Branch));
    }

    [Fact]
    public async Task A_git_that_cannot_list_the_branch_keeps_it_and_the_error_says_soAsync()
    {
        await StalePathAsync();

        LaunchException ex = await RefusedWithGitAsync(before: "case \" $* \" in *\" for-each-ref \"*) echo 'no refs today' >&2; exit 3;; esac");

        Assert.EndsWith($"; the branch {Branch} is left as it is: git could not list it (no refs today)", ex.Message, StringComparison.Ordinal);
        Assert.True(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task A_git_that_could_not_tell_whether_the_branch_existed_before_keeps_the_branch_it_found_afterAsync()
    {
        await StalePathAsync();

        LaunchException ex = await RefusedWithGitAsync(before: $"case \" $* \" in *\" rev-parse --verify --quiet refs/heads/{Branch} \"*) echo 'no probe today' >&2; exit 128;; esac");

        Assert.EndsWith(Stays("git could not tell whether it existed before the launch"), ex.Message, StringComparison.Ordinal);
        Assert.Equal(await ShaAsync("HEAD"), await ShaAsync("refs/heads/" + Branch));
    }

    [Fact]
    public async Task A_git_that_cannot_list_the_worktrees_keeps_the_branch_and_the_error_says_soAsync()
    {
        await StalePathAsync();

        LaunchException ex = await RefusedWithGitAsync(before: "case \" $* \" in *\" worktree list \"*) echo 'no list today' >&2; exit 3;; esac");

        Assert.EndsWith(Stays("git could not list the worktrees (no list today)"), ex.Message, StringComparison.Ordinal);
        Assert.True(await BranchExistsAsync(Branch));
    }

    // ---- names that look like an option or a path ----

    [Theory]
    [InlineData("-x")]
    [InlineData("--force")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("-b")]
    public async Task A_branch_name_that_looks_like_an_option_is_refused_by_the_launcher_and_changes_no_ref_of_the_repositoryAsync(string branch)
    {
        string refsBefore = await RefsAsync();
        string baseSha = await ShaAsync("HEAD");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(branch, RunName));

        // Since lean-worker#166 the launcher refuses the name itself, before git sees it: git no longer reads
        // "--force" as "git branch --force <base>" and makes no branch named after the base commit (it did, and the
        // row of "--force" pinned that stray branch).
        Assert.Equal(DashRefusal(branch), ex.Message);
        Assert.False(await BranchExistsAsync(branch));
        Assert.False(await BranchExistsAsync(baseSha));
        Assert.Equal(refsBefore, await RefsAsync());
        AssertLayoutGone();
    }

    [Fact]
    public async Task A_branch_with_a_slash_is_deleted_again_and_its_siblings_stayAsync()
    {
        await StalePathAsync();
        _ = await GitAsync(_repo, "branch", "a/c", "HEAD");
        _ = await GitAsync(_repo, "branch", "a-b", "HEAD");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync("a/b", RunName));

        Assert.EndsWith("; the branch a/b git made for it was deleted again", ex.Message, StringComparison.Ordinal);
        Assert.False(await BranchExistsAsync("a/b"));
        Assert.True(await BranchExistsAsync("a/c"));
        Assert.True(await BranchExistsAsync("a-b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_that_is_the_prefix_of_another_one_is_refused_by_git_and_the_other_one_staysAsync(bool pathIsRegistered)
    {
        _ = await GitAsync(_repo, "branch", "x/y", "HEAD");
        if (pathIsRegistered)
        {
            await StalePathAsync();
        }

        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync("x", RunName));

        // git cannot make "x" next to "x/y": it made nothing, so there is no tail, and the probe of the launcher
        // (which lists "x/y" under the prefix "x") never takes it for the branch of this launch.
        Assert.StartsWith("git could not create the worktree: ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("; the branch", ex.Message, StringComparison.Ordinal);
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        AssertLayoutGone();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_below_an_existing_branch_is_refused_by_git_and_the_existing_one_staysAsync(bool pathIsRegistered)
    {
        _ = await GitAsync(_repo, "branch", "x", "HEAD");
        if (pathIsRegistered)
        {
            await StalePathAsync();
        }

        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync("x/y", RunName));

        Assert.StartsWith("git could not create the worktree: ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("; the branch", ex.Message, StringComparison.Ordinal);
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        AssertLayoutGone();
    }
}
