using Xunit;

namespace LeanWorker.Tests;

// The --worktree name on one line in the refusals (lean-worker#170), and the refusals #166 left untested: a git that
// cannot tell whether the branch is a symbolic ref, a chain or a loop of symbolic refs in the branch's place, a git
// that cannot be run for the name check, and the names that spell a ref prefix (refs/heads/x, heads/x). Same fixture
// as the other worktree tests: the helpers are theirs.
public sealed partial class WorktreeRunTests
{
    // Written as hex escapes: the two separators would end a line of this source file if they stood in it as characters.
    private const string NextLine = "\x0085";
    private const string LineSeparator = "\x2028";
    private const string ParagraphSeparator = "\x2029";

    /// <summary>
    /// The message is one line: not one of the characters a reader of the launch error takes for the end of a line.
    /// </summary>
    private static void AssertOneLine(string message)
    {
        foreach (string c in new[] { "\n", "\r", NextLine, LineSeparator, ParagraphSeparator })
        {
            Assert.DoesNotContain(c, message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(message, c => char.IsControl(c));
    }

    private async Task<string[]> RefNamesAsync() =>
        [.. (await GitAsync(_repo, "for-each-ref", "--format=%(refname)")).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    // ---- a name git refuses or rewrites is shown on one line ----

    [Theory]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\rb", "a\\nb")]
    [InlineData("a\r\nb", "a\\n\\nb")]
    [InlineData("a\tb", "a\\nb")]
    [InlineData("a\u0001b", "a\\nb")]
    [InlineData("a\u007fb", "a\\nb")]
    [InlineData("x\nLEAN-WORKER LAUNCH FAILED: fake", "x\\nLEAN-WORKER LAUNCH FAILED: fake")]
    [InlineData("x\r\nLEAN-WORKER LAUNCH FAILED: fake", "x\\n\\nLEAN-WORKER LAUNCH FAILED: fake")]
    public async Task A_name_with_a_line_break_or_a_control_character_is_shown_on_one_line_in_the_refusal_of_either_roundAsync(string branch, string shown)
    {
        LaunchException first = await RefusedNameAsync(branch);
        LaunchException later = await RefusedNameAsync(branch, laterRound: true);

        Assert.Equal(first.Message, later.Message);
        Assert.StartsWith(GitRefusalStart(shown), first.Message, StringComparison.Ordinal);
        Assert.EndsWith(")", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(branch, first.Message, StringComparison.Ordinal);
        AssertOneLine(first.Message);
    }

    [Theory]
    [InlineData("-x\nfake", "-x\\nfake")]
    [InlineData("-\r", "-\\n")]
    [InlineData("--force\u0001", "--force\\n")]
    [InlineData("-x\nLEAN-WORKER LAUNCH FAILED: fake", "-x\\nLEAN-WORKER LAUNCH FAILED: fake")]
    public async Task A_name_with_a_dash_and_a_line_break_is_shown_on_one_line_and_git_is_not_askedAsync(string branch, string shown)
    {
        string[] calls = await GitCallsDuringAsync(async () =>
            _ = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(branch, RunName)));

        Assert.Empty(calls);
        LaunchException first = await RefusedNameAsync(branch);
        LaunchException later = await RefusedNameAsync(branch, laterRound: true);
        Assert.Equal(DashRefusal(shown), first.Message);
        Assert.Equal(first.Message, later.Message);
        AssertOneLine(first.Message);
    }

    // ---- U+0085, U+2028 and U+2029: names git takes, on one line in the "taken" refusals ----

    [Theory]
    [InlineData("a" + NextLine + "b")]
    [InlineData("a" + LineSeparator + "b")]
    [InlineData("a" + ParagraphSeparator + "b")]
    public async Task A_name_with_a_unicode_line_separator_is_a_branch_name_to_git_and_the_branch_is_taken_on_one_lineAsync(string branch)
    {
        Git.Result check = await GitResultAsync(_repo, "check-ref-format", "--branch", branch);
        Assert.Equal(0, check.ExitCode);
        Assert.Equal(branch, check.Stdout.TrimEnd('\n', '\r'));
        string baseSha = await ShaAsync("HEAD");
        _ = await GitAsync(_repo, "branch", branch, baseSha);
        await CommitAsync(_repo, "later.txt", "later\n", "later");

        LaunchException ex = await RefusedNameAsync(branch);

        Assert.Equal("the branch is taken: a\\nb", ex.Message);
        AssertOneLine(ex.Message);
        Assert.Equal(baseSha, await ShaAsync("refs/heads/" + branch));
    }

    [Theory]
    [InlineData("a" + NextLine + "b")]
    [InlineData("a" + LineSeparator + "b")]
    [InlineData("a" + ParagraphSeparator + "b")]
    public async Task A_symbolic_ref_at_a_name_with_a_unicode_line_separator_is_taken_and_the_message_is_one_lineAsync(string branch)
    {
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/" + branch, "refs/heads/gone");

        LaunchException ex = await RefusedNameAsync(branch);

        Assert.Equal("the branch is taken: a\\nb (it is a symbolic ref to refs/heads/gone)", ex.Message);
        AssertOneLine(ex.Message);
        Assert.False(await BranchExistsAsync("gone"));
    }

    [Theory]
    [InlineData("a" + NextLine + "b")]
    [InlineData("a" + LineSeparator + "b")]
    [InlineData("a" + ParagraphSeparator + "b")]
    public async Task A_name_with_a_unicode_line_separator_that_is_free_makes_the_worktree_and_the_removal_takes_that_branchAsync(string branch)
    {
        string refsBefore = await RefsAsync();
        string baseSha = await ShaAsync("HEAD");

        WorktreeRun wt = await CreateRunAsync(branch, RunName);

        Assert.Equal(baseSha, await ShaAsync("refs/heads/" + branch));
        Assert.Equal("refs/heads/" + branch, await GitAsync(wt.WorktreePath, "symbolic-ref", "HEAD"));
        await wt.FinishAsync();
        Assert.Equal("removed", wt.Action);
        Assert.Equal(refsBefore, await RefsAsync());
        AssertLayoutGone();
    }

    // ---- a git that cannot tell whether the branch is a symbolic ref ----

    [Theory]
    [InlineData(2, Branch, Branch)]
    [InlineData(3, Branch, Branch)]
    [InlineData(128, Branch, Branch)]
    [InlineData(3, "a" + LineSeparator + "b", "a\\nb")]
    public async Task A_symbolic_ref_probe_that_ends_with_another_code_than_zero_or_one_refuses_the_launch_and_makes_nothingAsync(int exitCode, string branch, string shown)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();
        LaunchException ex;
        using (GitInterposer git = new(FakeGitFolder, _repo, before: $"case \" $* \" in *\" symbolic-ref -q refs/heads/\"*) echo 'no link today' >&2; exit {exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)};; esac"))
        {
            ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync(branch, RunName));
        }

        Assert.Equal($"the branch {shown} cannot be checked: git could not tell whether it is a symbolic ref (no link today)", ex.Message);
        AssertOneLine(ex.Message);
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        Assert.False(await BranchExistsAsync(branch));
        AssertLayoutGone();
    }

    // ---- a chain or a loop of symbolic refs in the branch's place ----

    [Fact]
    public async Task A_chain_of_symbolic_refs_to_an_existing_branch_is_taken_and_nothing_is_madeAsync()
    {
        string target = await GitAsync(_repo, "symbolic-ref", "--short", "HEAD");
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/link-b", "refs/heads/" + target);
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/link-a", "refs/heads/link-b");
        string targetSha = await ShaAsync("refs/heads/" + target);

        LaunchException ex = await RefusedNameAsync("link-a");

        // The probe that follows the chain finds the branch at its end and answers first: the plain message.
        Assert.Equal("the branch is taken: link-a", ex.Message);
        Assert.Equal("refs/heads/link-b", await GitAsync(_repo, "symbolic-ref", "--no-recurse", "refs/heads/link-a"));
        Assert.Equal("refs/heads/" + target, await GitAsync(_repo, "symbolic-ref", "--no-recurse", "refs/heads/link-b"));
        Assert.Equal(targetSha, await ShaAsync("refs/heads/" + target));
    }

    [Fact]
    public async Task A_chain_of_symbolic_refs_to_a_missing_branch_is_taken_and_nothing_is_made_through_itAsync()
    {
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/link-b", "refs/heads/gone");
        _ = await GitAsync(_repo, "symbolic-ref", "refs/heads/link-a", "refs/heads/link-b");

        LaunchException ex = await RefusedNameAsync("link-a");

        // The probe that follows the chain finds nothing at its end; symbolic-ref -q follows it too and names the end.
        Assert.Equal("the branch is taken: link-a (it is a symbolic ref to refs/heads/gone)", ex.Message);
        Assert.False(await BranchExistsAsync("gone"));
        Assert.Equal("refs/heads/link-b", await GitAsync(_repo, "symbolic-ref", "--no-recurse", "refs/heads/link-a"));
        Assert.Equal("refs/heads/gone", await GitAsync(_repo, "symbolic-ref", "--no-recurse", "refs/heads/link-b"));
    }

    [Fact]
    public async Task A_loop_of_symbolic_refs_in_the_place_of_the_branch_is_refused_and_nothing_is_madeAsync()
    {
        string heads = Path.Combine(_repo, ".git", "refs", "heads");
        await File.WriteAllTextAsync(Path.Combine(heads, "loop-a"), "ref: refs/heads/loop-b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(heads, "loop-b"), "ref: refs/heads/loop-a\n", TestContext.Current.CancellationToken);

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateRunAsync("loop-a", RunName));

        // git follows the loop and cannot read the ref (symbolic-ref -q ends with 128, not with 1), so the launcher
        // cannot tell and refuses; the text after the colon is git's own.
        Assert.StartsWith("the branch loop-a cannot be checked: git could not tell whether it is a symbolic ref (", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(")", ex.Message, StringComparison.Ordinal);
        AssertOneLine(ex.Message);
        Assert.False(Directory.Exists(WorktreePath));
        Assert.DoesNotContain(WorktreePath, await WorktreesAsync(), StringComparison.Ordinal);
        AssertLayoutGone();
        Assert.Equal("ref: refs/heads/loop-b\n", await File.ReadAllTextAsync(Path.Combine(heads, "loop-a"), TestContext.Current.CancellationToken));
        Assert.Equal("ref: refs/heads/loop-a\n", await File.ReadAllTextAsync(Path.Combine(heads, "loop-b"), TestContext.Current.CancellationToken));
    }

    // ---- a git that cannot be run for the name check ----

    [Theory]
    [InlineData(Branch, Branch)]
    [InlineData("x\ny", "x\\ny")]
    public async Task A_git_that_cannot_be_run_for_the_name_check_is_a_refusal_in_either_round_and_nothing_is_madeAsync(string branch, string shown)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        string refsBefore = await RefsAsync();
        string worktreesBefore = await WorktreesAsync();
        string noGit = _dirs.Create("lw-nogit");
        LaunchException first;
        LaunchException later;
        using (EnvScope path = new(("PATH", noGit)))
        {
            first = await Assert.ThrowsAsync<LaunchException>(async () => await WorktreeRun.CreateAsync(_repo, branch, "HEAD", RunName, _setup));
            later = await Assert.ThrowsAsync<LaunchException>(async () => await WorktreeRun.ReuseAsync(_repo, branch, "HEAD", RunName, _setup));
        }

        string expected = $"--worktree '{shown}' cannot be checked: git check-ref-format --branch could not be run";
        Assert.Equal(expected, first.Message);
        Assert.Equal(expected, later.Message);
        AssertOneLine(first.Message);
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.Equal(worktreesBefore, await WorktreesAsync());
        AssertLayoutGone();
    }

    // ---- names that spell a ref prefix ----

    [Theory]
    [InlineData("refs/heads/x", "refs/heads/refs/heads/x")]
    [InlineData("heads/x", "refs/heads/heads/x")]
    public async Task A_name_that_spells_a_ref_prefix_is_the_branch_under_refs_heads_and_the_removal_takes_that_branch_onlyAsync(string branch, string fullRef)
    {
        _ = await GitAsync(_repo, "branch", "x", "HEAD");
        _ = await GitAsync(_repo, "branch", "other", "HEAD");
        string refsBefore = await RefsAsync();
        string[] namesBefore = await RefNamesAsync();
        string baseSha = await ShaAsync("HEAD");

        WorktreeRun wt = await CreateRunAsync(branch, RunName);

        Assert.Equal(fullRef, await GitAsync(wt.WorktreePath, "symbolic-ref", "HEAD"));
        Assert.Equal(baseSha, await ShaAsync(fullRef));
        string[] expectedNames = [.. namesBefore.Append(fullRef).Order(StringComparer.Ordinal)];
        Assert.Equal(expectedNames, await RefNamesAsync(), StringComparer.Ordinal);
        await wt.FinishAsync();
        Assert.Equal("removed", wt.Action);
        Assert.Equal(refsBefore, await RefsAsync());
        Assert.True(await BranchExistsAsync("x"));
        Assert.True(await BranchExistsAsync("other"));
        Assert.False(await BranchExistsAsync(branch));
        AssertLayoutGone();
    }
}
