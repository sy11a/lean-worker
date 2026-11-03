using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The test round of lean-worker#119: a run cut off by the output limit is truncated (#115), an unreadable file is
/// compared by its metadata and reported as unverified (#118), the repository's own git config and hooks are watched
/// (#114) and the worker's process group is killed when the worker exits (#116). Full runs against stub workers.
/// </summary>
[Collection("launcher-process-state")]
[UnsupportedOSPlatform("windows")]
public sealed class RunHardeningTests : IDisposable
{
    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    // ---- helpers ----

    private static string[] Evidence(JsonObject summary) => [.. summary["evidence_lines"]!.AsArray().Select(n => n!.GetValue<string>())];

    private static void HasLine(JsonObject summary, string line) => Assert.Contains(line, Evidence(summary), StringComparer.Ordinal);

    private static void LacksLine(JsonObject summary, string line) => Assert.DoesNotContain(line, Evidence(summary), StringComparer.Ordinal);

    private static string[] ChangedFiles(JsonObject summary) =>
        summary["changed_files"] is JsonArray a ? [.. a.Select(n => n!.GetValue<string>())] : [];

    private static async Task GitAsync(string dir, params string[] args)
    {
        Git.Result result = await Git.RunResultAsync(dir, args) ?? throw new InvalidOperationException("git did not run");
        Assert.True(result.ExitCode is 0, $"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    /// <summary>
    /// A git repository with one commit, whose .lean-worker folder is the runs root (task and project notes in it).
    /// </summary>
    private async Task<(string Repo, string Root)> NewRepoAsync()
    {
        string repo = _dirs.Create("lw-hard");
        await GitAsync(repo, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "base\n", TestContext.Current.CancellationToken);
        await GitAsync(repo, "add", "--", "README.md");
        await GitAsync(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-m", "base");
        string root = Path.Combine(repo, ".lean-worker");
        _ = Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "project.md"), RunAsyncGolden.Notes, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "task.md"), "do nothing", TestContext.Current.CancellationToken);
        return (repo, root);
    }

