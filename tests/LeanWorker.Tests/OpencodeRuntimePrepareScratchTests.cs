using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// OpencodeRuntime.Prepare's temporary config home: ScratchDirectory matches the env it hands the worker
/// and holds the plugin, and a failed Prepare (before the directory is created) leaves nothing behind.
/// </summary>
public class OpencodeRuntimePrepareScratchTests
{
    private static RunSpec Spec(List<string> tools, string runDir)
    {
        return new(
        RunDir: runDir, Provider: "anthropic", Model: "claude-haiku-4-5",
        Effort: "medium", Variant: null, Tools: tools, Allowed: [], Denied: [], Budget: 2m, WrapUp: false, PermissionMode: "acceptEdits",
        McpConfig: null, SystemFile: null, ReplaceSystemPrompt: false, Mode: "bare", CacheTtl: "5m",
        KeepClaudeMd: true, KeepMemory: true, KeepHooks: false, KeepUserEnv: false, ClaudeSettings: null,
        ProviderInfo: new Provider("anthropic", o: null));
    }

    [Fact]
    public void ScratchDirectory_matches_XDG_CONFIG_HOME_and_holds_the_plugin()
    {
        using FakeOnPath path = new("opencode");
        Prepared p = new OpencodeRuntime().Prepare(Spec(["Read"], Directory.CreateTempSubdirectory("lw-rundir").FullName));
        try
        {
            Assert.NotNull(p.ScratchDirectory);
            Assert.Equal(p.Env["XDG_CONFIG_HOME"], p.ScratchDirectory);
            Assert.True(Directory.Exists(p.ScratchDirectory));
            Assert.True(File.Exists(Path.Combine(p.ScratchDirectory!, "opencode", "plugins", "lean-worker.ts")));
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void A_tool_with_no_opencode_equivalent_leaves_no_scratch_directory_behind()
    {
        using FakeOnPath path = new("opencode");
        string runDir = Directory.CreateTempSubdirectory("lw-rundir").FullName;
        string prefix = $"lean-worker-{Path.GetFileName(runDir)}-";

        _ = Assert.Throws<LaunchException>(() => new OpencodeRuntime().Prepare(Spec(["NoSuchTool"], runDir)));

        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), prefix + "*"));
    }
}
