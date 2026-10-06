using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The deny floor (Launcher.DeniedFloor), how RunSpec.Denied is populated and used by both runtimes, and that
/// prices.json no longer pre-approves `sed -n` for MiniMax-M3 now that the floor denies its diff/log-escape
/// flags regardless of profile.
/// </summary>
public class DenyFloorTests
{
    private static RunSpec Spec(List<string> tools, List<string> allowed, List<string> denied) => new(
        RunDir: Directory.CreateTempSubdirectory("lw-rundir").FullName, Provider: "anthropic", Model: "claude-haiku-4-5",
        Effort: "medium", Variant: null, Tools: tools, Allowed: allowed, Denied: denied, Budget: 2m, WrapUp: false,
        PermissionMode: "acceptEdits", McpConfig: null, SystemFile: null, ReplaceSystemPrompt: false, Mode: "bare",
        CacheTtl: "5m", KeepClaudeMd: true, KeepMemory: true, KeepHooks: false, KeepUserEnv: false, ClaudeSettings: null,
        ProviderInfo: new Provider("anthropic", o: null));

    [Fact]
    public void Denied_floor_is_the_three_fixed_git_escape_patterns()
    {
        Assert.Equal(
            ["Bash(git *--output*)", "Bash(git *--ext-diff*)", "Bash(git *--textconv*)"],
            Launcher.DeniedFloor);
    }

