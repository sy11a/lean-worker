using Xunit;

namespace LeanWorker.Tests;

// The folders of the layout after `sweep-worktrees --apply` (lean-worker#157): when the sweep removed the last worktree
// of <root>/.worktrees/<repository>, that folder and an empty .worktrees go with it, the way a run's own removal takes
// them. Same fixture as the other sweep tests: the main checkout is <root>/main, so its layout folder is
// <root>/.worktrees/main.
public sealed partial class WorktreeSweepTests
{
    private string WorktreesFolder => Path.Combine(_root, ".worktrees");

    /// <summary>
    /// A worktree in the layout folder whose branch landed: the sweep may remove it.
    /// </summary>
    private async Task<string> LandedInLayoutAsync(string name)
    {
        string wt = await AddWorktreeAsync(name, LayoutFolder);
        await LandFileAsync(wt, name + ".txt");
        return wt;
    }

    /// <summary>
    /// A worktree in the layout folder with a commit of its own: the sweep keeps it as unmerged.
    /// </summary>
    private async Task<string> UnmergedInLayoutAsync(string name)
    {
        string wt = await AddWorktreeAsync(name, LayoutFolder);
        await CommitAsync(wt, name + ".txt", name + "\n", "work " + name);
        return wt;
    }

    /// <summary>
    /// A sweep with what it wrote to the standard output and to the standard error.
    /// </summary>
    private static async Task<(int Code, string Output, string Error)> RunSweepWithErrorAsync(params string[] args)
    {
        TextWriter oldError = Console.Error;
        await using StringWriter error = new();
        Console.SetError(error);
        try
        {
            (int code, _, string output) = await RunSweepAsync(args);
            return (code, output, error.ToString());
        }
        finally
        {
            Console.SetError(oldError);
        }
    }

