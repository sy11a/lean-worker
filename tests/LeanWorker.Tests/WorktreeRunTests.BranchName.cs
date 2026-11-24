using Xunit;

namespace LeanWorker.Tests;

// The check of the --worktree name (lean-worker#166): a name that starts with '-' is refused by the launcher before
// git is asked anything, every other name goes through `git check-ref-format --branch` (which also expands `@{-N}`),
// and a symbolic ref in the place of the branch counts as taken. Same fixture as the other worktree tests; the
// launch through the command line is in the sibling file of the same fixture.
public sealed partial class WorktreeRunTests
{
    private static string DashRefusal(string branch) => $"--worktree '{branch}' cannot name a branch: a branch name cannot start with '-'";

    private static string GitRefusalStart(string branch) => $"--worktree '{branch}' cannot name a branch: git check-ref-format --branch refuses it (";

    private static string ReadsAs(string branch, string previous) =>
        $"--worktree '{branch}' cannot name a branch: git reads it as '{previous}'; the name must be the branch itself";

    private async Task<WorktreeRun> ReuseNamedAsync(string branch)
    {
        WorktreeRun run = await WorktreeRun.ReuseAsync(_repo, branch, "HEAD", RunName, _setup);
        Assert.True(Directory.Exists(run.WorktreePath));
        return run;
    }

