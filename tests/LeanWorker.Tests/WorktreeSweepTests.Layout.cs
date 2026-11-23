using Xunit;

namespace LeanWorker.Tests;

// The sweep over the worktrees of the .worktrees/<repository>/<name> layout of --worktree (lean-worker#139): the
// main checkout is <root>/main, so the layout folder is <root>/.worktrees/main. Same fixture as the other sweep tests.
public sealed partial class WorktreeSweepTests
{
    private string LayoutFolder => Path.Combine(_root, ".worktrees", "main");

    /// <summary>
    /// <see cref="LandAsync"/> with a file of its own, so a branch cut after another one landed has something to commit.
    /// </summary>
    private async Task LandFileAsync(string branchDir, string file)
    {
        await CommitAsync(branchDir, file, file + "\n", "land " + file);
        _ = await GitAsync(_main, "merge", "--ff-only", Path.GetFileName(branchDir));
        _ = await GitAsync(_main, "push", "origin", "main");
    }

    [Fact]
    public async Task A_worktree_named_pinned_in_the_layout_folder_is_protected_and_staysAsync()
    {
        string pinned = await AddWorktreeAsync("pinned", LayoutFolder);

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"KEEP protected {pinned}", LineFor(lines, pinned));
        Assert.True(Directory.Exists(pinned));
    }

    [Fact]
    public async Task A_worktree_named_after_the_repository_and_pinned_stays_protected_in_both_layoutsAsync()
    {
        string beside = await AddWorktreeAsync("main-pinned", _root);
        string inLayout = await AddWorktreeAsync("job-pinned", LayoutFolder);

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"KEEP protected {beside}", LineFor(lines, beside));
        Assert.Equal($"KEEP protected {inLayout}", LineFor(lines, inLayout));
        Assert.True(Directory.Exists(beside));
        Assert.True(Directory.Exists(inLayout));
    }

    [Fact]
    public async Task A_folder_that_only_ends_in_pinned_without_a_dash_is_not_protectedAsync()
    {
        string wt = await AddWorktreeAsync("unpinned", LayoutFolder);

        Assert.Equal($"REMOVE merged {wt}", LineFor(await SweepAsync(OnMain()), wt));
    }

    [Fact]
    public async Task A_merged_clean_old_worktree_in_the_layout_folder_is_classed_like_the_same_one_next_to_the_repositoryAsync()
    {
        string first = await AddWorktreeAsync("job-a", LayoutFolder);
        await LandAsync(first);
        string second = await AddWorktreeAsync("job-b", LayoutFolder);
        await LandFileAsync(second, "second.txt");
        string beside = await AddWorktreeAsync("main-job-c", _root);
        await LandFileAsync(beside, "beside.txt");

        string[] plan = await SweepAsync(OnMain());

        Assert.Equal($"REMOVE merged {first}", LineFor(plan, first));
        Assert.Equal($"REMOVE merged {second}", LineFor(plan, second));
        Assert.Equal($"REMOVE merged {beside}", LineFor(plan, beside));
        Assert.Equal("total: 3 worktrees, 3 removable, 0 kept", plan[^1]);

        _ = await SweepAsync(OnMain("--apply"));

        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
        Assert.False(Directory.Exists(beside));
    }

    [Fact]
    public async Task A_worktree_in_the_layout_folder_is_kept_for_its_own_reasonAsync()
    {
        string unmerged = await AddWorktreeAsync("work", LayoutFolder);
        await CommitAsync(unmerged, "wip.txt", "wip\n", "wip");
        string dirty = await AddWorktreeAsync("edited", LayoutFolder);
        await File.WriteAllTextAsync(Path.Combine(dirty, "README.md"), "changed\n", TestContext.Current.CancellationToken);

        string[] lines = await SweepAsync(OnMain());

        Assert.Equal($"KEEP unmerged {unmerged}", LineFor(lines, unmerged));
        Assert.Equal($"KEEP dirty {dirty}", LineFor(lines, dirty));
    }

    [Fact]
    public async Task With_a_root_the_worktrees_folder_is_not_taken_for_a_repository_and_every_worktree_is_listed_onceAsync()
    {
        string pinned = await AddWorktreeAsync("pinned", LayoutFolder);
        string done = await AddWorktreeAsync("done", LayoutFolder);
        await LandAsync(done);

        string[] lines = await SweepAsync("--root", _root, "--older-than-days", "0");

        Assert.Equal($"KEEP protected {pinned}", LineFor(lines, pinned));
        Assert.Equal($"REMOVE merged {done}", LineFor(lines, done));
        Assert.Equal("total: 2 worktrees, 1 removable, 1 kept", lines[^1]);
        Assert.Equal(3, lines.Length);
    }
}