    private static string[] LinesOf(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---- the folders go with the last worktree ----

    [Fact]
    public async Task The_last_worktree_of_the_layout_folder_takes_the_repository_folder_and_worktrees_with_it_when_removedAsync()
    {
        string wt = await LandedInLayoutAsync("done");

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE merged {wt}", LineFor(lines, wt));
        Assert.False(Directory.Exists(wt));
        Assert.False(Directory.Exists(LayoutFolder));
        Assert.False(Directory.Exists(WorktreesFolder));
        Assert.True(Directory.Exists(_root));
        Assert.True(Directory.Exists(_main));
    }

    [Fact]
    public async Task Two_removed_worktrees_of_the_layout_folder_take_the_folders_with_themAsync()
    {
        string first = await LandedInLayoutAsync("job-a");
        string second = await LandedInLayoutAsync("job-b");

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE merged {first}", LineFor(lines, first));
        Assert.Equal($"REMOVE merged {second}", LineFor(lines, second));
        Assert.False(Directory.Exists(LayoutFolder));
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_kept_worktree_in_the_layout_folder_keeps_the_repository_folder_and_worktreesAsync()
    {
        string done = await LandedInLayoutAsync("done");
        string kept = await UnmergedInLayoutAsync("work");

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE merged {done}", LineFor(lines, done));
        Assert.Equal($"KEEP unmerged {kept}", LineFor(lines, kept));
        Assert.False(Directory.Exists(done));
        Assert.True(Directory.Exists(kept));
        Assert.True(Directory.Exists(LayoutFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_file_in_the_layout_folder_keeps_the_repository_folder_and_worktreesAsync()
    {
        string wt = await LandedInLayoutAsync("done");
        string note = Path.Combine(LayoutFolder, "notes.txt");
        await File.WriteAllTextAsync(note, "mine\n", TestContext.Current.CancellationToken);

        _ = await SweepAsync(OnMain("--apply"));

        Assert.False(Directory.Exists(wt));
        Assert.Equal("mine\n", await File.ReadAllTextAsync(note, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task The_folder_of_another_repository_keeps_worktrees_and_only_the_repository_folder_goesAsync()
    {
        string wt = await LandedInLayoutAsync("done");
        string other = Path.Combine(WorktreesFolder, "another-repo", "job");
        _ = Directory.CreateDirectory(other);

        _ = await SweepAsync(OnMain("--apply"));

        Assert.False(Directory.Exists(wt));
        Assert.False(Directory.Exists(LayoutFolder));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task A_sweep_over_a_root_takes_the_folders_the_same_wayAsync()
    {
        string wt = await LandedInLayoutAsync("done");

        string[] lines = await SweepAsync("--root", _root, "--older-than-days", "0", "--apply");

        Assert.Equal($"REMOVE merged {wt}", LineFor(lines, wt));
        Assert.False(Directory.Exists(LayoutFolder));
        Assert.False(Directory.Exists(WorktreesFolder));
    }

    // ---- what the sweep says does not change ----

    [Fact]
    public async Task A_dry_run_removes_nothing_and_prints_the_plan_onlyAsync()
    {
        string wt = await LandedInLayoutAsync("done");

        (int code, string output, string error) = await RunSweepWithErrorAsync(OnMain());

        Assert.Equal(0, code);
        Assert.Equal([$"REMOVE merged {wt}", "total: 1 worktrees, 1 removable, 0 kept"], LinesOf(output));
        Assert.Empty(error);
        Assert.True(Directory.Exists(wt));
        Assert.True(Directory.Exists(LayoutFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task The_lines_of_an_apply_run_are_those_of_the_dry_run_and_nothing_goes_to_the_standard_errorAsync()
    {
        string done = await LandedInLayoutAsync("done");
        string beside = await AddWorktreeAsync("main-beside", _root);
        await LandFileAsync(beside, "beside.txt");
        string kept = await UnmergedInLayoutAsync("work");

        (_, string plan, _) = await RunSweepWithErrorAsync(OnMain());
        (int code, string applied, string error) = await RunSweepWithErrorAsync(OnMain("--apply"));

        Assert.Equal(0, code);
        Assert.Equal(plan, applied);
        string[] lines = LinesOf(applied);
        Assert.Equal($"REMOVE merged {done}", LineFor(lines, done));
        Assert.Equal($"REMOVE merged {beside}", LineFor(lines, beside));
        Assert.Equal($"KEEP unmerged {kept}", LineFor(lines, kept));
        Assert.Equal("total: 3 worktrees, 2 removable, 1 kept", lines[^1]);
        Assert.Equal(4, lines.Length);
        Assert.Empty(error);
        Assert.False(Directory.Exists(done));
        Assert.False(Directory.Exists(beside));
        Assert.True(Directory.Exists(kept));
    }

    [Fact]
    public async Task A_removal_git_refuses_leaves_the_folders_and_the_lines_are_those_of_the_dry_runAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        string wt = await LandedInLayoutAsync("done");
        (_, string plan, _) = await RunSweepWithErrorAsync(OnMain());

        (int code, string applied, string error) = await RunWithGitAsync(
            before: "case \" $* \" in *\" worktree remove \"*) echo 'no removal today' >&2; exit 1;; esac",
            OnMain("--apply"));

        Assert.Equal(0, code);
        Assert.Equal(plan, applied);
        Assert.Equal($"could not remove {wt}: no removal today", error.Trim());
        Assert.True(Directory.Exists(wt));
        Assert.True(Directory.Exists(LayoutFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    private async Task<(int Code, string Output, string Error)> RunWithGitAsync(string before, string[] args)
    {
        using GitInterposer git = new(_dirs.Create("lw-fakegit"), _main, before);
        return await RunSweepWithErrorAsync(args);
    }

    // ---- a link in the layout ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_symlink_in_the_place_of_a_layout_folder_is_followed_nowhere_and_both_folders_stayAsync(bool worktreesIsTheLink)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");
        string wt = await LandedInLayoutAsync("done");
        string behind = Path.Combine(_root, "behind");
        string link = worktreesIsTheLink ? WorktreesFolder : LayoutFolder;
        Directory.Move(link, behind);
        _ = Directory.CreateSymbolicLink(link, behind);

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE merged {wt}", LineFor(lines, wt));
        Assert.False(Directory.Exists(wt));
        Assert.Equal(behind, new DirectoryInfo(link).LinkTarget);
        string emptied = worktreesIsTheLink ? Path.Combine(behind, "main") : behind;
        Assert.True(Directory.Exists(emptied));
        Assert.Empty(Directory.GetFileSystemEntries(emptied));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    // ---- a worktree that does not sit in the layout of the repository ----

    [Fact]
    public async Task A_worktree_in_some_other_worktrees_subfolder_is_swept_as_before_and_its_folders_stayAsync()
    {
        string elsewhere = Path.Combine(WorktreesFolder, "elsewhere");
        string wt = await AddWorktreeAsync("done", elsewhere);
        await LandAsync(wt);
        _ = Directory.CreateDirectory(LayoutFolder);

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE merged {wt}", LineFor(lines, wt));
        Assert.False(Directory.Exists(wt));
        Assert.True(Directory.Exists(elsewhere));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        Assert.True(Directory.Exists(LayoutFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    [Fact]
    public async Task A_worktree_next_to_the_repository_is_swept_as_before_and_leaves_the_layout_folders_aloneAsync()
    {
        string beside = await AddWorktreeAsync("main-beside", _root);
        await LandAsync(beside);
        _ = Directory.CreateDirectory(LayoutFolder);

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE merged {beside}", LineFor(lines, beside));
        Assert.False(Directory.Exists(beside));
        Assert.True(Directory.Exists(LayoutFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }

    // ---- left open: a worktree that was only pruned ----

    [Fact]
    public async Task A_worktree_whose_folder_was_gone_is_pruned_and_the_empty_repository_folder_stays_for_nowAsync()
    {
        string wt = await LandedInLayoutAsync("done");
        Directory.Delete(wt, recursive: true);

        string[] lines = await SweepAsync(OnMain("--apply"));

        Assert.Equal($"REMOVE missing {wt}", LineFor(lines, wt));
        Assert.DoesNotContain(wt, await GitAsync(_main, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.True(Directory.Exists(LayoutFolder));
        Assert.Empty(Directory.GetFileSystemEntries(LayoutFolder));
        Assert.True(Directory.Exists(WorktreesFolder));
    }
}
