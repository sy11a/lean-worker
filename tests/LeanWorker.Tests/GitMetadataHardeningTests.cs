using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The test rows of lean-worker#119 for the #114 hardening rounds (119g/119i/119j/119h): after a worker that planted a
/// clean filter, a hook or a redirect in the repository's own git metadata the launcher runs no git over the repository,
/// reports the tamper, keeps the worktree and refuses to continue the run. Full runs against stub workers, over real
/// repositories in temp folders (the worktree of a run is a sibling of the repository, so both live in one temp root).
/// </summary>
[Collection("launcher-process-state")]
[UnsupportedOSPlatform("windows")]
public sealed class GitMetadataHardeningTests : IDisposable
{
    private const string UsageStream =
        "printf '%s\\n' '{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"model\":\"claude-haiku-4-5\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":1000}}}'\n" +
        RunAsyncGolden.SuccessStream;

    private const string RunName = "scratch";
    private const string Branch = "scratch-branch";
    private const string SkipNote = "changed-files: skipped, the run changed the repository's git metadata";

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    // ---- helpers ----

    private static string[] Evidence(JsonObject summary) => [.. summary["evidence_lines"]!.AsArray().Select(n => n!.GetValue<string>())];

    private static string Status(JsonObject summary) => summary["status"]!.GetValue<string>();

    private static string WorktreeReason(JsonObject summary) => (summary["worktree"] as JsonObject)?["reason"]?.GetValue<string>() ?? "(none)";

    private static async Task GitAsync(string dir, params string[] args)
    {
        Git.Result result = await Git.RunResultAsync(dir, args) ?? throw new InvalidOperationException("git did not run");
        Assert.True(result.ExitCode is 0, $"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    /// <summary>
    /// A temp root holding a git repository with one commit (README.md = "base"), whose .lean-worker folder is the runs
    /// root. Everything a scenario plants beside the repository (copies, filter scripts, markers) goes into the root too.
    /// </summary>
    private async Task<(string Base, string Repo, string Root)> NewRepoAsync()
    {
        string tempRoot = _dirs.Create("lw-gm");
        string repo = Path.Combine(tempRoot, "repo");
        _ = Directory.CreateDirectory(repo);
        await GitAsync(repo, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "base\n", TestContext.Current.CancellationToken);
        await GitAsync(repo, "add", "--", "README.md");
        await GitAsync(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-m", "base");
        string root = Path.Combine(repo, ".lean-worker");
        _ = Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "task.md"), "do nothing", TestContext.Current.CancellationToken);
        return (tempRoot, repo, root);
    }

    private static async Task<(int Code, string Stdout, JsonObject Summary)> RunAsync(string root, string workerShell, string stream = RunAsyncGolden.SuccessStream,
        Action<Options>? configure = null)
    {
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, workerShell + "\n" + stream, configure);
        return (code, stdout, RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout)));
    }

    private static void InWorktree(Options o)
    {
        o.Name = RunName;
        o.Worktree = Branch;
        o.WorktreeBase = "HEAD";
    }

    /// <summary>
    /// The clean filter the scenarios plant: it touches the marker file and passes the content through, so a git call that
    /// runs it leaves the working tree as it was. Returns the script path and the marker path.
    /// </summary>
    private static async Task<(string Script, string Marker)> CleanFilterAsync(string tempRoot, string name)
    {
        string marker = Path.Combine(tempRoot, name + "-ran");
        string script = Path.Combine(tempRoot, name + "-clean.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ntouch '" + marker + "'\ncat\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, marker);
    }

    private static string FilterSection(string script) => "[filter \"evil\"]\\n\\tclean = " + script + "\\n\\tsmudge = cat\\n";

    private static void SkipUnlessChmodWorks() =>
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "needs a non-root POSIX user (chmod 000 has no effect for root)");

    // ---- the planted clean filter never runs in the launcher ----

    // edaf46a: the git-status pass over the working tree ran after the worker even when the worker had changed the
    // repository's config, so the planted clean filter ran in the launcher (and changed-files was listed).
    [Fact]
    public async Task A_planted_clean_filter_does_not_run_in_the_launcher_and_the_changed_files_stay_unknownAsync()
    {
        (string tempRoot, string repo, string root) = await NewRepoAsync();
        (string script, string marker) = await CleanFilterAsync(tempRoot, "filter");
        // The tracked file changes its content, not its size: the status has to compare the content, which is where the
        // filter would run.
        string worker =
            "printf '" + FilterSection(script) + "' >> '" + Path.Combine(repo, ".git", "config") + "'; " +
            "printf '*.md filter=evil\\n' > '" + Path.Combine(repo, ".gitattributes") + "'; " +
            "printf 'BASE\\n' > '" + Path.Combine(repo, "README.md") + "'";

        (int code, string stdout, JsonObject s) = await RunAsync(root, worker);

        Assert.False(File.Exists(marker), "the launcher ran the clean filter the worker planted");
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
        Assert.Null(s["changed_files"]);
        Assert.Null(s["out_of_scope"]);
        Assert.Contains(SkipNote, RunAsyncGolden.Notes_(s), StringComparer.Ordinal);
        Assert.Contains("- git-tamper: .git/config", Evidence(s), StringComparer.Ordinal);
        Assert.DoesNotContain("changed in the working tree", stdout, StringComparison.Ordinal);

        // The control: a plain git status over the same repository runs the filter, so the check above is not vacuous.
        Git.Result? status = await Git.RunResultAsync(repo, "status", "--porcelain");
        Assert.NotNull(status);
        Assert.True(File.Exists(marker), "the planted filter does not run under a plain git status");
    }

    // edaf46a: the decision over the worktree ran `git status` (and so the filter) on a worker that committed nothing;
    // the worktree was removed as clean.
    [Fact]
    public async Task A_planted_filter_with_no_commits_in_a_worktree_run_does_not_run_and_the_worktree_is_kept_as_git_metadataAsync()
    {
        (string tempRoot, string repo, string root) = await NewRepoAsync();
        (string script, string marker) = await CleanFilterAsync(tempRoot, "filter");
        string worktree = WorktreeRun.PathFor(repo, RunName);
        // The worker writes the file back as it was: the working tree holds no change, so the decision would have removed it.
        string worker =
            "c=$(git rev-parse --git-common-dir); " +
            "printf '" + FilterSection(script) + "' >> \"$c/config\"; " +
            "mkdir -p \"$c/info\"; " +
            "printf '*.md filter=evil\\n' >> \"$c/info/attributes\"; " +
            "printf 'changed\\n' >> README.md; " +
            "printf 'base\\n' > README.md";

        (int code, _, JsonObject s) = await RunAsync(root, worker, configure: o => InWorktree(o));

        Assert.False(File.Exists(marker), "the launcher ran the clean filter the worker planted");
        Assert.Contains("- git-tamper: .git/config", Evidence(s), StringComparer.Ordinal);
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
        Assert.True(Directory.Exists(worktree), "the worktree was removed");
        Assert.Equal("git-metadata", WorktreeReason(s));
        Assert.Contains("- worktree: kept git-metadata " + worktree, Evidence(s), StringComparer.Ordinal);
    }

    // edaf46a: the launcher's git calls were not pinned to the git dir resolved before the worker, so a rewritten ".git"
    // file of the worktree pointed them at the fake git dir and its filter ran.
    [Fact]
    public async Task A_worktree_git_file_rewritten_to_a_fake_git_dir_with_a_filter_does_not_run_it_and_is_git_tamperAsync()
    {
        (string tempRoot, string repo, string root) = await NewRepoAsync();
        (string script, string marker) = await CleanFilterAsync(tempRoot, "filter");
        string fake = Path.Combine(tempRoot, "fake-git");
        string worktree = WorktreeRun.PathFor(repo, RunName);
        string worker =
            "d=$(git rev-parse --absolute-git-dir); " +
            "c=$(dirname \"$(dirname \"$d\")\"); " +
            "cp -a \"$c\" '" + fake + "'; " +
            "cp -a \"$d/HEAD\" \"$d/index\" '" + fake + "/'; " +
            "rm -f '" + fake + "/commondir'; " +
            "mkdir -p '" + fake + "/info'; " +
            "printf '" + FilterSection(script) + "' >> '" + fake + "/config'; " +
            "printf '*.md filter=evil\\n' >> '" + fake + "/info/attributes'; " +
            "printf 'changed\\n' >> README.md; " +
            "printf 'gitdir: %s\\n' '" + fake + "' > .git";

        (int code, _, JsonObject s) = await RunAsync(root, worker, configure: o => InWorktree(o));

        Assert.False(File.Exists(marker), "the launcher ran the filter of the fake git dir");
        Assert.Contains(Evidence(s), l => l.StartsWith("- git-tamper: ", StringComparison.Ordinal) && l.EndsWith("/.git", StringComparison.Ordinal));
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
        Assert.True(Directory.Exists(worktree), "the worktree was removed");
        Assert.Equal("git-metadata", WorktreeReason(s));
    }

    // edaf46a: git reads "$GIT_DIR/commondir" in a main ".git" too, and the pinned status did not name the common dir, so
    // a planted commondir moved the invoking repository's status to a copy whose filter then ran.
    [Fact]
    public async Task A_commondir_planted_in_the_invoking_git_dir_does_not_run_the_filter_of_the_copy_and_is_git_tamperAsync()
    {
        (string tempRoot, string repo, string root) = await NewRepoAsync();
        (string script, string marker) = await CleanFilterAsync(tempRoot, "filter");
        string worktree = WorktreeRun.PathFor(repo, RunName);
        // The worker copies the git dir of the invoking repository, gives the copy a clean filter and points the ".git" of
        // the invoking repository at it. The tracked file it changes keeps its size.
        string worker =
            "d=$(git rev-parse --absolute-git-dir); " +
            "c=$(dirname \"$(dirname \"$d\")\"); " +
            "repo=$(dirname \"$c\"); " +
            "cp -a \"$c\" \"$c-copy\"; " +
            "printf '" + FilterSection(script) + "' >> \"$c-copy/config\"; " +
            "mkdir -p \"$c-copy/info\"; " +
            "printf '* filter=evil\\n' > \"$c-copy/info/attributes\"; " +
            "printf '%s\\n' \"$c-copy\" > \"$c/commondir\"; " +
            "printf 'BASE\\n' > \"$repo/README.md\"";

        (int code, _, JsonObject s) = await RunAsync(root, worker, configure: o => InWorktree(o));

        Assert.False(File.Exists(marker), "the launcher ran the filter of the planted commondir");
        Assert.Contains("- git-tamper: .git/commondir", Evidence(s), StringComparer.Ordinal);
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
        Assert.True(Directory.Exists(worktree), "the worktree was removed");
        Assert.Equal("git-metadata", WorktreeReason(s));
    }

    // edaf46a: the after pass followed the worktree's own commondir file, so a redirect to a clean copy hid the change of
    // the real config (core.fsmonitor) from the comparison.
    [Fact]
    public async Task A_worktree_commondir_redirected_to_a_clean_copy_while_the_real_config_is_changed_is_git_tamperAsync()
    {
        (string tempRoot, _, string root) = await NewRepoAsync();
        (string script, string marker) = await CleanFilterAsync(tempRoot, "monitor");
        string worker =
            "d=$(git rev-parse --absolute-git-dir); " +
            "c=$(dirname \"$(dirname \"$d\")\"); " +
            "cp -a \"$c\" \"$c-copy\"; " +
            "printf '[core]\\n\\tfsmonitor = " + script + "\\n' >> \"$c/config\"; " +
            "printf '%s\\n' \"$c-copy\" > \"$d/commondir\"";

        (int code, _, JsonObject s) = await RunAsync(root, worker, configure: o => InWorktree(o));

        Assert.False(File.Exists(marker), "the launcher ran the fsmonitor the worker wrote");
        Assert.Contains(Evidence(s), l => l.StartsWith("- git-tamper: ", StringComparison.Ordinal));
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
    }

    // ---- the hooks ----

    // edaf46a: the hash of a hook covered its content, not its mode, so an exec bit on an existing hook went unseen.
    [Fact]
    public async Task An_exec_bit_added_to_an_existing_hook_is_git_tamperAsync()
    {
        (_, string repo, string root) = await NewRepoAsync();
        string hook = Path.Combine(repo, ".git", "hooks", "pre-commit");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (int code, _, JsonObject s) = await RunAsync(root, "chmod +x '" + hook + "'");

        Assert.Contains("- git-tamper: .git/hooks/pre-commit", Evidence(s), StringComparer.Ordinal);
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
    }

    // edaf46a: a hooks directory swapped for a symlink was followed, so the hooks behind it were hashed.
    [Fact]
    public async Task A_hooks_directory_swapped_for_a_symlink_is_git_tamper_and_the_target_is_not_hashedAsync()
    {
        (string tempRoot, string repo, string root) = await NewRepoAsync();
        string hooks = Path.Combine(repo, ".git", "hooks");
        string elsewhere = Path.Combine(tempRoot, "elsewhere-hooks");
        _ = Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "pre-commit"), "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);

        (int code, _, JsonObject s) = await RunAsync(root, "rm -rf '" + hooks + "'; ln -s '" + elsewhere + "' '" + hooks + "'");

        Assert.Contains("- git-tamper: .git/hooks", Evidence(s), StringComparer.Ordinal);
        Assert.DoesNotContain(Evidence(s), l => l.Contains("elsewhere-hooks/pre-commit", StringComparison.Ordinal));
        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
    }

    // ---- an unreadable watched file ----

    // edaf46a: a watched file the after snapshot could not read was dropped from the comparison, so the run looked clean.
    [Fact]
    public async Task A_watched_file_that_is_unreadable_after_the_run_is_git_tamperAsync()
    {
        SkipUnlessChmodWorks();
        (_, string repo, string root) = await NewRepoAsync();
        string config = Path.Combine(repo, ".git", "config");
        try
        {
            (int code, _, JsonObject s) = await RunAsync(root, "chmod 000 '" + config + "'");

            Assert.Contains("- git-tamper: git-metadata: unreadable after the run", Evidence(s), StringComparer.Ordinal);
            Assert.Contains("git-tamper: git-metadata: unreadable after the run", RunAsyncGolden.Notes_(s), StringComparer.Ordinal);
            Assert.Equal("out-of-scope", Status(s));
            Assert.Equal(1, code);
        }
        finally
        {
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ---- the wrap-up and the continuation ----

    private static void WrapUpAtTheBudget(Options o)
    {
        o.MaxBudgetUsd = 0.007m;
        o.WrapUpAt = 0.8m;
        o.Mode = "auto";
    }

    // The control of the next row: an untouched repository still wraps up and invites the continuation.
    [Fact]
    public async Task A_clean_wrap_up_is_wrapped_up_exits_3_and_invites_the_continuationAsync()
    {
        (_, _, string root) = await NewRepoAsync();

        (int code, string stdout, JsonObject s) = await RunAsync(root, "true", UsageStream, o => WrapUpAtTheBudget(o));

        Assert.Equal("wrapped-up", Status(s));
        Assert.Equal(3, code);
        Assert.Contains("continue:", stdout, StringComparison.Ordinal);
    }

    // edaf46a: a wrap-up kept its status wrapped-up (exit 3) whatever the worker did to the git metadata, and printed the
    // continue line.
    [Fact]
    public async Task A_tampered_wrap_up_is_out_of_scope_exits_1_and_prints_no_continue_or_escalate_lineAsync()
    {
        (_, string repo, string root) = await NewRepoAsync();
        string worker = "printf '[core]\\n\\thooksPath = /dev/null\\n' >> '" + Path.Combine(repo, ".git", "config") + "'";

        (int code, string stdout, JsonObject s) = await RunAsync(root, worker, UsageStream, o => WrapUpAtTheBudget(o));

        Assert.Equal("out-of-scope", Status(s));
        Assert.Equal(1, code);
        Assert.DoesNotContain("continue:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("escalate:", stdout, StringComparison.Ordinal);
    }

    // edaf46a: --continue-from started a fresh worker over a repository whose config the previous run had tampered with.
    [Fact]
    public async Task Continuing_a_run_whose_summary_records_git_tamper_is_refusedAsync()
    {
        (_, string repo, string root) = await NewRepoAsync();
        (_, _, JsonObject first) = await RunAsync(root, "printf '[core]\\n\\thooksPath = /dev/null\\n' >> '" + Path.Combine(repo, ".git", "config") + "'");
        string runDir = first["run_dir"]!.GetValue<string>();
        string marker = Path.Combine(_dirs.Create("lw-marker"), "worker-ran");

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, "touch '" + marker + "'\n" + RunAsyncGolden.SuccessStream, o =>
        {
            o.TaskFile = null;
            o.ContinueFrom = runDir;
        });

        Assert.Equal(1, code);
        Assert.Contains("continue: refused:", stdout, StringComparison.Ordinal);
        Assert.Contains("git tamper", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), "the worker ran although the continuation was refused");
    }

    // ---- a launch that dies before the worker ----

    // edaf46a: a launch that died before the after pass decided over the worktree as a finished run did: a clean worktree
    // was removed. The launcher cannot vouch for the repository then, so the worktree is kept as "unverified".
    [Fact]
    public async Task A_launch_that_dies_before_the_worker_in_a_worktree_run_keeps_the_worktree_as_unverifiedAsync()
    {
        (_, string repo, string root) = await NewRepoAsync();
        string worktree = WorktreeRun.PathFor(repo, RunName);
        string marker = Path.Combine(_dirs.Create("lw-marker"), "worker-ran");
        TextWriter oldError = Console.Error;
        await using StringWriter error = new();
        try
        {
            Console.SetError(error);
            _ = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(root, "touch '" + marker + "'\n" + RunAsyncGolden.SuccessStream, o =>
            {
                InWorktree(o);
                o.WrapUpAt = 1m;
            }));
        }
        finally
        {
            Console.SetError(oldError);
        }

        Assert.False(File.Exists(marker), "the worker ran");
        Assert.True(Directory.Exists(worktree), "the worktree was removed");
        Assert.Contains("worktree: kept unverified " + worktree, error.ToString(), StringComparison.Ordinal);
    }
}
