// Runtimes: how a worker is started and how its output stream is read. Claude Code (`claude -p`) and
// opencode (`opencode run`) are supported; both stream JSON lines the launcher meters live.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class ClaudeRuntime : IRuntime
{
    public string Name => "claude";

    public Prepared Prepare(RunSpec s)
    {
        string claude = Launcher.FindOnPath("claude") ?? throw new LaunchException("'claude' is not on PATH.");
        Dictionary<string, string?> env = BaseEnv(s);
        List<string> a = ["-p"];
        if (s.Mode is "bare")
        {
            a.Add("--bare");
        }

        a.AddRange(["--model", s.Model, "--effort", s.Effort]);
        if (s.SystemFile is not null)
        {
            a.AddRange([s.ReplaceSystemPrompt ? "--system-prompt-file" : "--append-system-prompt-file", s.SystemFile]);
        }

        a.AddRange(["--tools", string.Join(',', s.Tools)]);
        if (s.Allowed.Count > 0)
        {
            a.Add("--allowedTools");
            a.AddRange(s.Allowed);
        }
        if (s.Denied.Count > 0)
        {
            a.Add("--disallowedTools");
            a.AddRange(s.Denied);
        }
        if (s.Mode is "lean")
        {
            AddLeanSettings(s, env, a);
        }
        else if (s.ClaudeSettings is not null)
        {
            a.AddRange(["--settings", Path.GetFullPath(s.ClaudeSettings)]);
        }
        a.Add("--strict-mcp-config");
        if (s.McpConfig is not null)
        {
            a.AddRange(["--mcp-config", Path.GetFullPath(s.McpConfig)]);
        }

        a.AddRange(["--permission-mode", s.PermissionMode]);
        // Claude Code prices only Claude models correctly, so its own cap is a second net for them only;
        // the launcher's meter enforces the budget for every model.
        if (s.Provider is "anthropic")
        {
            a.AddRange(["--max-budget-usd", s.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        // Partial messages carry each API call's final output tokens (message_delta); the assistant events only
        // repeat the count from the start of the message, which undercounts output.
        a.AddRange(["--no-session-persistence", "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
        return new Prepared(claude, a, env, "claude " + string.Join(' ', a.Select(arg => Runtimes.Quote(arg))), ScratchDirectory: null);
    }

    private static Dictionary<string, string?> BaseEnv(RunSpec s)
    {
        Dictionary<string, string?> env = new(StringComparer.Ordinal);
        if (s.CacheTtl is not "default")
        {
            env["CLAUDE_CODE_PROMPT_CACHE_TTL"] = s.CacheTtl;
        }

        if (s.Provider is not "anthropic")
        {
            // Another provider's Anthropic-compatible endpoint. The key goes through the environment, never to disk.
            string url = s.ProviderInfo.AnthropicBaseUrl
                      ?? throw new LaunchException($"provider '{s.Provider}' has no anthropicBaseUrl in prices.json; use --runtime opencode");
            env["ANTHROPIC_BASE_URL"] = url;
            env["ANTHROPIC_AUTH_TOKEN"] = Runtimes.ProviderKey(s.ProviderInfo);
            env["ANTHROPIC_API_KEY"] = null;
            foreach (string? tier in new[] { "HAIKU", "SONNET", "OPUS" })
            {
                env[$"ANTHROPIC_DEFAULT_{tier}_MODEL"] = s.Model;
            }
        }

        return env;
    }

    private static void AddLeanSettings(RunSpec s, Dictionary<string, string?> env, List<string> a)
    {
        // Without --bare Claude Code would load CLAUDE.md / AGENTS.md / rules, the user's settings, hooks and
        // plugins. --setting-sources "" loads no user/project/local settings at all (managed settings and
        // --settings still apply), so personal hooks and plugins stay out while the injected hook runs.
        JsonObject settings = s.ClaudeSettings is not null
            ? Json.ParseLenient(File.ReadAllText(s.ClaudeSettings)).AsObject()
            : [];
        if (!s.KeepClaudeMd)
        {
            settings["claudeMdExcludes"] = new JsonArray("**/CLAUDE.md", "**/CLAUDE.local.md", "**/AGENTS.md", "**/.claude/rules/**");
        }

        if (!s.KeepMemory)
        {
            settings["autoMemoryEnabled"] = false;
        }

        settings["outputStyle"] = "default"; // a personal output style would otherwise shape the worker's prompt
        if (!s.KeepHooks && s.KeepUserEnv && UserSettingsEnv() is { } userEnv)
        {
            // Settings sources are off, so carry the user's env block (proxies, for example) over explicitly,
            // through the process environment: it may hold tokens, which must not land in the run dir.
            // What the launcher sets itself (another provider's endpoint and key) wins.
            CopyUserEnv(userEnv, env);
        }
        if (s.WrapUp)
        {
            AddWrapUpHook(settings, s.RunDir);
        }
        string settingsPath = Path.GetFullPath(Path.Combine(s.RunDir, "settings.json"));
        File.WriteAllText(settingsPath, settings.ToJsonString(Json.Indented), Json.Utf8);
        a.AddRange(["--settings", settingsPath, "--disable-slash-commands"]);
        if (s.KeepHooks)
        {
            return;
        }

        a.AddRange(["--setting-sources", string.Empty]);
    }

    private static void CopyUserEnv(JsonObject userEnv, Dictionary<string, string?> env)
    {
        foreach ((string? k, JsonNode? v) in userEnv)
        {
            if (!env.ContainsKey(k) && v is JsonValue jv && jv.TryGetValue(out string? value))
            {
                env[k] = value;
            }
        }
    }

    private static void AddWrapUpHook(JsonObject settings, string runDir)
    {
        if (settings["hooks"] is not JsonObject hooks)
        {
            JsonObject n = [];
            settings["hooks"] = n;
            hooks = n;
        }

        if (hooks["PreToolUse"] is not JsonArray pre)
        {
            JsonArray m = [];
            hooks["PreToolUse"] = m;
            pre = m;
        }

        pre.Add(new JsonObject
        {
            ["matcher"] = ".*",
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Runtimes.HookCommand(runDir) }),
        });
    }

    private static JsonObject? UserSettingsEnv()
    {
        string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string path = Path.Combine(dir, "settings.json");
        try { return File.Exists(path) ? Json.ParseLenient(File.ReadAllText(path))["env"] as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private readonly Dictionary<string, Usage> _calls = [];
    private string? _streaming;

    public bool Record(string line)
    {
        if (!line.StartsWith("{\"type\":\"stream_event\"", StringComparison.Ordinal))
        {
            return true;
        }

        return line.Contains("\"message_start\"", StringComparison.Ordinal)
            || line.Contains("\"message_delta\"", StringComparison.Ordinal);
    }

    public static Usage FromApiUsage(string id, string model, JsonObject u)
    {
        JsonObject? cc = u["cache_creation"] as JsonObject;
        long w1h = Json.Num(cc?["ephemeral_1h_input_tokens"]);
        long w5m = cc?["ephemeral_5m_input_tokens"] is not null
            ? Json.Num(cc["ephemeral_5m_input_tokens"])
            : Json.Num(u["cache_creation_input_tokens"]) - w1h;
        return new Usage(id, model, Json.Num(u["input_tokens"]), Json.Num(u["output_tokens"]), 0,
            Json.Num(u["cache_read_input_tokens"]), Math.Max(0, w5m), w1h);
    }

    // One API call is reported several times (message_start, one assistant event per content block, message_delta).
    // Anthropic has final input and cache counts at the start and the final output count only in message_delta;
    // other providers' Anthropic-compatible endpoints (z.ai) send zeros at the start and everything in the delta.
    // Every count only grows, so the largest value of each field is the final one.
    private Usage Merge(Usage u)
    {
        if (_calls.TryGetValue(u.Id, out Usage? p))
        {
            string model = u.Model.Length > 0 ? u.Model : p.Model;
            long input = Math.Max(u.Input, p.Input);
            long output = Math.Max(u.Output, p.Output);
            long cacheRead = Math.Max(u.CacheRead, p.CacheRead);
            long cacheWrite5m = Math.Max(u.CacheWrite5m, p.CacheWrite5m);
            long cacheWrite1h = Math.Max(u.CacheWrite1h, p.CacheWrite1h);
            u = new Usage(u.Id, model, input, output, 0, cacheRead, cacheWrite5m, cacheWrite1h);
        }

        return _calls[u.Id] = u;
    }

    public Usage? Parse(JsonObject line, Outcome o)
    {
        string? type = Json.Str(line, "type");
        if (type is "stream_event" && line["event"] is JsonObject ev)
        {
            string? evType = Json.Str(ev, "type");
            if (evType is "message_start" && ev["message"] is JsonObject start && start["usage"] is JsonObject su)
            {
                _streaming = Json.Str(start, "id") ?? Guid.NewGuid().ToString();
                return Merge(FromApiUsage(_streaming, Json.Str(start, "model") ?? string.Empty, su));
            }
            if (evType is "message_delta" && _streaming is not null && ev["usage"] is JsonObject du)
            {
                return Merge(FromApiUsage(_streaming, string.Empty, du));
            }

            return null;
        }
        if (type is "assistant" && line["message"] is JsonObject msg && msg["usage"] is JsonObject u)
        {
            return Merge(FromApiUsage(Json.Str(msg, "id") ?? Guid.NewGuid().ToString(), Json.Str(msg, "model") ?? string.Empty, u));
        }

        if (type is not "result")
        {
            return null;
        }
        o.HasResult = true;
        o.IsError = Json.Bool(line, "is_error");
        o.Report = Json.Str(line, "result") ?? string.Empty;
        o.SessionId = Json.Str(line, "session_id");
        o.Subtype = Json.Str(line, "subtype");
        o.TerminalReason = Json.Str(line, "terminal_reason");
        o.ReportedCost = Json.Dec(line, "total_cost_usd");
        o.Turns = Json.Num(line["num_turns"]);
        o.Denials = line["permission_denials"] is JsonArray d ? d.Count : 0;
        o.Thinking = Json.Num(line["usage"]?["output_tokens_details"]?["thinking_tokens"]);
        return null;
    }

    public void Finish(Outcome o, int exitCode)
    {
    }
}