    private static async Task<(int Code, string Stdout, JsonObject Summary)> RunAsync(string root, string workerShell)
    {
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, workerShell + "\n" + RunAsyncGolden.SuccessStream);
        return (code, stdout, RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout)));
    }

    private static void SkipUnlessChmodWorks() =>
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs a non-root POSIX user (chmod 000 has no effect for root)");

    private static void Unlock(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---- #115: a run cut off by the output limit ----

    private async Task<(int Code, JsonObject Summary)> RunOpencodeAsync(string stepFinishTokensAndReason, bool withText = true)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        string root = _dirs.NewRoot();
        string stubDir = _dirs.Create("lw-oc");
        string stub = Path.Combine(stubDir, "opencode");
        string text = withText ? "printf '%s\\n' '{\"type\":\"text\",\"sessionID\":\"s1\",\"part\":{\"messageID\":\"m1\",\"text\":\"DONE\"}}'\n" : string.Empty;
        string step = "printf '%s\\n' '{\"type\":\"step_finish\",\"sessionID\":\"s1\",\"part\":{\"id\":\"p1\"," + stepFinishTokensAndReason + "}}'\n";
        await File.WriteAllTextAsync(stub, "#!/bin/sh\ncat >/dev/null\n" + text + step, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        string cwd = _dirs.Create("lw-cwd");
        TextWriter oldOut = Console.Out;
        await using StringWriter outWriter = new();
        try
        {
            Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
            Directory.SetCurrentDirectory(cwd);
            Console.SetOut(outWriter);
            Options o = new() { TaskFile = Path.Combine(root, "task.md"), RunsRoot = root, Runtime = "opencode", Model = "anthropic/claude-haiku-4-5", Mode = "auto", Name = Guid.NewGuid().ToString("N") };
            int code = await Launcher.RunAsync(o);
            return (code, RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(outWriter.ToString())));
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    private static string Tokens(int output, string reason) =>
        "\"reason\":\"" + reason + "\",\"tokens\":{\"input\":10,\"output\":" + output.ToString(CultureInfo.InvariantCulture) + ",\"reasoning\":0,\"cache\":{\"write\":0,\"read\":0}}";

    [Fact]
    public async Task An_opencode_run_whose_last_step_hit_the_length_limit_with_no_output_is_truncated_and_exits_non_zeroAsync()
    {
        (int code, JsonObject s) = await RunOpencodeAsync(Tokens(0, "length"));

        Assert.NotEqual(0, code);
        Assert.Equal("truncated", s["status"]!.GetValue<string>());
        Assert.Equal("length", s["terminal_reason"]!.GetValue<string>());
        HasLine(s, "- status: truncated");
    }

    [Fact]
    public async Task An_opencode_run_whose_last_step_hit_the_length_limit_but_produced_output_is_not_truncatedAsync()
    {
        (int code, JsonObject s) = await RunOpencodeAsync(Tokens(50, "length"));

        Assert.NotEqual("truncated", s["status"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("length", s["terminal_reason"]!.GetValue<string>());
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task An_opencode_run_that_stopped_normally_is_unchangedAsync()
    {
        (int code, JsonObject s) = await RunOpencodeAsync(Tokens(0, "stop"));

        Assert.Equal(0, code);
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.NotEqual("length", s["terminal_reason"]?.GetValue<string>(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_claude_run_whose_result_says_length_with_no_output_tokens_is_truncatedAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (int code, string stdout) = await RunAsyncGolden.RunAsync(root,
            "printf '%s\\n' '{\"type\":\"result\",\"subtype\":\"success\",\"terminal_reason\":\"length\",\"is_error\":false,\"result\":\"DONE\",\"session_id\":\"s1\",\"total_cost_usd\":0.01,\"num_turns\":1,\"usage\":{\"output_tokens\":0},\"permission_denials\":[]}'");
        JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));

        Assert.NotEqual(0, code);
        Assert.Equal("truncated", s["status"]!.GetValue<string>());
        Assert.Equal("length", s["terminal_reason"]!.GetValue<string>());
    }

    // ---- #118: unreadable files are compared by their metadata ----

    [Fact]
    public async Task A_chmod_000_file_the_worker_rewrote_between_the_snapshots_is_reported_as_changedAsync()
    {
        SkipUnlessChmodWorks();
        (string repo, string root) = await NewRepoAsync();
        string locked = Path.Combine(repo, "locked.txt");
        await File.WriteAllTextAsync(locked, "secret\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            (int code, _, JsonObject s) = await RunAsync(root, "chmod 600 '" + locked + "'; echo more-bytes >> '" + locked + "'; chmod 000 '" + locked + "'");

            Assert.Contains("locked.txt", ChangedFiles(s), StringComparer.Ordinal);
            HasLine(s, "- changed: locked.txt");
            LacksLine(s, "- unverified: locked.txt");
            Assert.Equal(0, code);
        }
        finally
        {
            Unlock(locked);
        }
    }

    [Fact]
    public async Task A_chmod_000_file_nobody_touched_is_listed_as_unverified_and_not_as_changedAsync()
    {
        SkipUnlessChmodWorks();
        (string repo, string root) = await NewRepoAsync();
        string locked = Path.Combine(repo, "locked.txt");
        await File.WriteAllTextAsync(locked, "secret\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            (int code, _, JsonObject s) = await RunAsync(root, "true");

            HasLine(s, "- unverified: locked.txt");
            Assert.DoesNotContain("locked.txt", ChangedFiles(s), StringComparer.Ordinal);
            LacksLine(s, "- changed: locked.txt");
            Assert.Contains("unverified: locked.txt", RunAsyncGolden.Notes_(s), StringComparer.Ordinal);
            Assert.Equal(0, code);
        }
        finally
        {
            Unlock(locked);
        }
    }

    // ---- #114: the shared git config and hooks are watched ----

    private static string AppendConfig(string repo, string section) =>
        "printf '%b' '" + section + "' >> '" + Path.Combine(repo, ".git", "config") + "'";

    [Theory]
    [InlineData("hooksPath")]
    [InlineData("fsmonitor")]
    public async Task A_worker_that_writes_the_git_config_is_listed_as_git_tamper_and_the_run_is_not_a_successAsync(string key)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        (string repo, string root) = await NewRepoAsync();
        string ran = Path.Combine(_dirs.Create("lw-marker"), "ran");
        string monitor = Path.Combine(Path.GetDirectoryName(ran)!, "monitor.sh");
        await File.WriteAllTextAsync(monitor, "#!/bin/sh\ntouch '" + ran + "'\nexit 1\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(monitor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string value = key is "hooksPath" ? "/dev/null" : monitor;

        (int code, _, JsonObject s) = await RunAsync(root, AppendConfig(repo, "[core]\\n\\t" + key + " = " + value + "\\n"));

        HasLine(s, "- git-tamper: .git/config");
        Assert.NotEqual("success", s["status"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(ran), "the launcher ran the fsmonitor the worker wrote");
    }

    [Fact]
    public async Task A_worker_that_adds_a_file_under_the_git_hooks_is_listed_as_git_tamper_and_the_run_is_not_a_successAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        (string repo, string root) = await NewRepoAsync();
        string hook = Path.Combine(repo, ".git", "hooks", "pre-commit");

        (int code, _, JsonObject s) = await RunAsync(root, "printf '#!/bin/sh\\nexit 0\\n' > '" + hook + "'");

        HasLine(s, "- git-tamper: .git/hooks/pre-commit");
        Assert.NotEqual("success", s["status"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.NotEqual(0, code);
    }

    [Fact]
    public async Task A_run_that_touches_no_git_metadata_has_no_git_tamper_lineAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");
        (string repo, string root) = await NewRepoAsync();

        (int code, string stdout, JsonObject s) = await RunAsync(root, "echo work > '" + Path.Combine(repo, "work.txt") + "'");

        Assert.DoesNotContain(Evidence(s), l => l.StartsWith("- git-tamper:", StringComparison.Ordinal));
        Assert.DoesNotContain("git metadata", stdout, StringComparison.Ordinal);
        Assert.Equal("success", s["status"]!.GetValue<string>());
        Assert.Equal(0, code);
    }

    // ---- #116: the worker's process group is killed when the worker exits ----

    private static bool IsAlive(int pid)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            // A killed process nobody reaped yet is a zombie ("Z"): dead for every purpose here.
            return !stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z');
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void SkipUnlessGroupWorks() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() && ProcessGroup.Available, "needs Linux with setsid");

    [Fact]
    public async Task A_background_process_the_worker_leaves_behind_is_dead_when_the_launcher_returns_and_the_leftovers_are_countedAsync()
    {
        SkipUnlessGroupWorks();
        string root = _dirs.NewRoot();
        string pidFile = Path.Combine(_dirs.Create("lw-pid"), "pid");
        int pid = 0;
        try
        {
            (int code, string stdout) = await RunAsyncGolden.RunAsync(root,
                "sh -c 'sleep 300 >/dev/null 2>&1 </dev/null & echo $! > \"$1\"' sh '" + pidFile + "'\n" + RunAsyncGolden.SuccessStream);
            JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));
            pid = int.Parse((await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)).Trim(), CultureInfo.InvariantCulture);

            Assert.False(IsAlive(pid), "the sleep the worker left behind survived the launcher");
            string leftovers = Assert.Single(Evidence(s), l => l.StartsWith("- leftovers: ", StringComparison.Ordinal));
            Assert.True(int.Parse(leftovers["- leftovers: ".Length..], CultureInfo.InvariantCulture) >= 1, leftovers);
            Assert.Equal(0, code);
        }
        finally
        {
            if (pid > 0 && IsAlive(pid))
            {
                using System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill();
            }
        }
    }

    [Fact]
    public async Task A_worker_that_leaves_nothing_behind_has_a_leftovers_line_of_zeroAsync()
    {
        SkipUnlessGroupWorks();
        string root = _dirs.NewRoot();

        (int code, string stdout) = await RunAsyncGolden.RunAsync(root, RunAsyncGolden.SuccessStream);
        JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));

        HasLine(s, "- leftovers: 0");
        Assert.Equal(0, code);
    }
}
