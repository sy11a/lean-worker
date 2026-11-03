using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The worktree a run owns (--worktree) over real git repositories in temp folders: a bare origin, a clone of it as
/// the invoking repository (origin/HEAD set) and the worktree beside it. Every repository gets its own user config
/// (never global).
/// </summary>
[Collection("launcher-process-state")]
public sealed class WorktreeRunTests : IAsyncLifetime
{
    private const string Branch = "wt-branch";
    private const string RunName = "job";

    private readonly TempDirs _dirs = new();
    private string _root = string.Empty;
    private string _repo = string.Empty;
    private string _setup = string.Empty;

    private string WorktreePath => Path.Combine(_root, "repo-" + RunName);

    public async ValueTask InitializeAsync()
    {
        _root = _dirs.Create("lw-wtrun");
        string origin = Path.Combine(_root, "origin.git");
        _repo = Path.Combine(_root, "repo");
        _setup = Path.Combine(_repo, ".lean-worker");

        _ = await GitAsync(_root, "init", "--bare", "-b", "main", origin);
        _ = await GitAsync(_root, "clone", origin, _repo);
        _ = await GitAsync(_repo, "config", "user.name", "Test");
        _ = await GitAsync(_repo, "config", "user.email", "test@example.com");
        _ = await GitAsync(_repo, "config", "commit.gpgsign", "false");
        await CommitAsync(_repo, "README.md", "base\n", "base");
        _ = await GitAsync(_repo, "push", "-u", "origin", "main");
        _ = await GitAsync(_repo, "remote", "set-head", "origin", "-a");

        WriteFile(_setup, "profiles.json", "{\"profiles\":{}}");
        WriteFile(_setup, "project.md", "project notes\n");
        WriteFile(_setup, "code-a.md", "code a\n");
        WriteFile(_setup, "system/s.md", "system notes\n");
    }

