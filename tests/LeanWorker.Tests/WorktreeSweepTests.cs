using System.Diagnostics;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The "sweep-worktrees" subcommand over real git repositories in temp folders: a bare origin, a clone of it as
/// the main checkout and linked worktrees beside it. Every repository gets its own user config (never global).
/// </summary>
[Collection("launcher-process-state")]
public sealed class WorktreeSweepTests : IAsyncLifetime
{
    private readonly CommandSandbox _box = new();
    private readonly TempDirs _dirs = new();
    private string _root = string.Empty;
    private string _main = string.Empty;
    private string _wts = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _root = _dirs.Create("lw-sweep");
        string origin = Path.Combine(_root, "origin.git");
        _main = Path.Combine(_root, "main");
        _wts = Path.Combine(_root, "wts");
        _ = Directory.CreateDirectory(_wts);

        _ = await GitAsync(_root, "init", "--bare", "-b", "main", origin);
        _ = await GitAsync(_root, "clone", origin, _main);
        await ConfigureAsync(_main);
        await CommitAsync(_main, "README.md", "base\n", "base");
        _ = await GitAsync(_main, "push", "-u", "origin", "main");
        _ = await GitAsync(_main, "remote", "set-head", "origin", "-a");
    }

    public ValueTask DisposeAsync()
    {
        _box.Dispose();
        _dirs.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<string> GitAsync(string dir, params string[] args)
    {
        Git.Result result = await Git.RunResultAsync(dir, args) ?? throw new InvalidOperationException("git did not run");
        Assert.True(result.ExitCode is 0, $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout;
    }

    private static async Task ConfigureAsync(string dir)
    {
        _ = await GitAsync(dir, "config", "user.name", "Test");
        _ = await GitAsync(dir, "config", "user.email", "test@example.com");
        _ = await GitAsync(dir, "config", "commit.gpgsign", "false");
    }

    private static async Task CommitAsync(string dir, string file, string content, string message)
    {
        string path = Path.Combine(dir, file);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        _ = await GitAsync(dir, "add", "--", file);
        _ = await GitAsync(dir, "commit", "-m", message);
    }

    private async Task<string> AddWorktreeAsync(string name, string? parent = null)
    {
        string path = Path.Combine(parent ?? _wts, name);
        _ = await GitAsync(_main, "worktree", "add", "-b", name, path);
        return path;
    }

    private async Task LandAsync(string branchDir)
    {
        await CommitAsync(branchDir, "feature.txt", "feature\n", "feature");
        _ = await GitAsync(_main, "merge", "--ff-only", Path.GetFileName(branchDir));
        _ = await GitAsync(_main, "push", "origin", "main");
    }

    private static async Task<(int Code, string[] Lines, string Output)> RunSweepAsync(params string[] args)
    {
        TextWriter oldOut = Console.Out;
        await using StringWriter captured = new();
        Console.SetOut(captured);
        try
        {
            int code = await WorktreeSweep.RunAsync(args);
            string output = captured.ToString();
            return (code, output.Split('\n', StringSplitOptions.RemoveEmptyEntries), output);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }

    private static async Task<string[]> SweepAsync(params string[] args)
    {
        (int code, string[] lines, _) = await RunSweepAsync(args);
        Assert.Equal(0, code);
        return lines;
    }

    private static string LineFor(string[] lines, string path) =>
        Assert.Single(lines, l => l.EndsWith(" " + path, StringComparison.Ordinal));

    private async Task<string[]> SweepMainAsync(params string[] extra) =>
        await SweepAsync([.. new[] { "--repo", _main, "--older-than-days", "0" }, .. extra]);

    [Fact]
    public async Task A_clean_merged_worktree_is_removed_with_apply_and_its_branch_staysAsync()
    {
        string wt = await AddWorktreeAsync("done");
        await LandAsync(wt);

        string[] plan = await SweepMainAsync();
        Assert.Equal($"REMOVE merged {wt}", LineFor(plan, wt));
        Assert.True(Directory.Exists(wt));

        string[] applied = await SweepMainAsync("--apply");
        Assert.Equal($"REMOVE merged {wt}", LineFor(applied, wt));
        Assert.False(Directory.Exists(wt));
        Assert.DoesNotContain(wt, await GitAsync(_main, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.Contains("done", await GitAsync(_main, "branch", "--list", "done"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_squash_landed_branch_is_removed_as_landedAsync()
    {
        string wt = await AddWorktreeAsync("squash");
        await CommitAsync(wt, "feature.txt", "same\n", "on the branch");
        await CommitAsync(_main, "feature.txt", "same\n", "squashed");
        _ = await GitAsync(_main, "push", "origin", "main");

        Assert.Equal($"REMOVE landed {wt}", LineFor(await SweepMainAsync(), wt));
    }

    [Fact]
    public async Task An_unmerged_branch_is_keptAsync()
    {
        string wt = await AddWorktreeAsync("work");
        await CommitAsync(wt, "wip.txt", "wip\n", "wip");

        Assert.Equal($"KEEP unmerged {wt}", LineFor(await SweepMainAsync(), wt));
    }

    [Fact]
    public async Task An_uncommitted_change_keeps_the_worktree_as_dirtyAsync()
    {
        string wt = await AddWorktreeAsync("edited");
        await File.WriteAllTextAsync(Path.Combine(wt, "README.md"), "changed\n", TestContext.Current.CancellationToken);

        Assert.Equal($"KEEP dirty {wt}", LineFor(await SweepMainAsync(), wt));
    }

    [Fact]
    public async Task An_untracked_file_keeps_the_worktree_as_dirtyAsync()
    {
        string wt = await AddWorktreeAsync("untracked");
        await File.WriteAllTextAsync(Path.Combine(wt, "new.txt"), "x\n", TestContext.Current.CancellationToken);

        Assert.Equal($"KEEP dirty {wt}", LineFor(await SweepMainAsync(), wt));
    }

    [Fact]
    public async Task A_locked_worktree_is_keptAsync()
    {
        string wt = await AddWorktreeAsync("locked");
        _ = await GitAsync(_main, "worktree", "lock", wt);

        Assert.Equal($"KEEP locked {wt}", LineFor(await SweepMainAsync("--apply"), wt));
        Assert.True(Directory.Exists(wt));
    }

    [Fact]
    public async Task Built_in_and_custom_protect_globs_keep_the_worktreeAsync()
    {
        string pinned = await AddWorktreeAsync("repo-pinned");
        string custom = await AddWorktreeAsync("keepme-now");
        string open = await AddWorktreeAsync("open");

        string[] lines = await SweepMainAsync("--protect", "keepme-*", "--apply");

        Assert.Equal($"KEEP protected {pinned}", LineFor(lines, pinned));
        Assert.Equal($"KEEP protected {custom}", LineFor(lines, custom));
        Assert.Equal($"REMOVE merged {open}", LineFor(lines, open));
        Assert.True(Directory.Exists(pinned));
        Assert.True(Directory.Exists(custom));
    }

    [Fact]
    public async Task A_worktree_that_holds_another_worktree_is_kept_as_nestedAsync()
    {
        string outer = await AddWorktreeAsync("outer");
        string inner = await AddWorktreeAsync("inner", outer);

        string[] lines = await SweepMainAsync("--apply");

        Assert.Equal($"KEEP nested {outer}", LineFor(lines, outer));
        Assert.StartsWith("REMOVE", LineFor(lines, inner), StringComparison.Ordinal);
        Assert.True(Directory.Exists(outer));
    }

    [Fact]
    public async Task A_worktree_whose_folder_is_gone_is_removed_as_missing_and_prunedAsync()
    {
        string wt = await AddWorktreeAsync("gone");
        Directory.Delete(wt, recursive: true);

        Assert.Equal($"REMOVE missing {wt}", LineFor(await SweepMainAsync(), wt));
        Assert.Contains(wt, await GitAsync(_main, "worktree", "list", "--porcelain"), StringComparison.Ordinal);

        _ = await SweepMainAsync("--apply");
        Assert.DoesNotContain(wt, await GitAsync(_main, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rebase_in_progress_keeps_the_worktree_as_dirtyAsync()
    {
        string wt = await AddWorktreeAsync("rebasing");
        await CommitAsync(wt, "README.md", "branch\n", "branch change");
        await CommitAsync(_main, "README.md", "main\n", "main change");
        _ = await GitAsync(_main, "push", "origin", "main");
        Git.Result? rebase = await Git.RunResultAsync(wt, "rebase", "origin/main");
        Assert.NotEqual(0, rebase?.ExitCode);
        string rebaseDir = (await GitAsync(wt, "rev-parse", "--git-path", "rebase-merge")).Trim();
        Assert.True(Directory.Exists(rebaseDir));

        Assert.Equal($"KEEP dirty {wt}", LineFor(await SweepMainAsync(), wt));
    }

    [Fact]
    public async Task A_fresh_worktree_is_young_with_the_default_ageAsync()
    {
        string wt = await AddWorktreeAsync("fresh");

        string[] lines = await SweepAsync("--repo", _main);

        Assert.Equal($"KEEP young {wt}", LineFor(lines, wt));
    }

    [Fact]
    public async Task Without_an_origin_nothing_counts_as_mergedAsync()
    {
        string repo = Path.Combine(_root, "solo");
        _ = await GitAsync(_root, "init", "-b", "main", repo);
        await ConfigureAsync(repo);
        await CommitAsync(repo, "a.txt", "a\n", "a");
        string wt = Path.Combine(_wts, "solo-wt");
        _ = await GitAsync(repo, "worktree", "add", "-b", "solo-wt", wt);

        string[] lines = await SweepAsync("--repo", repo, "--older-than-days", "0");

        Assert.Equal($"KEEP unmerged {wt}", LineFor(lines, wt));
    }

    [Fact]
    public async Task A_dry_run_removes_nothing_and_the_total_matches_the_linesAsync()
    {
        string merged = await AddWorktreeAsync("m1");
        string unmerged = await AddWorktreeAsync("u1");
        await CommitAsync(unmerged, "u.txt", "u\n", "u");
        string locked = await AddWorktreeAsync("l1");
        _ = await GitAsync(_main, "worktree", "lock", locked);
        await LandAsync(merged);

        string[] lines = await SweepMainAsync();

        Assert.Equal(4, lines.Length);
        Assert.Equal("total: 3 worktrees, 1 removable, 2 kept", lines[^1]);
        _ = Assert.Single(lines, l => l.StartsWith("REMOVE ", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(l => l.StartsWith("KEEP ", StringComparison.Ordinal)));
        Assert.True(Directory.Exists(merged));
        Assert.True(Directory.Exists(unmerged));
        Assert.True(Directory.Exists(locked));
    }

    [Fact]
    public async Task Root_finds_every_repository_directly_under_itAsync()
    {
        string root = _dirs.Create("lw-sweep-root");
        string wtA = Path.Combine(_wts, "wt-a");
        string wtB = Path.Combine(_wts, "wt-b");
        foreach ((string name, string wt) in new[] { ("a", wtA), ("b", wtB) })
        {
            string repo = Path.Combine(root, name);
            _ = await GitAsync(root, "init", "-b", "main", repo);
            await ConfigureAsync(repo);
            await CommitAsync(repo, "x.txt", "x\n", "x");
            _ = await GitAsync(repo, "worktree", "add", "-b", Path.GetFileName(wt), wt);
        }

        string[] lines = await SweepAsync("--root", root, "--older-than-days", "0");

        _ = LineFor(lines, wtA);
        _ = LineFor(lines, wtB);
        Assert.Equal("total: 2 worktrees, 0 removable, 2 kept", lines[^1]);
    }

    [Fact]
    public async Task A_process_working_inside_the_worktree_keeps_it_as_in_useAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the working directories are read from /proc");
        string wt = await AddWorktreeAsync("busy");
        using Process sleeper = Process.Start(new ProcessStartInfo("sleep", "30") { WorkingDirectory = wt, UseShellExecute = false })!;
        try
        {
            string[] lines = await SweepMainAsync("--apply");

            Assert.Equal($"KEEP in-use {wt}", LineFor(lines, wt));
            Assert.True(Directory.Exists(wt));
        }
        finally
        {
            sleeper.Kill(entireProcessTree: true);
            await sleeper.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--repo")]
    [InlineData("--older-than-days")]
    public async Task Unknown_options_and_missing_values_failAsync(string option) =>
        _ = await Assert.ThrowsAsync<LaunchException>(async () => await RunSweepAsync(option));

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task A_bad_age_failsAsync(string days) =>
        _ = await Assert.ThrowsAsync<LaunchException>(async () => await RunSweepAsync("--repo", _main, "--older-than-days", days));

    [Fact]
    public async Task Help_prints_the_usage_and_returns_zeroAsync()
    {
        (int code, _, string output) = await RunSweepAsync("--help");

        Assert.Equal(0, code);
        Assert.StartsWith("sweep-worktrees:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_result_reports_the_exit_code_and_stderr_of_a_failing_callAsync()
    {
        Git.Result? result = await Git.RunResultAsync(_main, "rev-parse", "--verify", "no-such-ref");

        Assert.NotNull(result);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }

    [Fact]
    public async Task Git_result_reports_stdout_of_a_good_callAsync()
    {
        Git.Result? result = await Git.RunResultAsync(_main, "rev-parse", "--abbrev-ref", "HEAD");

        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("main", result.Stdout.Trim());
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Git_run_still_returns_null_on_a_non_zero_exitAsync()
    {
        Assert.Null(await Git.RunAsync(_main, "rev-parse", "--verify", "no-such-ref"));
        Assert.Equal("main", (await Git.RunAsync(_main, "rev-parse", "--abbrev-ref", "HEAD"))?.Trim());
    }
}
