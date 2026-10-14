using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Options.Parse over every text option and every flag: each sets its own field and nothing else fails.
/// </summary>
public class OptionsParseTests
{
    [Fact]
    public void Every_text_option_sets_its_field_from_the_next_argument()
    {
        Options o = Options.Parse([
            "--task", "task.md", "--continue-from", "run-dir", "--profile", "code", "--system", "notes.md",
            "--name", "n1", "--runtime", "opencode", "--model", "zai/glm-5.3", "--effort", "high",
            "--variant", "fast", "--mcp-config", "mcp.json", "--prices", "p.json", "--permission-mode", "dontAsk",
            "--runs-root", "rr", "--claude-settings", "s.json", "--mode", "bare", "--cache-ttl", "1h",
        ]);

        Assert.Equal("task.md", o.TaskFile);
        Assert.Equal("run-dir", o.ContinueFrom);
        Assert.Equal("code", o.Profile);
        Assert.Equal("notes.md", o.SystemFile);
        Assert.Equal("n1", o.Name);
        Assert.Equal("opencode", o.Runtime);
        Assert.Equal("zai/glm-5.3", o.Model);
        Assert.Equal("high", o.Effort);
        Assert.Equal("fast", o.Variant);
        Assert.Equal("mcp.json", o.McpConfig);
        Assert.Equal("p.json", o.PricesFile);
        Assert.Equal("dontAsk", o.PermissionMode);
        Assert.Equal("rr", o.RunsRoot);
        Assert.Equal("s.json", o.ClaudeSettings);
        Assert.Equal("bare", o.Mode);
        Assert.Equal("1h", o.CacheTtl);
    }

    [Fact]
    public void Text_options_are_unset_and_mode_is_auto_without_arguments()
    {
        Options o = Options.Parse([]);

        Assert.Null(o.TaskFile);
        Assert.Null(o.Model);
        Assert.Equal("auto", o.Mode);
        Assert.False(o.Help);
    }

    [Theory]
    [InlineData("--task")]
    [InlineData("--model")]
    [InlineData("--cache-ttl")]
    public void A_text_option_without_a_value_fails(string option)
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => Options.Parse([option]));

        Assert.Equal($"{option} needs a value", ex.Message);
    }

    [Fact]
    public void Every_flag_sets_its_field()
    {
        Options o = Options.Parse([
            "--no-project-notes", "--replace-system-prompt", "--keep-claude-md", "--keep-memory", "--keep-hooks",
            "--no-user-env", "--no-hooks", "--no-gate", "--help",
        ]);

        Assert.True(o.NoProjectNotes);
        Assert.True(o.ReplaceSystemPrompt);
        Assert.True(o.KeepClaudeMd);
        Assert.True(o.KeepMemory);
        Assert.True(o.KeepHooks);
        Assert.True(o.NoUserEnv);
        Assert.True(o.NoGate);
        Assert.True(o.Help);
    }

    [Fact]
    public void Short_help_flag_sets_help() => Assert.True(Options.Parse(["-h"]).Help);

    [Fact]
    public void No_bare_is_an_alias_for_lean_mode() => Assert.Equal("lean", Options.Parse(["--no-bare"]).Mode);

    [Fact]
    public void Flags_and_text_options_mix_in_any_order()
    {
        Options o = Options.Parse(["--no-gate", "--task", "t.md", "--timeout-minutes", "5", "--keep-memory"]);

        Assert.True(o.NoGate);
        Assert.Equal("t.md", o.TaskFile);
        Assert.Equal(5, o.TimeoutMinutes);
        Assert.True(o.KeepMemory);
    }

    [Fact]
    public void An_unknown_option_fails_with_a_pointer_to_help()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => Options.Parse(["--bogus"]));

        Assert.Equal("unknown option '--bogus' (see --help)", ex.Message);
    }
}
