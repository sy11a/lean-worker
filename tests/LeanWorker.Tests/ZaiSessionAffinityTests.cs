using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The z.ai session-affinity headers that OpencodeRuntime.Prepare adds to the opencode config, and how /// the
/// launcher keeps the user's own provider block and key out of the recorded file.
/// </summary>
public class ZaiSessionAffinityTests
{
    private static RunSpec Spec(string provider)
    {
        return new(
        RunDir: Directory.CreateTempSubdirectory("lw-rundir").FullName, Provider: provider, Model: "glm-5.3",
        Effort: "medium", Variant: null, Tools: ["Read"], Allowed: [], Denied: [], Budget: 2m, WrapUp: false, PermissionMode: "acceptEdits",
        McpConfig: null, SystemFile: null, ReplaceSystemPrompt: false, Mode: "bare", CacheTtl: "5m",
        KeepClaudeMd: true, KeepMemory: true, KeepHooks: false, KeepUserEnv: false, ClaudeSettings: null,
        ProviderInfo: new Provider(provider, o: null));
    }

    /// <summary>
    /// Points XDG_CONFIG_HOME at a fresh directory with the given opencode.json content (or none), and /// restores
    /// the old value on dispose.
    /// </summary>
    private sealed class FakeUserConfig : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        public FakeUserConfig(string? opencodeJson = null)
        {
            string dir = Directory.CreateTempSubdirectory("lw-xdg").FullName;
            if (opencodeJson is not null)
            {
                string opencodeDir = Path.Combine(dir, "opencode");
                _ = Directory.CreateDirectory(opencodeDir);
                File.WriteAllText(Path.Combine(opencodeDir, "opencode.json"), opencodeJson);
            }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old);
    }

    private static JsonObject Headers(Prepared p, string provider) =>
        JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!["provider"]![provider]!["models"]!["glm-5.3"]!["headers"]!.AsObject();

    private static JsonObject Recorded(RunSpec s) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(s.RunDir, "opencode-config.json")))!.AsObject();

    [Fact]
    public void Zai_provider_with_no_user_block_gets_both_headers_recorded_with_no_key()
    {
        using FakeOnPath path = new("opencode");
        using FakeUserConfig cfg = new();
        RunSpec s = Spec("zai-coding-plan");
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject headers = Headers(p, "zai-coding-plan");
            Assert.Equal("lean-worker", headers["x-session-affinity"]!.GetValue<string>());
            Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());

            JsonObject recordedHeaders = Recorded(s)["provider"]!["zai-coding-plan"]!["models"]!["glm-5.3"]!["headers"]!.AsObject();
            Assert.Equal("lean-worker", recordedHeaders["x-session-affinity"]!.GetValue<string>());
            Assert.Equal("lean-worker", recordedHeaders["X-Session-Id"]!.GetValue<string>());
            Assert.DoesNotContain("apiKey", Recorded(s).ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Zai_provider_with_a_non_object_user_entry_does_not_throw_and_gets_the_minimal_block()
    {
        using FakeOnPath path = new("opencode");
        using FakeUserConfig cfg = new(/*lang=json,strict*/ """{ "provider": { "zai-coding-plan": "x" } }""");
        RunSpec s = Spec("zai-coding-plan");
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject headers = Headers(p, "zai-coding-plan");
            Assert.Equal("lean-worker", headers["x-session-affinity"]!.GetValue<string>());
            Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Non_zai_provider_with_a_string_user_entry_passes_through_unchanged()
    {
        using FakeOnPath path = new("opencode");
        using FakeUserConfig cfg = new(/*lang=json,strict*/ """{ "provider": { "anthropic": "x" } }""");
        RunSpec s = Spec("anthropic");
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            Assert.Equal("x", config["provider"]!["anthropic"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void Zai_provider_with_a_user_block_keeps_the_users_fields_and_other_models_and_does_not_record_the_key()
    {
        using FakeOnPath path = new("opencode");
        using FakeUserConfig cfg = new(/*lang=json,strict*/ """
            {
              "provider": {
                "zai-coding-plan": {
                  "options": { "apiKey": "super-secret-key" },
                  "models": { "other-model": { "name": "Other" } }
                }
              }
            }
            """);
        RunSpec s = Spec("zai-coding-plan");
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            JsonObject provider = config["provider"]!["zai-coding-plan"]!.AsObject();
            Assert.Equal("super-secret-key", provider["options"]!["apiKey"]!.GetValue<string>());
            Assert.Equal("Other", provider["models"]!["other-model"]!["name"]!.GetValue<string>());
            JsonObject headers = Headers(p, "zai-coding-plan");
            Assert.Equal("lean-worker", headers["x-session-affinity"]!.GetValue<string>());
            Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());

            string recordedText = Recorded(s).ToJsonString();
            Assert.DoesNotContain("super-secret-key", recordedText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void A_header_the_user_already_set_is_kept_not_duplicated()
    {
        using FakeOnPath path = new("opencode");
        using FakeUserConfig cfg = new(/*lang=json,strict*/ """
            {
              "provider": {
                "zai-coding-plan": {
                  "models": { "glm-5.3": { "headers": { "X-SESSION-AFFINITY": "mine" } } }
                }
              }
            }
            """);
        RunSpec s = Spec("zai-coding-plan");
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject headers = Headers(p, "zai-coding-plan");
            Assert.Equal("mine", headers["X-SESSION-AFFINITY"]!.GetValue<string>());
            Assert.False(headers.ContainsKey("x-session-affinity"));
            Assert.Equal("lean-worker", headers["X-Session-Id"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }

    [Fact]
    public void A_non_zai_provider_gets_no_headers_at_all()
    {
        using FakeOnPath path = new("opencode");
        using FakeUserConfig cfg = new();
        RunSpec s = Spec("minimax-coding-plan");
        Prepared p = new OpencodeRuntime().Prepare(s);
        try
        {
            JsonObject config = JsonNode.Parse(p.Env["OPENCODE_CONFIG_CONTENT"]!)!.AsObject();
            Assert.False(config.ContainsKey("provider"));
        }
        finally
        {
            Directory.Delete(p.ScratchDirectory!, recursive: true);
        }
    }
}
