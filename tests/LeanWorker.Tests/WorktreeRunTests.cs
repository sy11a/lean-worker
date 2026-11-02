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
    public async Task A_branch_that_moved_before_the_removal_survives_and_the_note_says_soAsync()
    {
        WorktreeRun wt = await CreateAsync("HEAD");
        string baseSha = await GitAsync(_repo, "rev-parse", "HEAD");
        // A commit with the same tree: the worktree stays clean after the branch moved onto it.
        string moved = await GitAsync(_repo, "commit-tree", baseSha + "^{tree}", "-p", baseSha, "-m", "moved");

        // The branch moves while the launcher looks at the working tree (after it checked the branch
        // and before it deletes it): git consults core.fsmonitor on every status.
        string hook = Path.Combine(_root, "fsmonitor.sh");
        WriteScript(hook, $"#!/bin/sh\ngit update-ref refs/heads/{Branch} {moved} {baseSha} >/dev/null 2>&1\nexit 1\n");
        _ = await GitAsync(_repo, "config", "core.fsmonitor", hook);

        await wt.FinishAsync();

        Assert.Equal("removed", wt.Action);
        Assert.Equal("empty", wt.Reason);
        Assert.False(Directory.Exists(wt.WorktreePath));
        Assert.NotNull(wt.Note);
        Assert.StartsWith("branch kept: ", wt.Note, StringComparison.Ordinal);
        Assert.Equal(moved, await GitAsync(_repo, "rev-parse", "refs/heads/" + Branch));
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
}