    /// <summary>
    /// The launch of the fixture with <paramref name="branch"/>, first round or a later one: refused, and then no ref of
    /// the repository changed (the full for-each-ref output is compared), no worktree is registered and no layout
    /// folder was made.
    /// </summary>
    private async Task<LaunchException> RefusedNameAsync(string branch, bool laterRound = false)
    {
        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () =>
            _ = laterRound ? await ReuseNamedAsync(branch) : await CreateRunAsync(branch, RunName));

        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        AssertLayoutGone();
        return ex;
    }

    /// <summary>
    /// Checks out a branch and then the one the repository started on: <c>@{-1}</c> now reads as the first.
    /// </summary>
    private async Task<(string Previous, string Current)> CheckOutTwoBranchesAsync()
    {
        string current = await GitAsync(_repo, "symbolic-ref", "--short", "HEAD");
        _ = await GitAsync(_repo, "branch", "prev-a", "HEAD");
        _ = await GitAsync(_repo, "switch", "prev-a");
        _ = await GitAsync(_repo, "switch", current);
        return ("prev-a", current);
    }

    /// <summary>
    /// The calls the launcher makes to git of the fixture's repository while <paramref name="launch"/> runs, one line
    /// per call (the arguments after -C).
    /// </summary>
    private async Task<string[]> GitCallsDuringAsync(Func<Task> launch)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        string folder = FakeGitFolder;
        string log = Path.Combine(folder, "calls.log");
        using (GitInterposer git = new(folder, _repo, before: $"printf '%s\\n' \"$*\" >> '{log}'"))
        {
            await launch();
        }

        return File.Exists(log) ? [.. await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken)] : [];
    }

    // ---- a name that starts with '-' is refused without asking git ----

    [Theory]
    [InlineData("-x")]
    [InlineData("--force")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("-b")]
    public async Task A_branch_name_that_starts_with_a_dash_is_refused_without_asking_git_at_allAsync(string branch)
    {
        string[] calls = await GitCallsDuringAsync(async () =>
            _ = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(branch, RunName)));

        Assert.Empty(calls);
        LaunchException ex = await RefusedNameAsync(branch);
        Assert.Equal(DashRefusal(branch), ex.Message);
    }

    [Fact]
    public async Task A_valid_branch_name_is_asked_of_git_first_and_the_log_of_calls_shows_itAsync()
    {
        string[] calls = await GitCallsDuringAsync(async () => _ = await CreateRunAsync("a/b", RunName));

        Assert.NotEmpty(calls);
        Assert.EndsWith(" check-ref-format --branch a/b", calls[0], StringComparison.Ordinal);
    }

    // ---- a name git refuses ----

    [Theory]
    [InlineData("a..b")]
    [InlineData("bad name")]
    [InlineData("x.lock")]
    [InlineData("HEAD")]
    [InlineData("")]
    [InlineData("a/")]
    [InlineData("/a")]
    [InlineData("a//b")]
    [InlineData("a.")]
    [InlineData(".a")]
    [InlineData("a/.b")]
    [InlineData("a~1")]
    [InlineData("a^b")]
    [InlineData("a:b")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("a[")]
    [InlineData("a\\b")]
    [InlineData("a@{b")]
    [InlineData("main@{1}")]
    [InlineData("@{u}")]
    [InlineData("@{upstream}")]
    public async Task A_branch_name_git_check_ref_format_refuses_is_refused_with_the_launchers_message_and_nothing_is_madeAsync(string branch)
    {
        LaunchException ex = await RefusedNameAsync(branch);

        Assert.StartsWith(GitRefusalStart(branch), ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(")", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_name_with_a_control_character_or_a_line_break_is_refused_by_git_and_nothing_is_madeAsync()
    {
        // The message shows the name on one line (issue #170): each of these characters is written as the two characters \n.
        (string Name, string Shown)[] names = [("a\u0001b", "a\\nb"), ("a\u007fb", "a\\nb"), ("x\n", "x\\n"), ("x\r", "x\\n"), ("a\tb", "a\\nb")];
        foreach ((string name, string shown) in names)
        {
            LaunchException ex = await RefusedNameAsync(name);

            Assert.StartsWith(GitRefusalStart(shown), ex.Message, StringComparison.Ordinal);
            AssertOneLine(ex.Message);
        }
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData("HEAD")]
    public async Task A_branch_name_git_refuses_costs_one_git_call_the_check_and_nothing_elseAsync(string branch)
    {
        string[] calls = await GitCallsDuringAsync(async () =>
            _ = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(branch, RunName)));

        string call = Assert.Single(calls);
        Assert.EndsWith($" check-ref-format --branch {branch}", call, StringComparison.Ordinal);
    }

    // ---- @{-N}: git reads it as the branch checked out before ----

    [Fact]
    public async Task The_previous_branch_spelled_as_at_minus_one_is_refused_as_what_git_reads_it_as_and_that_branch_is_untouchedAsync()
    {
        (string previous, string current) = await CheckOutTwoBranchesAsync();
        string previousSha = await ShaAsync("refs/heads/" + previous);
        string currentSha = await ShaAsync("refs/heads/" + current);

        LaunchException one = await RefusedNameAsync("@{-1}");
        LaunchException two = await RefusedNameAsync("@{-2}");
        LaunchException suffixed = await RefusedNameAsync("@{-1}x");

        Assert.Equal(ReadsAs("@{-1}", previous), one.Message);
        Assert.Equal(ReadsAs("@{-2}", current), two.Message);
        Assert.Equal(ReadsAs("@{-1}x", previous + "x"), suffixed.Message);
        Assert.Equal(previousSha, await ShaAsync("refs/heads/" + previous));
        Assert.Equal(currentSha, await ShaAsync("refs/heads/" + current));
        Assert.False(await BranchExistsAsync(previous + "x"));
    }

    [Fact]
    public async Task The_previous_branch_spelled_as_at_minus_one_is_refused_in_a_later_round_with_the_same_messageAsync()
    {
        (string previous, _) = await CheckOutTwoBranchesAsync();

        LaunchException ex = await RefusedNameAsync("@{-1}", laterRound: true);

        Assert.Equal(ReadsAs("@{-1}", previous), ex.Message);
    }

    [Fact]
    public async Task At_minus_one_without_a_previous_branch_is_refused_as_a_name_git_refusesAsync()
    {
        LaunchException ex = await RefusedNameAsync("@{-1}");

        Assert.StartsWith(GitRefusalStart("@{-1}"), ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(")", ex.Message, StringComparison.Ordinal);
    }

    // ---- names that are valid pass unchanged ----

    [Theory]
    [InlineData("a/b")]
    [InlineData("v1.2-fix")]
    [InlineData("a-b")]
    [InlineData("café-über")]
    [InlineData("a@b")]
    [InlineData("@x")]
    [InlineData("x@")]
    [InlineData("@")]
    [InlineData("head")]
    public async Task A_valid_branch_name_passes_unchanged_and_the_worktree_is_made_and_removed_againAsync(string branch)
    {
        string baseSha = await ShaAsync("HEAD");

        WorktreeRun wt = await CreateRunAsync(branch, RunName);

        Assert.Equal(baseSha, await ShaAsync("refs/heads/" + branch));
        Assert.Equal("refs/heads/" + branch, await GitAsync(wt.WorktreePath, "symbolic-ref", "HEAD"));
        await wt.FinishAsync();
        Assert.Equal("removed", wt.Action);
        Assert.False(await BranchExistsAsync(branch));
        AssertLayoutGone();
    }

    // ---- a symbolic ref in the place of the branch ----

    [Fact]
    public async Task A_dangling_symbolic_ref_in_the_place_of_the_branch_is_taken_and_the_branch_it_points_to_is_not_madeAsync()
    {
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/" + Branch, "refs/heads/gone");

        LaunchException ex = await RefusedNameAsync(Branch);

        Assert.Equal($"the branch is taken: {Branch} (it is a symbolic ref to refs/heads/gone)", ex.Message);
        Assert.False(await BranchExistsAsync("gone"));
        Assert.Equal("refs/heads/gone", await GitAsync(_repo, "symbolic-ref", "refs/heads/" + Branch));
    }

    [Fact]
    public async Task A_symbolic_ref_to_an_existing_branch_is_taken_and_the_branch_it_points_to_is_untouchedAsync()
    {
        string target = await GitAsync(_repo, "symbolic-ref", "--short", "HEAD");
        string targetSha = await ShaAsync("refs/heads/" + target);
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/" + Branch, "refs/heads/" + target);

        LaunchException ex = await RefusedNameAsync(Branch);

        // The probe that follows the ref finds the target and answers first: the plain message, without the link.
        Assert.Equal($"the branch is taken: {Branch}", ex.Message);
        Assert.Equal(targetSha, await ShaAsync("refs/heads/" + target));
        Assert.Equal("refs/heads/" + target, await GitAsync(_repo, "symbolic-ref", "refs/heads/" + Branch));
    }

    // ---- a later round ----

    [Theory]
    [InlineData("-x")]
    [InlineData("--force")]
    [InlineData("-")]
    [InlineData("a..b")]
    [InlineData("bad name")]
    [InlineData("x.lock")]
    [InlineData("HEAD")]
    [InlineData("")]
    public async Task A_later_round_refuses_the_same_names_with_the_same_message_as_the_first_roundAsync(string branch)
    {
        LaunchException first = await RefusedNameAsync(branch);
        LaunchException later = await RefusedNameAsync(branch, laterRound: true);

        Assert.Equal(first.Message, later.Message);
        Assert.StartsWith($"--worktree '{branch}' cannot name a branch: ", later.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_round_with_a_refused_name_leaves_the_worktree_of_the_first_round_as_it_isAsync()
    {
        WorktreeRun first = await CreateRunAsync(Branch, RunName);
        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await ReuseNamedAsync("-x"));

        Assert.Equal(DashRefusal("-x"), ex.Message);
        Assert.True(Directory.Exists(first.WorktreePath));
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
    }

    [Fact]
    public async Task A_later_round_with_a_valid_name_still_continues_its_worktree_and_cleans_it_upAsync()
    {
        WorktreeRun first = await CreateRunAsync("a/b", RunName);

        WorktreeRun again = await ReuseNamedAsync("a/b");

        Assert.Equal(first.WorktreePath, again.WorktreePath);
        Assert.Null(again.ContinueNote);
        await again.FinishAsync();
        Assert.Equal("removed", again.Action);
        Assert.False(await BranchExistsAsync("a/b"));
        AssertLayoutGone();
    }
}