    [Fact]
    public void Claude_runtime_adds_disallowedTools_right_after_allowedTools_when_denied_is_non_empty()
    {
        using FakeOnPath path = new("claude");
        RunSpec s = Spec(["Read", "Bash"], ["Bash(ls:*)"], [.. Launcher.DeniedFloor]);
        Prepared p = new ClaudeRuntime().Prepare(s);

        int allowedIdx = p.Args.IndexOf("--allowedTools");
        Assert.True(allowedIdx >= 0);
        Assert.Equal("Bash(ls:*)", p.Args[allowedIdx + 1]);
        int deniedIdx = allowedIdx + 2;
        Assert.Equal("--disallowedTools", p.Args[deniedIdx]);
        for (int i = 0; i < Launcher.DeniedFloor.Length; i++)
        {
            Assert.Equal(Launcher.DeniedFloor[i], p.Args[deniedIdx + 1 + i]);
        }

        string afterLastDeny = p.Args[deniedIdx + 1 + Launcher.DeniedFloor.Length];
        Assert.StartsWith("--", afterLastDeny, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_runtime_adds_no_disallowedTools_when_denied_is_empty()
    {
        using FakeOnPath path = new("claude");
        RunSpec s = Spec(["Read"], [], []);
        Prepared p = new ClaudeRuntime().Prepare(s);

        Assert.DoesNotContain("--disallowedTools", p.Args);
    }

    [Fact]
    public void Opencode_bash_permission_lists_every_allow_entry_before_any_deny_entry()
    {
        using FakeOnPath path = new("opencode");
        RunSpec s = Spec(["Read", "Bash"], ["Bash(ls:*)", "Bash(pwd)"], [.. Launcher.DeniedFloor]);
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject bash = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["permission"]!["bash"]!.AsObject();
            List<string> keys = [.. bash.Select(kv => kv.Key)];

            Assert.Equal("*", keys[0]);
            Assert.Equal("deny", bash["*"]!.GetValue<string>());

            List<string> rest = [.. keys.Skip(1)];
            Assert.Equal("allow", bash["ls*"]!.GetValue<string>());
            Assert.Equal("allow", bash["pwd"]!.GetValue<string>());
            Assert.Equal("deny", bash["git *--output*"]!.GetValue<string>());
            Assert.Equal("deny", bash["git *--ext-diff*"]!.GetValue<string>());
            Assert.Equal("deny", bash["git *--textconv*"]!.GetValue<string>());

            int firstDenyIdx = rest.FindIndex(k => k is "git *--output*" or "git *--ext-diff*" or "git *--textconv*");
            Assert.True(firstDenyIdx >= 0);
            int lastAllowIdx = rest.FindLastIndex(k => k is "ls*" or "pwd");
            Assert.True(rest.Take(firstDenyIdx).All(k => bash[k]!.GetValue<string>() == "allow"));
            Assert.True(lastAllowIdx < firstDenyIdx);
            Assert.True(rest.Skip(firstDenyIdx).All(k => bash[k]!.GetValue<string>() == "deny"));
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Opencode_bash_permission_puts_a_denied_pattern_after_a_broader_later_allow_so_it_still_wins()
    {
        using FakeOnPath path = new("opencode");
        RunSpec s = Spec(["Read", "Bash"], ["Bash(git push:*)", "Bash(git:*)"], [.. Launcher.DeniedFloor, "Bash(git push:*)"]);
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            foreach (JsonObject permission in new[] { config["permission"]!.AsObject(), config["agent"]!["lean-worker"]!["permission"]!.AsObject() })
            {
                JsonObject bash = permission["bash"]!.AsObject();
                List<string> keys = [.. bash.Select(kv => kv.Key)];

                Assert.Equal("deny", bash["git push*"]!.GetValue<string>());
                int gitPushIdx = keys.IndexOf("git push*");
                int gitIdx = keys.IndexOf("git*");
                Assert.True(gitPushIdx > gitIdx, $"'git push*' (index {gitPushIdx}) must come after 'git*' (index {gitIdx}) so the deny wins: {string.Join(", ", keys)}");
            }
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Opencode_bash_permission_starts_with_a_wildcard_deny_so_unlisted_commands_are_denied()
    {
        using FakeOnPath path = new("opencode");
        RunSpec s = Spec(["Read", "Bash"], ["Bash(ls:*)"], []);
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject bash = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["permission"]!["bash"]!.AsObject();
            List<string> keys = [.. bash.Select(kv => kv.Key)];

            Assert.True(bash.ContainsKey("*"));
            Assert.Equal("deny", bash["*"]!.GetValue<string>());
            Assert.Equal("*", keys[0]);
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    public void Opencode_edit_permission_denies_dot_git_in_the_fixed_order_in_both_permission_blocks(string tool)
    {
        using FakeOnPath path = new("opencode");
        RunSpec s = Spec(["Read", tool], [], []);
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            foreach (JsonObject permission in new[] { config["permission"]!.AsObject(), config["agent"]!["lean-worker"]!["permission"]!.AsObject() })
            {
                JsonObject edit = permission["edit"]!.AsObject();
                Assert.Equal(["*", ".git/**", "**/.git/**", ".git", "**/.git"], edit.Select(kv => kv.Key));
                Assert.Equal("allow", edit["*"]!.GetValue<string>());
                Assert.Equal("deny", edit[".git/**"]!.GetValue<string>());
                Assert.Equal("deny", edit["**/.git/**"]!.GetValue<string>());
                Assert.Equal("deny", edit[".git"]!.GetValue<string>());
                Assert.Equal("deny", edit["**/.git"]!.GetValue<string>());
            }
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Opencode_bash_permission_under_bypassPermissions_allows_everything_but_still_denies_the_floor()
    {
        using FakeOnPath path = new("opencode");
        RunSpec s = Spec(["Read", "Bash"], ["Bash(ls:*)"], [.. Launcher.DeniedFloor]) with { PermissionMode = "bypassPermissions" };
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject bash = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["permission"]!["bash"]!.AsObject();
            List<string> keys = [.. bash.Select(kv => kv.Key)];

            Assert.Equal("*", keys[0]);
            Assert.Equal("allow", bash["*"]!.GetValue<string>());
            Assert.DoesNotContain("ls*", keys);
            Assert.Equal(["*", "git *--output*", "git *--ext-diff*", "git *--textconv*"], keys);
            foreach (string k in keys.Skip(1))
            {
                Assert.Equal("deny", bash[k]!.GetValue<string>());
            }
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Claude_runtime_adds_disallowedTools_even_under_bypassPermissions()
    {
        using FakeOnPath path = new("claude");
        RunSpec s = Spec(["Read", "Bash"], [], [.. Launcher.DeniedFloor]) with { PermissionMode = "bypassPermissions" };
        Prepared p = new ClaudeRuntime().Prepare(s);

        int deniedIdx = p.Args.IndexOf("--disallowedTools");
        Assert.True(deniedIdx >= 0);
        for (int i = 0; i < Launcher.DeniedFloor.Length; i++)
        {
            Assert.Equal(Launcher.DeniedFloor[i], p.Args[deniedIdx + 1 + i]);
        }
    }

    [Fact]
    public void Opencode_permission_has_no_edit_key_without_edit_or_write()
    {
        using FakeOnPath path = new("opencode");
        RunSpec s = Spec(["Read", "Glob"], [], []);
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            Assert.False(config["permission"]!.AsObject().ContainsKey("edit"));
            Assert.False(config["agent"]!["lean-worker"]!["permission"]!.AsObject().ContainsKey("edit"));
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchRun_sets_denied_to_the_floor_plus_the_profiles_deniedTools_floor_first_no_duplicatesAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        JsonObject profiles = new()
        {
            ["profiles"] = new JsonObject
            {
                ["p1"] = new JsonObject { ["deniedTools"] = new JsonArray(Launcher.DeniedFloor[0], "Bash(curl:*)") },
            },
        };
        File.WriteAllText(Path.Combine(root, "profiles.json"), profiles.ToJsonString());
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, "exit 0", o => o.Profile = "p1");
        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        string command = await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);

        List<string> expected = [.. Launcher.DeniedFloor, "Bash(curl:*)"];
        int pos = command.IndexOf("--disallowedTools", StringComparison.Ordinal);
        Assert.True(pos >= 0);
        foreach (string entry in expected)
        {
            int next = command.IndexOf(Runtimes.Quote(entry), pos, StringComparison.Ordinal);
            Assert.True(next > pos, $"'{entry}' not found in order in: {command}");
            pos = next;
        }
    }

    [Fact]
    public async Task LaunchRun_leaves_denied_empty_when_tools_have_no_bashAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, "exit 0", o => o.Tools = ["Read"]);
        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        string command = await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("--disallowedTools", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchRun_denied_floor_survives_a_cli_allow_override_of_the_profiles_allowedToolsAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        JsonObject profiles = new()
        {
            ["profiles"] = new JsonObject
            {
                ["p1"] = new JsonObject { ["allowedTools"] = new JsonArray("Bash(pwd)") },
            },
        };
        File.WriteAllText(Path.Combine(root, "profiles.json"), profiles.ToJsonString());
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, "exit 0", o =>
        {
            o.Profile = "p1";
            o.AllowedTools.Add("Bash(ls:*)"); // --allow on the CLI replaces the profile's allowedTools entirely
        });
        string runDir = RunAsyncGolden.RunDirFrom(stdout);
        string command = await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);

        Assert.Contains(Runtimes.Quote("Bash(ls:*)"), command, StringComparison.Ordinal);
        Assert.DoesNotContain(Runtimes.Quote("Bash(pwd)"), command, StringComparison.Ordinal);

        int pos = command.IndexOf("--disallowedTools", StringComparison.Ordinal);
        Assert.True(pos >= 0);
        foreach (string entry in Launcher.DeniedFloor)
        {
            int next = command.IndexOf(Runtimes.Quote(entry), pos, StringComparison.Ordinal);
            Assert.True(next > pos, $"'{entry}' not found in order in: {command}");
            pos = next;
        }
    }

    [Fact]
    public async Task LaunchRun_denied_floor_survives_model_traits_adding_allowed_patternsAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string? oldKey = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        Environment.SetEnvironmentVariable("MINIMAX_API_KEY", "dummy-minimax-key");
        try
        {
            (_, string stdout) = await RunAsyncGolden.RunAsync(root, "exit 0", o =>
            {
                o.Model = "minimax/MiniMax-M3";
                o.Mode = "auto";
            });
            string runDir = RunAsyncGolden.RunDirFrom(stdout);
            string command = await File.ReadAllTextAsync(Path.Combine(runDir, "command.txt"), TestContext.Current.CancellationToken);
            JsonObject summary = RunAsyncGolden.Summary(runDir);
            Assert.Equal("MiniMax-M3", Json.Str(summary, "model_traits"));
            Assert.Contains(Runtimes.Quote("Bash(head:*)"), command, StringComparison.Ordinal);

            int pos = command.IndexOf("--disallowedTools", StringComparison.Ordinal);
            Assert.True(pos >= 0);
            foreach (string entry in Launcher.DeniedFloor)
            {
                int next = command.IndexOf(Runtimes.Quote(entry), pos, StringComparison.Ordinal);
                Assert.True(next > pos, $"'{entry}' not found in order in: {command}");
                pos = next;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINIMAX_API_KEY", oldKey);
        }
    }

    [Fact]
    public void MiniMax_M3_no_longer_pre_approves_sed_n_in_prices_json()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "launcher", "prices.json")))
        {
            d = d.Parent;
        }

        JsonNode book = JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName, "skills", "lean-worker", "launcher", "prices.json")))!;
        JsonArray allowed = book["modelTraits"]!["MiniMax-M3"]!["allowedTools"]!.AsArray();
        Assert.DoesNotContain("Bash(sed -n:*)", allowed.Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void MiniMax_M3_pre_approves_git_show_in_prices_json()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "launcher", "prices.json")))
        {
            d = d.Parent;
        }

        JsonNode book = JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName, "skills", "lean-worker", "launcher", "prices.json")))!;
        JsonArray allowed = book["modelTraits"]!["MiniMax-M3"]!["allowedTools"]!.AsArray();
        Assert.Contains("Bash(git show:*)", allowed.Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void MiniMax_M3_pre_approves_od_but_not_cat_in_prices_json()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "launcher", "prices.json")))
        {
            d = d.Parent;
        }

        JsonNode book = JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName, "skills", "lean-worker", "launcher", "prices.json")))!;
        JsonArray allowed = book["modelTraits"]!["MiniMax-M3"]!["allowedTools"]!.AsArray();
        List<string> entries = [.. allowed.Select(a => a!.GetValue<string>())];
        Assert.Contains("Bash(od:*)", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("Bash(cat", StringComparison.Ordinal));
    }

    [Fact]
    public void MiniMax_M3_od_allow_reaches_both_runtimes()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "launcher", "prices.json")))
        {
            d = d.Parent;
        }

        JsonObject doc = JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName, "skills", "lean-worker", "launcher", "prices.json")))!.AsObject();
        ModelTraits? traits = PriceBook.FromJson(doc).Traits("MiniMax-M3");
        Assert.NotNull(traits);
        List<string> allowed = [.. traits!.AllowedTools];
        List<string> denied = [.. Launcher.DeniedFloor];
        RunSpec s = Spec(["Read", "Bash"], allowed, denied);

        using (new FakeOnPath("opencode"))
        {
            Prepared p = new OpencodeRuntime().Prepare(s);
            try
            {
                JsonObject bash = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["permission"]!["bash"]!.AsObject();
                Assert.Equal("allow", bash["od*"]!.GetValue<string>());
                Assert.False(bash.ContainsKey("cat*"));
            }
            finally
            {
                Directory.Delete(p.ScratchDirectory!, recursive: true);
            }
        }

        using (new FakeOnPath("claude"))
        {
            Prepared p = new ClaudeRuntime().Prepare(s);
            Assert.Contains("Bash(od:*)", p.Args);
        }
    }

    [Fact]
    public void MiniMax_M3_deny_floor_wins_over_the_git_show_allow_in_both_runtimes()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "launcher", "prices.json")))
        {
            d = d.Parent;
        }

        JsonObject doc = JsonNode.Parse(File.ReadAllText(Path.Combine(d!.FullName, "skills", "lean-worker", "launcher", "prices.json")))!.AsObject();
        ModelTraits? traits = PriceBook.FromJson(doc).Traits("MiniMax-M3");
        Assert.NotNull(traits);
        List<string> allowed = [.. traits!.AllowedTools];
        List<string> denied = [.. Launcher.DeniedFloor];
        RunSpec s = Spec(["Read", "Bash"], allowed, denied);

        using (new FakeOnPath("opencode"))
        {
            Prepared p = new OpencodeRuntime().Prepare(s);
            try
            {
                JsonObject bash = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["permission"]!["bash"]!.AsObject();
                List<string> keys = [.. bash.Select(kv => kv.Key)];

                Assert.Equal("allow", bash["git show*"]!.GetValue<string>());
                int gitShowIdx = keys.IndexOf("git show*");
                Assert.True(gitShowIdx >= 0);
                foreach (string floorKey in new[] { "git *--output*", "git *--ext-diff*", "git *--textconv*" })
                {
                    int floorIdx = keys.IndexOf(floorKey);
                    Assert.True(floorIdx > gitShowIdx, $"'{floorKey}' (index {floorIdx}) must come after 'git show*' (index {gitShowIdx}) so the deny wins: {string.Join(", ", keys)}");
                }
            }
            finally
            {
                Directory.Delete(p.ScratchDirectory!, recursive: true);
            }
        }

        using (new FakeOnPath("claude"))
        {
            Prepared p = new ClaudeRuntime().Prepare(s);
            Assert.Contains("Bash(git show:*)", p.Args);

            int pos = p.Args.IndexOf("--disallowedTools");
            Assert.True(pos >= 0);
            foreach (string entry in Launcher.DeniedFloor)
            {
                int next = p.Args.IndexOf(entry, pos);
                Assert.True(next > pos, $"'{entry}' not found in order in: {string.Join(" ", p.Args)}");
                pos = next;
            }
        }
    }
}
