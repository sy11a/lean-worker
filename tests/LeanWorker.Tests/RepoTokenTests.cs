using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

[Collection("launcher-process-state")]
public class RepoTokenTests
{
    [Fact]
    public void Expand_passes_entries_without_the_token_through_unchanged() =>
        Assert.Equal(["Bash(git diff:*)", "Read"], RepoToken.Expand(["Bash(git diff:*)", "Read"], "/home/u/r"));

    [Fact]
    public void Expand_replaces_one_occurrence_of_the_token() =>
        Assert.Equal(["Bash(git -C /home/u/r diff:*)"], RepoToken.Expand(["Bash(git -C <repo> diff:*)"], "/home/u/r"));

    [Fact]
    public void Expand_replaces_two_occurrences_of_the_token_in_one_entry() =>
        Assert.Equal(["Read(/home/u/r) Read(/home/u/r)"], RepoToken.Expand(["Read(<repo>) Read(<repo>)"], "/home/u/r"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("/home/u/r/")]
    public void Expand_drops_token_entries_but_keeps_others_when_root_is_not_usable(string? root) =>
        Assert.Equal(["Read"], RepoToken.Expand(["Bash(git -C <repo> diff:*)", "Read"], root));

    [Theory]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("\\")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("$")]
    [InlineData("`")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData(":")]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("|")]
    [InlineData("&")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("\r")]
    public void Expand_drops_token_entries_when_the_root_contains_a_forbidden_character(string forbidden)
    {
        string root = "/home/u/r" + forbidden + "x";
        Assert.Equal(["Read"], RepoToken.Expand(["Bash(git -C <repo> diff:*)", "Read"], root));
    }

    [Fact]
    public void Expand_dedupes_after_expansion_keeping_the_first_and_preserving_order()
    {
        Assert.Equal(["Bash(git -C /home/u/r diff:*)", "Read"],
            RepoToken.Expand(["Bash(git -C <repo> diff:*)", "Read", "Bash(git -C <repo> diff:*)"], "/home/u/r"));
    }

    [Fact]
    public void ExpandDenied_matches_Expand_when_the_root_is_usable()
    {
        string[] entries = ["Bash(git -C <repo> push:*)", "Read", "Bash(git -C <repo> push:*)"];
        Assert.Equal(RepoToken.Expand(entries, "/home/u/r"), RepoToken.ExpandDenied(entries, "/home/u/r"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("/home/u/r/")]
    public void ExpandDenied_throws_naming_the_entry_when_root_is_not_usable(string? root)
    {
        LaunchException ex = Assert.Throws<LaunchException>(() =>
            RepoToken.ExpandDenied(["Bash(git -C <repo> push:*)", "Read"], root));
        Assert.Equal(
            $"deniedTools entry Bash(git -C <repo> push:*) needs <repo>, but the working tree has no usable root: {root ?? "none"}",
            ex.Message);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("\\")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("$")]
    [InlineData("`")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData(":")]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("|")]
    [InlineData("&")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("\r")]
    public void ExpandDenied_throws_naming_the_entry_when_the_root_contains_a_forbidden_character(string forbidden)
    {
        string root = "/home/u/r" + forbidden + "x";
        LaunchException ex = Assert.Throws<LaunchException>(() =>
            RepoToken.ExpandDenied(["Bash(git -C <repo> push:*)", "Read"], root));
        Assert.Equal(
            $"deniedTools entry Bash(git -C <repo> push:*) needs <repo>, but the working tree has no usable root: {root}",
            ex.Message);
    }

    [Fact]
    public void ExpandDenied_names_the_first_entry_with_the_token_when_root_is_not_usable()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() =>
            RepoToken.ExpandDenied(["Read", "Bash(git -C <repo> push:*)", "Bash(git -C <repo> pull:*)"], root: null));
        Assert.Equal(
            "deniedTools entry Bash(git -C <repo> push:*) needs <repo>, but the working tree has no usable root: none",
            ex.Message);
    }

    [Fact]
    public void ExpandDenied_passes_entries_without_the_token_through_when_root_is_not_usable() =>
        Assert.Equal(["Read", "Edit"], RepoToken.ExpandDenied(["Read", "Edit"], root: null));

    [Fact]
    public async Task RootAsync_returns_the_root_of_a_freshly_initialized_repoAsync()
    {
        string dir = Directory.CreateTempSubdirectory("lw-repotoken").FullName;
        try
        {
            RunGit(dir, "init", "-q");
            string expected = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? new DirectoryInfo(dir).FullName;
            string? root = await RepoToken.RootAsync(dir);
            Assert.NotNull(root);
            Assert.Equal(expected.TrimEnd('/'), root!.TrimEnd('/'));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RootAsync_from_a_subdirectory_returns_the_same_rootAsync()
    {
        string dir = Directory.CreateTempSubdirectory("lw-repotoken-sub").FullName;
        try
        {
            RunGit(dir, "init", "-q");
            string sub = Directory.CreateDirectory(Path.Combine(dir, "a", "b")).FullName;
            string? rootFromTop = await RepoToken.RootAsync(dir);
            string? rootFromSub = await RepoToken.RootAsync(sub);
            Assert.NotNull(rootFromTop);
            Assert.Equal(rootFromTop, rootFromSub);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RootAsync_outside_a_git_tree_returns_nullAsync()
    {
        string dir = Directory.CreateTempSubdirectory("lw-repotoken-nogit").FullName;
        try { Assert.Null(await RepoToken.RootAsync(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void RunGit(string dir, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo psi = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-C", dir, "-c", "user.email=t@t", "-c", "user.name=t" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void MiniMax_M3_allowed_tools_contain_the_four_git_dash_C_repo_entries_in_prices_json()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "launcher", "prices.json")))
        {
            d = d.Parent;
        }

        JsonNode book = JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName, "skills", "lean-worker", "launcher", "prices.json")))!;
        JsonArray allowed = book["modelTraits"]!["MiniMax-M3"]!["allowedTools"]!.AsArray();
        List<string> entries = [.. allowed.Select(a => a!.GetValue<string>())];
        Assert.Contains("Bash(git -C <repo> diff:*)", entries, StringComparer.Ordinal);
        Assert.Contains("Bash(git -C <repo> status:*)", entries, StringComparer.Ordinal);
        Assert.Contains("Bash(git -C <repo> log:*)", entries, StringComparer.Ordinal);
        Assert.Contains("Bash(git -C <repo> show:*)", entries, StringComparer.Ordinal);
    }

    [Fact]
    public async Task LaunchRun_expands_repo_token_to_the_real_root_in_the_claude_arguments_it_receivesAsync()
    {
        string repoDir = Directory.CreateTempSubdirectory("lw-repotoken-wiring").FullName;
        string stubDir = Directory.CreateTempSubdirectory("lw-repotoken-stub").FullName;
        try
        {
            RunGit(repoDir, "init", "-q");
            string sub = Directory.CreateDirectory(Path.Combine(repoDir, "work")).FullName;
            string? expectedRoot = await RepoToken.RootAsync(sub);
            Assert.NotNull(expectedRoot);

            string command = await RunLaunchWithRepoTokenAsync(sub, stubDir);

            Assert.Contains(Runtimes.Quote($"Bash(git -C {expectedRoot} diff:*)"), command, StringComparison.Ordinal);
            Assert.Contains(Runtimes.Quote($"Bash(git -C {expectedRoot} push:*)"), command, StringComparison.Ordinal);
            Assert.DoesNotContain(RepoToken.Token, command, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
            Directory.Delete(stubDir, recursive: true);
        }
    }

    private static async Task<string> RunLaunchWithRepoTokenAsync(string workingDirectory, string stubDir)
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        try
        {
            string root = RunAsyncGolden.NewRoot();
            JsonObject profiles = new()
            {
                ["profiles"] = new JsonObject
                {
                    ["p1"] = new JsonObject
                    {
                        ["allowedTools"] = new JsonArray("Bash(git -C <repo> diff:*)"),
                        ["deniedTools"] = new JsonArray("Bash(git -C <repo> push:*)"),
                    },
                },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "profiles.json"), profiles.ToJsonString(), TestContext.Current.CancellationToken);

            string stubPath = Path.Combine(stubDir, "claude");
            await File.WriteAllTextAsync(stubPath, "#!/bin/sh\ncat >/dev/null\nexit 0\n", TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
            Directory.SetCurrentDirectory(workingDirectory);
            await using StringWriter outWriter = new();
            TextWriter oldOut = Console.Out;
            Console.SetOut(outWriter);
            string stdout;
            try
            {
                Options o = new() { TaskFile = Path.Combine(root, "task.md"), RunsRoot = root, Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "repo-token-wiring", Profile = "p1" };
                _ = await Launcher.RunAsync(o);
                stdout = outWriter.ToString();
            }
            finally
            {
                Console.SetOut(oldOut);
            }

            string runDir = RunAsyncGolden.RunDirFrom(stdout);
            return await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task LaunchRun_fails_outside_a_git_tree_when_deniedTools_has_the_tokenAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        JsonObject profiles = new()
        {
            ["profiles"] = new JsonObject
            {
                ["p1"] = new JsonObject
                {
                    ["deniedTools"] = new JsonArray("Bash(git -C <repo> push:*)"),
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(root, "profiles.json"), profiles.ToJsonString(), TestContext.Current.CancellationToken);

        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () =>
            await RunAsyncGolden.RunAsync(root, "exit 0", o => o.Profile = "p1"));
        Assert.Equal(
            "deniedTools entry Bash(git -C <repo> push:*) needs <repo>, but the working tree has no usable root: none",
            ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task LaunchRun_launches_outside_a_git_tree_when_only_allowedTools_has_the_tokenAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        JsonObject profiles = new()
        {
            ["profiles"] = new JsonObject
            {
                ["p1"] = new JsonObject
                {
                    ["allowedTools"] = new JsonArray("Bash(git -C <repo> diff:*)"),
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(root, "profiles.json"), profiles.ToJsonString(), TestContext.Current.CancellationToken);

        const string successStream = """
            printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
            """;
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, successStream, o => o.Profile = "p1");
        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        string command = await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("-C", command, StringComparison.Ordinal);
        Assert.DoesNotContain(RepoToken.Token, command, StringComparison.Ordinal);
    }
}