    public ValueTask DisposeAsync()
    {
        _dirs.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<string> GitAsync(string dir, params string[] args)
    {
        Git.Result result = await Git.RunResultAsync(dir, args) ?? throw new InvalidOperationException("git did not run");
        Assert.True(result.ExitCode is 0, $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout.Trim();
    }

    private static async Task<Git.Result> GitResultAsync(string dir, params string[] args) =>
        await Git.RunResultAsync(dir, args) ?? throw new InvalidOperationException("git did not run");

    private static void WriteFile(string dir, string relative, string content)
    {
        string path = Path.Combine(dir, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task CommitAsync(string dir, string file, string content, string message)
    {
        WriteFile(dir, file, content);
        _ = await GitAsync(dir, "add", "--", file);
        _ = await GitAsync(dir, "commit", "-m", message);
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private async Task<WorktreeRun> CreateAsync(string baseRef = "HEAD", string branch = Branch, string name = RunName) =>
        await WorktreeRun.CreateAsync(_repo, branch, baseRef, name, _setup);

    private static string[] Lines(JsonObject summary) => [.. summary["evidence_lines"]!.AsArray().Select(n => n!.GetValue<string>())];

    private async Task<bool> BranchExistsAsync(string branch) =>
        (await GitResultAsync(_repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).ExitCode is 0;

    private async Task AssertKeptAsync(WorktreeRun wt, string reason)
    {
        await wt.FinishAsync();
        Assert.Equal("kept", wt.Action);
        Assert.Equal(reason, wt.Reason);
        Assert.True(Directory.Exists(wt.WorktreePath));
        Assert.True(await BranchExistsAsync(Branch));
    }

    // ---- base pin ----

    [Fact]
    public async Task A_commit_of_the_worker_on_the_branch_keeps_the_worktree_as_commitsAsync()
    {
        WorktreeRun wt = await CreateAsync("HEAD");
        await CommitAsync(wt.WorktreePath, "work.txt", "work\n", "work");

        await AssertKeptAsync(wt, "commits");
    }

    [Fact]
    public async Task A_commit_on_a_detached_head_keeps_the_worktree_as_commitsAsync()
    {
        WorktreeRun wt = await CreateAsync("HEAD");
        _ = await GitAsync(wt.WorktreePath, "checkout", "--detach");
        await CommitAsync(wt.WorktreePath, "work.txt", "work\n", "work");

        await AssertKeptAsync(wt, "commits");
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("no-such-ref")]
    public async Task A_base_that_is_a_flag_or_does_not_exist_is_a_launch_error_and_creates_nothingAsync(string baseRef)
    {
        _ = await Assert.ThrowsAsync<LaunchException>(async () => await CreateAsync(baseRef));

        Assert.False(Directory.Exists(WorktreePath));
        Assert.False(File.Exists(WorktreePath));
        Assert.False(await BranchExistsAsync(Branch));
        Assert.DoesNotContain(WorktreePath, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
    }

    // ---- branch delete ----

    [Fact]
    public async Task The_launchers_own_git_calls_do_not_run_the_core_fsmonitor_hook_so_it_cannot_move_the_branchAsync()
    {
        // Since #114 the launcher's own git calls carry -c core.fsmonitor= : a worker that planted a
        // fsmonitor hook in the shared config can no longer move the branch (or anything else)
        // while the launcher looks at the working tree.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        WorktreeRun wt = await CreateAsync("HEAD");
        string baseSha = await GitAsync(_repo, "rev-parse", "HEAD");
        string moved = await GitAsync(_repo, "commit-tree", baseSha + "^{tree}", "-p", baseSha, "-m", "moved");

        string ran = Path.Combine(_root, "fsmonitor-ran");
        string hook = Path.Combine(_root, "fsmonitor.sh");
        WriteScript(hook, $"#!/bin/sh\ntouch '{ran}'\ngit update-ref refs/heads/{Branch} {moved} {baseSha} >/dev/null 2>&1\nexit 1\n");
        _ = await GitAsync(_repo, "config", "core.fsmonitor", hook);

        // Control: a plain git call (without the launcher's options) does run the hook, so the
        // assertions below prove the launcher's calls skip it and not that the hook never works.
        await RunPlainGitAsync(_repo, "status");
        Assert.True(File.Exists(ran), "control: a plain git status should run the fsmonitor hook");
        File.Delete(ran);
        _ = await GitAsync(_repo, "update-ref", "refs/heads/" + Branch, baseSha);

        await wt.FinishAsync();

        Assert.False(File.Exists(ran), "the launcher's git calls ran the fsmonitor hook");
        Assert.Equal("removed", wt.Action);
        Assert.Equal("empty", wt.Reason);
        Assert.False(Directory.Exists(wt.WorktreePath));
        Assert.Null(wt.Note);
        Assert.False(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task The_launchers_own_git_calls_do_not_run_a_hook_from_core_hooksPathAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        WorktreeRun wt = await CreateAsync("HEAD");
        string ran = Path.Combine(_root, "hook-ran");
        string hooks = Path.Combine(_root, "evil-hooks");
        _ = Directory.CreateDirectory(hooks);
        WriteScript(Path.Combine(hooks, "reference-transaction"), $"#!/bin/sh\ntouch '{ran}'\n");
        _ = await GitAsync(_repo, "config", "core.hooksPath", hooks);

        // Control: a plain git call that updates a ref runs the hook.
        string baseSha = await GitAsync(_repo, "rev-parse", "HEAD");
        await RunPlainGitAsync(_repo, "update-ref", "refs/heads/control", baseSha);
        Assert.True(File.Exists(ran), "control: a plain ref update should run the reference-transaction hook");
        File.Delete(ran);

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.False(File.Exists(ran), "the launcher's git calls ran a hook from core.hooksPath");
    }

    private static async Task RunPlainGitAsync(string dir, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo psi = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        CancellationToken ct = TestContext.Current.CancellationToken;
        _ = await p.StandardOutput.ReadToEndAsync(ct);
        _ = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
    }

    // ---- exemption ----

    [Fact]
    public async Task An_untracked_file_of_the_worker_in_the_lean_worker_folder_keeps_the_worktreeAsync()
    {
        WorktreeRun wt = await CreateAsync();
        WriteFile(wt.WorktreePath, ".lean-worker/inbox/x.md", "note\n");

        await AssertKeptAsync(wt, "changes");
    }

    [Fact]
    public async Task A_file_the_base_tracks_is_not_overwritten_by_the_copy_and_an_edit_of_it_keeps_the_worktreeAsync()
    {
        await CommitAsync(_repo, ".lean-worker/project.md", "tracked notes\n", "track project.md");
        WriteFile(_setup, "project.md", "local notes\n");

        WorktreeRun wt = await CreateAsync();
        string inWorktree = Path.Combine(wt.WorktreePath, ".lean-worker", "project.md");
        Assert.Equal("tracked notes\n", await File.ReadAllTextAsync(inWorktree, TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(inWorktree, "edited by the worker\n", TestContext.Current.CancellationToken);

        await AssertKeptAsync(wt, "changes");
    }

    [Fact]
    public async Task A_copied_file_the_worker_changed_keeps_the_worktreeAsync()
    {
        WorktreeRun wt = await CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(wt.WorktreePath, ".lean-worker", "code-a.md"), "changed\n", TestContext.Current.CancellationToken);

        await AssertKeptAsync(wt, "changes");
    }

    [Fact]
    public async Task A_copied_file_the_worker_deleted_keeps_the_worktreeAsync()
    {
        WorktreeRun wt = await CreateAsync();
        File.Delete(Path.Combine(wt.WorktreePath, ".lean-worker", "code-a.md"));

        await AssertKeptAsync(wt, "changes");
    }

    [Fact]
    public async Task A_copied_file_the_worker_added_to_the_index_keeps_the_worktreeAsync()
    {
        WorktreeRun wt = await CreateAsync();
        _ = await GitAsync(wt.WorktreePath, "add", "-f", "--", ".lean-worker/code-a.md");

        await AssertKeptAsync(wt, "changes");
    }

    [Fact]
    public async Task A_nested_untracked_file_keeps_the_worktreeAsync()
    {
        WorktreeRun wt = await CreateAsync();
        WriteFile(wt.WorktreePath, "src/new/dir/f.txt", "new\n");

        await AssertKeptAsync(wt, "changes");
    }

    // ---- no force ----

    [Fact]
    public async Task A_clean_run_deletes_the_copied_files_removes_the_worktree_and_the_branch_and_leaves_the_invoking_setupAsync()
    {
        WorktreeRun wt = await CreateAsync();
        foreach (string file in new[] { "profiles.json", "project.md", "code-a.md", "system/s.md" })
        {
            Assert.True(File.Exists(Path.Combine(wt.WorktreePath, ".lean-worker", file)), file);
        }

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.Equal("empty", wt.Reason);
        Assert.Null(wt.Note);
        Assert.False(Directory.Exists(wt.WorktreePath));
        Assert.DoesNotContain(wt.WorktreePath, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.False(await BranchExistsAsync(Branch));
        Assert.Equal("{\"profiles\":{}}", await File.ReadAllTextAsync(Path.Combine(_setup, "profiles.json"), TestContext.Current.CancellationToken));
        Assert.Equal("project notes\n", await File.ReadAllTextAsync(Path.Combine(_setup, "project.md"), TestContext.Current.CancellationToken));
        Assert.Equal("code a\n", await File.ReadAllTextAsync(Path.Combine(_setup, "code-a.md"), TestContext.Current.CancellationToken));
        Assert.Equal("system notes\n", await File.ReadAllTextAsync(Path.Combine(_setup, "system", "s.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tracked_modification_keeps_the_worktree_as_changesAsync()
    {
        WorktreeRun wt = await CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(wt.WorktreePath, "README.md"), "edited\n", TestContext.Current.CancellationToken);

        await AssertKeptAsync(wt, "changes");
    }

    [Fact]
    public async Task An_ignored_file_is_discarded_with_the_worktree_and_counted_in_the_reasonAsync()
    {
        await CommitAsync(_repo, ".gitignore", "bin/\n", "ignore bin");
        WorktreeRun wt = await CreateAsync();
        WriteFile(wt.WorktreePath, "bin/x", "build output\n");

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.Equal("empty (ignored files discarded: 1)", wt.Reason);
        Assert.False(Directory.Exists(wt.WorktreePath));
        Assert.False(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task A_worktree_git_refuses_to_remove_stays_and_its_multi_line_error_is_one_evidence_lineAsync()
    {
        WorktreeRun wt = await CreateAsync();
        _ = await GitAsync(_repo, "worktree", "lock", "--reason", "held", wt.WorktreePath);

        await wt.FinishAsync();

        Assert.Equal("kept", wt.Action);
        Assert.StartsWith("kept: ", wt.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', wt.Reason);
        Assert.Contains("\\n", wt.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(wt.WorktreePath));
        Assert.True(await BranchExistsAsync(Branch));

        JsonObject summary = new()
        {
            ["status"] = "success",
            ["exit_code"] = 0,
            ["terminal_reason"] = "completed",
            ["duration_ms"] = 1,
            ["worktree"] = new JsonObject { ["path"] = wt.WorktreePath, ["branch"] = Branch, ["action"] = wt.Action, ["reason"] = wt.Reason },
        };
        List<string> lines = EvidenceBlock.Build(summary);
        string line = Assert.Single(lines, l => l.StartsWith("- worktree:", StringComparison.Ordinal));
        Assert.Equal($"- worktree: kept {wt.Reason} {wt.WorktreePath}", line);
        Assert.All(lines, l => Assert.DoesNotContain('\n', l));
    }

    [Fact]
    public void A_reason_with_line_breaks_stays_on_one_evidence_line()
    {
        JsonObject summary = new()
        {
            ["status"] = "success",
            ["exit_code"] = 0,
            ["terminal_reason"] = "completed",
            ["duration_ms"] = 1,
            ["worktree"] = new JsonObject { ["path"] = "/w/a\nb", ["branch"] = "x", ["action"] = "kept", ["reason"] = "kept: first\nsecond" },
        };

        List<string> lines = EvidenceBlock.Build(summary);

        Assert.Equal("- worktree: kept kept: first\\nsecond /w/a\\nb", Assert.Single(lines, l => l.StartsWith("- worktree:", StringComparison.Ordinal)));
        Assert.All(lines, l => Assert.DoesNotContain('\n', l));
    }

    // ---- taken ----

    [Fact]
    public async Task A_path_that_is_taken_is_refused_and_left_as_it_wasAsync()
    {
        _ = Directory.CreateDirectory(WorktreePath);
        WriteFile(WorktreePath, "mine.txt", "mine\n");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateAsync());

        Assert.Contains("taken", ex.Message, StringComparison.Ordinal);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Path.Combine(WorktreePath, "mine.txt"), TestContext.Current.CancellationToken));
        Assert.Equal([Path.Combine(WorktreePath, "mine.txt")], Directory.GetFileSystemEntries(WorktreePath), StringComparer.Ordinal);
        Assert.False(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task A_branch_that_is_taken_is_refused_and_unchangedAsync()
    {
        _ = await GitAsync(_repo, "branch", Branch);
        string before = await GitAsync(_repo, "rev-parse", "refs/heads/" + Branch);
        await CommitAsync(_repo, "later.txt", "later\n", "later");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateAsync());

        Assert.Contains("taken", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await GitAsync(_repo, "rev-parse", "refs/heads/" + Branch));
        Assert.False(Directory.Exists(WorktreePath));
    }

    [Fact]
    public async Task A_second_run_with_the_same_name_is_refused_and_the_first_worktree_is_unchangedAsync()
    {
        WorktreeRun first = await CreateAsync();
        string before = await GitAsync(_repo, "rev-parse", "refs/heads/" + Branch);

        _ = await Assert.ThrowsAsync<LaunchException>(async () => await CreateAsync(branch: "other-branch"));

        Assert.True(Directory.Exists(first.WorktreePath));
        Assert.Contains(first.WorktreePath, await GitAsync(_repo, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.Equal(before, await GitAsync(_repo, "rev-parse", "refs/heads/" + Branch));
        Assert.False(await BranchExistsAsync("other-branch"));
        Assert.True(File.Exists(Path.Combine(first.WorktreePath, ".lean-worker", "code-a.md")));
    }

    // ---- through the launcher ----

    private static string Success(string before = "") => before + "\n" + RunAsyncGolden.SuccessStream;

    private async Task<(int Code, string Stdout, JsonObject Summary)> LaunchAsync(string script, Action<Options> configure)
    {
        // The task lives in the invoking repository's .lean-worker folder, which is also the runs root.
        WriteFile(_setup, "task.md", "do nothing");
        (int code, string stdout) = await RunAsyncGolden.RunAsync(_setup, script, configure);
        return (code, stdout, RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout)));
    }

    [Fact]
    public async Task Without_the_flag_the_summary_has_a_null_worktree_and_nothing_is_printedAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream);
        JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));

        Assert.Equal(0, code);
        Assert.True(s.ContainsKey("worktree"));
        Assert.Null(s["worktree"]);
        Assert.DoesNotContain("worktree:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(s["evidence_lines"]!.AsArray(), n => n!.GetValue<string>().StartsWith("- worktree:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clean_launch_with_the_flag_removes_the_worktree_and_records_all_four_keysAsync()
    {
        (int code, string stdout, JsonObject s) = await LaunchAsync(Success(), o =>
        {
            o.Name = RunName;
            o.Worktree = Branch;
        });

        Assert.Equal(0, code);
        JsonObject wt = s["worktree"]!.AsObject();
        Assert.Equal(["path", "branch", "action", "reason"], wt.Select(kv => kv.Key), StringComparer.Ordinal);
        Assert.Equal(WorktreePath, wt["path"]!.GetValue<string>());
        Assert.Equal(Branch, wt["branch"]!.GetValue<string>());
        Assert.Equal("removed", wt["action"]!.GetValue<string>());
        Assert.Equal("empty", wt["reason"]!.GetValue<string>());
        Assert.Contains($"- worktree: removed empty {WorktreePath}", Lines(s), StringComparer.Ordinal);
        Assert.Contains($"worktree: removed empty {WorktreePath}", stdout.Split('\n'), StringComparer.Ordinal);
        Assert.False(Directory.Exists(WorktreePath));
        Assert.False(await BranchExistsAsync(Branch));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(_setup, s);
    }

    [Fact]
    public async Task A_launch_whose_worker_leaves_a_file_keeps_the_worktree_and_says_soAsync()
    {
        (int code, string stdout, JsonObject s) = await LaunchAsync(Success("echo made > made.txt"), o =>
        {
            o.Name = RunName;
            o.Worktree = Branch;
            o.WorktreeBase = "HEAD";
        });

        Assert.Equal(0, code);
        JsonObject wt = s["worktree"]!.AsObject();
        Assert.Equal(WorktreePath, wt["path"]!.GetValue<string>());
        Assert.Equal(Branch, wt["branch"]!.GetValue<string>());
        Assert.Equal("kept", wt["action"]!.GetValue<string>());
        Assert.Equal("changes", wt["reason"]!.GetValue<string>());
        Assert.Contains($"- worktree: kept changes {WorktreePath}", Lines(s), StringComparer.Ordinal);
        Assert.Contains($"worktree: kept changes {WorktreePath}", stdout.Split('\n'), StringComparer.Ordinal);
        Assert.True(File.Exists(Path.Combine(WorktreePath, "made.txt")));
        Assert.True(await BranchExistsAsync(Branch));
    }

    [Fact]
    public async Task A_launch_with_the_flag_and_no_name_is_a_launch_error_and_creates_nothingAsync()
    {
        WriteFile(_setup, "task.md", "do nothing");

        _ = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsyncGolden.RunAsync(_setup, RunAsyncGolden.SuccessStream, o =>
        {
            o.Name = null;
            o.Worktree = Branch;
        }));

        Assert.False(await BranchExistsAsync(Branch));
    }

    // ---- symlinks in the cleanup (lean-worker#113) ----

    private static void SkipWithoutSymlinks() => Assert.SkipWhen(OperatingSystem.IsWindows(), "the symlink tests need a Unix file system");

    private void AssertInvokingSetupIntact()
    {
        Assert.Equal("{\"profiles\":{}}", File.ReadAllText(Path.Combine(_setup, "profiles.json")));
        Assert.Equal("project notes\n", File.ReadAllText(Path.Combine(_setup, "project.md")));
        Assert.Equal("code a\n", File.ReadAllText(Path.Combine(_setup, "code-a.md")));
        Assert.Equal("system notes\n", File.ReadAllText(Path.Combine(_setup, "system", "s.md")));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(_repo, "README.md")));
    }

    [Fact]
    public async Task A_copied_folder_replaced_by_a_symlink_to_the_invoking_folder_deletes_nothing_and_keeps_the_worktreeAsync()
    {
        SkipWithoutSymlinks();
        WorktreeRun wt = await CreateAsync();
        string linked = Path.Combine(wt.WorktreePath, ".lean-worker", "system");
        Directory.Delete(linked, recursive: true);
        _ = Directory.CreateSymbolicLink(linked, Path.Combine(_setup, "system"));

        await AssertKeptAsync(wt, "changes");

        AssertInvokingSetupIntact();
        Assert.NotNull(wt.Note);
        Assert.Contains(linked, wt.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copied_file_replaced_by_a_symlink_to_its_source_is_not_deleted_through_and_the_worktree_is_keptAsync()
    {
        SkipWithoutSymlinks();
        WorktreeRun wt = await CreateAsync();
        string linked = Path.Combine(wt.WorktreePath, ".lean-worker", "code-a.md");
        File.Delete(linked);
        _ = File.CreateSymbolicLink(linked, Path.Combine(_setup, "code-a.md"));

        await AssertKeptAsync(wt, "changes");

        AssertInvokingSetupIntact();
        Assert.NotNull(wt.Note);
        Assert.Contains(linked, wt.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worktree_root_replaced_by_a_symlink_to_the_invoking_repository_deletes_nothing_and_runs_no_status_thereAsync()
    {
        SkipWithoutSymlinks();
        WorktreeRun wt = await CreateAsync();
        string moved = wt.WorktreePath + "-moved";
        Directory.Move(wt.WorktreePath, moved);
        _ = Directory.CreateSymbolicLink(wt.WorktreePath, _repo);

        // git consults core.fsmonitor on every status: a status run on the invoking repository's tree leaves the marker.
        string marker = Path.Combine(_root, "status-ran");
        string hook = Path.Combine(_root, "fsmonitor.sh");
        WriteScript(hook, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        _ = await GitAsync(_repo, "config", "core.fsmonitor", hook);

        try
        {
            await wt.FinishAsync();

            Assert.Equal("kept", wt.Action);
            Assert.Equal("changes", wt.Reason);
            Assert.NotNull(wt.Note);
            Assert.Contains("check failed", wt.Note, StringComparison.Ordinal);
            Assert.False(File.Exists(marker), "no status may run on the invoking repository's tree");
            AssertInvokingSetupIntact();
            Assert.True(Directory.Exists(Path.Combine(_repo, ".git")));
            Assert.True(await BranchExistsAsync(Branch));
            Assert.True(File.Exists(Path.Combine(moved, ".lean-worker", "code-a.md")));
        }
        finally
        {
            // Remove the link itself, never what it points at.
            Directory.Delete(wt.WorktreePath);
        }
    }

    /// <summary>
    /// Commits a symlink at <c>.lean-worker/system</c> (pointing at a folder outside the repository), then restores the
    /// real folder in the working tree: the base now tracks a symlink at a setup path.
    /// </summary>
    private async Task<string> TrackSymlinkedSystemFolderAsync()
    {
        string outside = Path.Combine(_root, "outside");
        _ = Directory.CreateDirectory(outside);
        string system = Path.Combine(_setup, "system");
        string backup = Path.Combine(_root, "system-backup");
        Directory.Move(system, backup);
        _ = Directory.CreateSymbolicLink(system, outside);
        _ = await GitAsync(_repo, "add", "-f", "--", ".lean-worker/system");
        _ = await GitAsync(_repo, "commit", "-m", "track a symlink");
        Directory.Delete(system);
        Directory.Move(backup, system);
        return outside;
    }

    [Fact]
    public async Task A_base_that_tracks_a_symlink_at_a_setup_path_is_refused_and_nothing_is_written_through_itAsync()
    {
        SkipWithoutSymlinks();
        string outside = await TrackSymlinkedSystemFolderAsync();

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await CreateAsync());

        Assert.Contains("tracks a symlink", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task A_launch_whose_base_tracks_a_symlink_at_a_setup_path_is_refused_before_the_worker_runsAsync()
    {
        SkipWithoutSymlinks();
        string outside = await TrackSymlinkedSystemFolderAsync();
        string workerMarker = Path.Combine(_root, "worker-ran");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await LaunchAsync(Success("touch '" + workerMarker + "'"), o =>
        {
            o.Name = RunName;
            o.Worktree = Branch;
            o.WorktreeBase = "HEAD";
        }));

        Assert.Contains("tracks a symlink", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(workerMarker), "the worker must not run");
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // ---- the invoking repository stays in the checks (lean-worker#113) ----

    private static string[] OutOfScope(JsonObject summary) =>
        summary["out_of_scope"] is JsonArray a ? [.. a.Select(n => n!.GetValue<string>())] : [];

    private async Task<(int Code, string Stdout, JsonObject Summary)> LaunchWorktreeAsync(string workerShell, Action<Options>? more = null)
    {
        return await LaunchAsync(Success(workerShell), o =>
        {
            o.Name = RunName;
            o.Worktree = Branch;
            more?.Invoke(o);
        });
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task WriteLockedAsync(string path)
    {
        await File.WriteAllTextAsync(path, "secret\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.None);
    }

    [UnsupportedOSPlatform("windows")]
    private static void Unlock(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task A_worker_that_writes_into_the_invoking_repository_is_reported_with_the_invoking_prefix_and_the_plain_messageAsync()
    {
        (int code, string stdout, JsonObject s) = await LaunchWorktreeAsync("echo leak > '" + _repo + "/leak.txt'");

        Assert.Equal(0, code);
        Assert.Contains("<invoking>/leak.txt", OutOfScope(s), StringComparer.Ordinal);
        Assert.Contains("1 changed in the invoking repository", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("outside the write scope", stdout, StringComparison.Ordinal);
        Assert.Contains("WARNING:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_write_scope_the_invoking_repository_path_counts_as_outside_the_write_scopeAsync()
    {
        (int code, string stdout, JsonObject s) = await LaunchWorktreeAsync("echo leak > '" + _repo + "/leak.txt'", o => o.WriteScope.Add("src/**"));

        Assert.Equal(0, code);
        Assert.Equal(["<invoking>/leak.txt"], OutOfScope(s));
        Assert.Contains("1 outside the write scope", stdout, StringComparison.Ordinal);
        Assert.Contains("changed outside the write scope: <invoking>/leak.txt", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launchers_own_run_directory_and_ledger_in_the_invoking_repository_are_not_reportedAsync()
    {
        (int code, string stdout, JsonObject s) = await LaunchWorktreeAsync("true");

        Assert.Equal(0, code);
        Assert.Empty(OutOfScope(s));
        Assert.DoesNotContain("<invoking>", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("changed in the invoking repository", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("invoking repository not checked", string.Join('\n', RunAsyncGolden.Notes_(s)), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_setup, "runs.jsonl")));
    }

    [Fact]
    public async Task A_gate_PATH_directory_inside_the_invoking_repository_is_refused_like_one_inside_the_worktreeAsync()
    {
        SkipWithoutSymlinks();
        string bin = Path.Combine(_repo, "gatebin");
        _ = Directory.CreateDirectory(bin);
        string gate = Path.Combine(_root, "gate.sh");
        WriteScript(gate, "#!/bin/sh\nexit 0\n");
        RunAsyncGolden.WriteProfile(_setup, "test", GateLoopTests.GateProfile(gate));
        string workerMarker = Path.Combine(_root, "worker-ran");

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await LaunchWorktreeAsync("touch '" + workerMarker + "'", o =>
        {
            o.Profile = "test";
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }));

        Assert.Equal("gate PATH directory '" + bin + "' is inside the working tree", ex.Message);
        Assert.False(File.Exists(workerMarker), "the worker must not run");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_file_in_the_invoking_repository_hashes_as_a_question_mark_and_does_not_crash_the_snapshotAsync()
    {
        SkipWithoutSymlinks();
        string locked = Path.Combine(_repo, "locked.txt");
        await WriteLockedAsync(locked);
        try
        {
            Assert.SkipWhen(CanRead(locked), "the file is readable anyway (running as root)");

            WriteScope.Snapshot snapshot = (await WriteScope.TakeAsync(_repo))!;

            // The marker plus the metadata still readable (#118): size, mtime ticks, unix mode (000 -> 0).
            Assert.Matches(@"^\?\|7\|\d+\|0$", snapshot.Dirty["locked.txt"]);
        }
        finally
        {
            Unlock(locked);
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            _ = File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_worker_that_leaves_an_unreadable_file_in_the_invoking_repository_is_reported_and_the_run_does_not_crashAsync()
    {
        SkipWithoutSymlinks();
        string locked = Path.Combine(_repo, "locked.txt");
        try
        {
            (int code, _, JsonObject s) = await LaunchWorktreeAsync("echo x > '" + locked + "'; chmod 000 '" + locked + "'");

            Assert.Equal(0, code);
            Assert.Contains("<invoking>/locked.txt", OutOfScope(s), StringComparer.Ordinal);
        }
        finally
        {
            Unlock(locked);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_file_already_in_the_invoking_repository_before_the_run_is_not_reportedAsync()
    {
        SkipWithoutSymlinks();
        string locked = Path.Combine(_repo, "locked.txt");
        await WriteLockedAsync(locked);
        try
        {
            (int code, _, JsonObject s) = await LaunchWorktreeAsync("true");

            Assert.Equal(0, code);
            Assert.Empty(OutOfScope(s));
        }
        finally
        {
            Unlock(locked);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_file_in_the_invoking_repository_nobody_touched_is_listed_as_unverified_not_changedAsync()
    {
        SkipWithoutSymlinks();
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "chmod 000 has no effect for root");
        string locked = Path.Combine(_repo, "locked.txt");
        await WriteLockedAsync(locked);
        try
        {
            (int code, _, JsonObject s) = await LaunchWorktreeAsync("true");

            Assert.Equal(0, code);
            Assert.Contains("- unverified: <invoking>/locked.txt", Lines(s), StringComparer.Ordinal);
            Assert.Empty(OutOfScope(s));
        }
        finally
        {
            Unlock(locked);
        }
    }

    [Fact]
    public async Task A_worker_that_writes_the_worktrees_config_worktree_is_listed_as_git_tamper_and_the_run_is_not_a_successAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        string script = "printf '[core]\\n\\thooksPath = /dev/null\\n' > \"$(git -C '" + WorktreePath + "' rev-parse --absolute-git-dir)/config.worktree\"";

        (int code, _, JsonObject s) = await LaunchWorktreeAsync(script);

        Assert.Contains(Lines(s), l => l.StartsWith("- git-tamper: ", StringComparison.Ordinal) && l.EndsWith("/config.worktree", StringComparison.Ordinal));
        Assert.NotEqual("success", s["status"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.NotEqual(0, code);
    }

    [Fact]
    public async Task A_worker_that_writes_the_shared_git_config_of_the_invoking_repository_is_listed_as_git_tamperAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");

        (int code, _, JsonObject s) = await LaunchWorktreeAsync("printf '[core]\\n\\thooksPath = /dev/null\\n' >> '" + Path.Combine(_repo, ".git", "config") + "'");

        Assert.Contains("- git-tamper: .git/config", Lines(s), StringComparer.Ordinal);
        Assert.NotEqual("success", s["status"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.NotEqual(0, code);
    }

    [Fact]
    public async Task A_failed_invoking_repository_snapshot_after_the_worker_is_a_run_noteAsync()
    {
        // The worker breaks the invoking repository's HEAD: git cannot run in it any more.
        (int code, _, JsonObject s) = await LaunchWorktreeAsync("echo garbage > '" + Path.Combine(_repo, ".git", "HEAD") + "'");

        Assert.Equal(0, code);
        Assert.Contains(RunAsyncGolden.Notes_(s), n => n.StartsWith("invoking repository not checked: ", StringComparison.Ordinal));
    }
}
