using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The runtime is handed the stable, content-addressed system path (not a run-dir copy), whichever runtime.
/// </summary>
[Collection("launcher-process-state")]
public class RuntimeSystemPathTests
{
    private static RunSpec Spec(string? systemFile, bool replace = false)
    {
        return new(
        RunDir: Directory.CreateTempSubdirectory("lw-rundir").FullName, Provider: "anthropic", Model: "claude-haiku-4-5",
        Effort: "medium", Variant: null, Tools: ["Read"], Allowed: [], Denied: [], Budget: 2m, WrapUp: false, PermissionMode: "acceptEdits",
        McpConfig: null, SystemFile: systemFile, ReplaceSystemPrompt: replace, Mode: "bare", CacheTtl: "5m",
        KeepClaudeMd: true, KeepMemory: true, KeepHooks: false, KeepUserEnv: false, ClaudeSettings: null,
        ProviderInfo: new Provider("anthropic", o: null));
    }

    [Fact]
    public void Claude_appends_the_given_system_file_path()
    {
        using FakeOnPath path = new("claude");
        Prepared p = new ClaudeRuntime().Prepare(Spec("/stable/system/abc123.md"));
        int i = p.Args.IndexOf("--append-system-prompt-file");
        Assert.True(i >= 0 && p.Args[i + 1] is "/stable/system/abc123.md");
        Assert.DoesNotContain("--system-prompt-file", p.Args, StringComparer.Ordinal);
        Assert.Null(p.ScratchDirectory);
    }

    [Fact]
    public void Claude_replaces_with_the_given_system_file_path_when_asked()
    {
        using FakeOnPath path = new("claude");
        Prepared p = new ClaudeRuntime().Prepare(Spec("/stable/system/abc123.md", replace: true));
        int i = p.Args.IndexOf("--system-prompt-file");
        Assert.True(i >= 0 && p.Args[i + 1] is "/stable/system/abc123.md");
        Assert.DoesNotContain("--append-system-prompt-file", p.Args, StringComparer.Ordinal);
        Assert.Null(p.ScratchDirectory);
    }

    [Fact]
    public void Opencode_instructions_point_to_the_given_system_file_path()
    {
        using FakeOnPath path = new("opencode");
        Prepared p = new OpencodeRuntime().Prepare(Spec("/stable/system/abc123.md"));
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            Assert.Equal(["/stable/system/abc123.md"], config["instructions"]!.AsArray().Select(n => n!.GetValue<string>()), StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }
}
