// Runtimes: how a worker is started and how its output stream is read. Claude Code (`claude -p`) and
// opencode (`opencode run`) are supported; both stream JSON lines the launcher meters live.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class OpencodeRuntime : IRuntime
{
    public string Name => "opencode";

    // Claude Code tool names (the profile vocabulary) -> opencode permission keys.
    private static readonly Dictionary<string, string[]> _toolKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"] = ["read", "list"],
        ["Edit"] = ["edit"],
        ["Write"] = ["edit"],
        ["Glob"] = ["glob"],
        ["Grep"] = ["grep"],
        ["Bash"] = ["bash"],
        ["WebFetch"] = ["webfetch"],
        ["WebSearch"] = ["websearch"],
    };

    public Prepared Prepare(RunSpec s)
    {
        string opencode = Launcher.FindOnPath("opencode") ?? throw new LaunchException("'opencode' is not on PATH.");
        if (s.McpConfig is not null)
        {
            throw new LaunchException("--mcp-config is supported by the claude runtime only");
        }

        JsonObject permission = Permissions(s);

        JsonObject config = new()
        {
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["permission"] = permission,
            ["agent"] = new JsonObject
            {
                ["lean-worker"] = new JsonObject { ["mode"] = "primary", ["model"] = $"{s.Provider}/{s.Model}", ["permission"] = permission.DeepClone() },
            },
        };
        if (s.SystemFile is not null)
        {
            config["instructions"] = new JsonArray(s.SystemFile);
        }

        (bool providerFromUser, JsonObject? providerBlock) = AddProviderConfig(s, config);
        string xdg = InstallPlugin(s.RunDir);
        Dictionary<string, string?> env = WorkerEnv(s, xdg, config);
        WriteRecordedConfig(s, config, providerFromUser, providerBlock);

        List<string> a = ["run", "--format", "json", "--agent", "lean-worker", "-m", $"{s.Provider}/{s.Model}"];
        if (s.Variant is not null)
        {
            a.AddRange(["--variant", s.Variant]);
        }

        return new Prepared(opencode, a, env, "opencode " + string.Join(' ', a.Select(x => Runtimes.Quote(x))), ScratchDirectory: xdg);
    }

    // A clean config home: a unique temporary directory under the system temp path, outside the repository,
    // removed after the run. No global instructions, plugins, skills or MCP servers reach the worker.
    // Logins stay in the data directory, which is not moved.
    private static string InstallPlugin(string runDir)
    {
        string xdg = Directory.CreateTempSubdirectory($"lean-worker-{Path.GetFileName(Path.GetFullPath(runDir))}-").FullName;
        string pluginDir = Path.Combine(xdg, "opencode", "plugins");
        _ = Directory.CreateDirectory(pluginDir);
        string plugin = Path.Combine(AppContext.BaseDirectory, "opencode-plugin.ts");
        if (!File.Exists(plugin))
        {
            throw new LaunchException($"opencode plugin not found next to the launcher: {plugin}");
        }

        File.Copy(plugin, Path.Combine(pluginDir, "lean-worker.ts"), overwrite: true);
        return xdg;
    }

    private static JsonObject Permissions(RunSpec s)
    {
        JsonObject permission = new() { ["*"] = "deny" };
        foreach (string tool in s.Tools)
        {
            if (!_toolKeys.TryGetValue(tool, out string[]? keys))
            {
                throw new LaunchException($"tool '{tool}' has no opencode equivalent");
            }

            foreach (string k in keys)
            {
                permission[k] = "allow";
            }
        }
        // Edit/Write both map to opencode's `edit`. Deny `.git/**`, `**/.git/**`, `.git` and `**/.git` so a worker
        // cannot replace `.git/config` and turn a later `git diff` (diff.external) or `git status` (core.fsmonitor)
        // into a command runner; the plain `.git` / `**/.git` entries cover a linked worktree, where `.git` is a
        // file whose contents `gitdir: <dir the worker wrote>` make git read that dir's config. opencode's last
        // matching rule wins, so the deny entries come after `*`.
        if (s.Tools.Contains("Edit", StringComparer.OrdinalIgnoreCase) || s.Tools.Contains("Write", StringComparer.OrdinalIgnoreCase))
        {
            JsonObject edit = new() { ["*"] = "allow" };
            edit[".git/**"] = "deny";
            edit["**/.git/**"] = "deny";
            edit[".git"] = "deny";
            edit["**/.git"] = "deny";
            permission["edit"] = edit;
        }
        if (s.Tools.Contains("Bash", StringComparer.OrdinalIgnoreCase))
        {
            // The bash object is emitted in every permission mode so the deny floor holds everywhere.
            // opencode uses last matching rule wins; the per-pattern allow entries come first, then the per-pattern
            // deny entries so they win. An allow entry whose inner pattern ends in `:*` (Claude Code's "or with any
            // args") is split into two keys, the bare command and "<command> *": opencode's `*` has no word boundary,
            // so the old `od*` would also match `odX`. Any other allow entry is emitted with `:*` replaced by `*` as
            // before. Deny entries stay as-is (`rm*`): narrowing them to `rm` / `rm *` would let `rmdir` slip through
            // in bypass mode. In a non-bypass mode a leading `"*": "deny"` makes every unlisted command denied (an
            // "ask" is impossible: `opencode run` auto-rejects a prompt and ends the session); in bypass mode the
            // leading `"*": "allow"` lets the worker run anything except the deny entries.
            JsonObject bash = new() { ["*"] = s.PermissionMode is "bypassPermissions" ? "allow" : "deny" };
            if (s.PermissionMode is not "bypassPermissions")
            {
                foreach (string pattern in s.Allowed)
                {
                    if (pattern.StartsWith("Bash(", StringComparison.Ordinal) && pattern.EndsWith(')'))
                    {
                        string inner = pattern[5..^1];
                        if (inner.EndsWith(":*", StringComparison.Ordinal))
                        {
                            string bare = inner[..^2];
                            bash[bare] = "allow";
                            bash[bare + " *"] = "allow";
                        }
                        else
                        {
                            bash[inner.Replace(":*", "*", StringComparison.Ordinal)] = "allow";
                        }
                    }
                }
            }

            foreach (string pattern in s.Denied)
            {
                if (pattern.StartsWith("Bash(", StringComparison.Ordinal) && pattern.EndsWith(')'))
                {
                    // Remove-then-add so the deny moves after every allow: JsonObject keeps a key's original position on reassignment, so without this an earlier allow of the same glob would win.
                    string key = pattern[5..^1].Replace(":*", "*", StringComparison.Ordinal);
                    bash.Remove(key);
                    bash[key] = "deny";
                }
            }

            permission["bash"] = bash;
        }
        return permission;
    }

    // opencode sends x-session-affinity / X-Session-Id on every request, which z.ai routes by: a fresh worker
    // lands on a node without its prefix cached (first call 42-51% read). Force both to a fixed value so
    // every worker reuses the cached one (99% read). Set on provider.models.<model>.headers, the only level
    // opencode merges after its own (provider options.headers are overridden and do not work). Only zai
    // providers are touched; non-zai entries pass through as-is, and a zai entry that isn't an object
    // (a string, array, ...) falls through to the minimal block below.
    private static (bool FromUser, JsonObject? Block) AddProviderConfig(RunSpec s, JsonObject config)
    {
        bool providerFromUser = false;
        JsonObject? providerBlock = null;
        JsonNode? passthroughNode = null;
        if (UserProviderBlock(s.Provider) is { } provider)
        {
            if (s.Provider.StartsWith("zai", StringComparison.OrdinalIgnoreCase))
            {
                if (provider is JsonObject obj)
                {
                    providerFromUser = true;
                    providerBlock = obj;
                }
            }
            else
            {
                providerFromUser = true;
                passthroughNode = provider;
            }
        }
        if (s.Provider.StartsWith("zai", StringComparison.OrdinalIgnoreCase))
        {
            providerBlock ??= [];
            ApplyZaiSessionAffinityHeaders(providerBlock, s.Model);
            config["provider"] = new JsonObject { [s.Provider] = providerBlock };
        }
        else if (passthroughNode is not null)
        {
            config["provider"] = new JsonObject { [s.Provider] = passthroughNode };
        }
        return (providerFromUser, providerBlock);
    }

    private static Dictionary<string, string?> WorkerEnv(RunSpec s, string xdg, JsonObject config)
    {
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["XDG_CONFIG_HOME"] = xdg,
            // The plugin puts the user's own value back for the worker's shell commands (git, gh and others).
            ["LEAN_WORKER_XDG_CONFIG_HOME"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty,
            ["LEAN_WORKER_RUN_DIR"] = Path.GetFullPath(s.RunDir),
            ["LEAN_WORKER_WRAPUP"] = s.WrapUp ? "1" : "0",
            ["OPENCODE_CONFIG_CONTENT"] = config.ToJsonString(),
            ["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1",
            ["OPENCODE_DISABLE_CLAUDE_CODE_SKILLS"] = "1",
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "1",
        };
        if (!s.KeepClaudeMd)
        {
            env["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1";
            env["OPENCODE_DISABLE_CLAUDE_CODE"] = "1";
        }
        return env;
    }

    // The config may carry a provider's key, so the record keeps it without the provider block. A block the
    // launcher built itself contains only the affinity headers, no key, so it is safe to keep verbatim.
    private static void WriteRecordedConfig(RunSpec s, JsonObject config, bool providerFromUser, JsonObject? providerBlock)
    {
        JsonObject recorded = (JsonObject)config.DeepClone();
        if (recorded.ContainsKey("provider"))
        {
            if (providerFromUser)
            {
                recorded["provider"] = "(copied from the user's opencode config; not recorded)";
            }
            else
            {
                JsonObject b = new()
                {
                    [s.Provider] = providerBlock!.DeepClone(),
                };
                recorded["provider"] = b;
            }
        }

        File.WriteAllText(Path.Combine(s.RunDir, "opencode-config.json"), recorded.ToJsonString(Json.Indented), Json.Utf8);
    }

    /// <summary>
    /// A custom provider defined in the user's opencode config, carried into the clean config home.
    /// </summary>
    private static JsonNode? UserProviderBlock(string provider)
    {
        string dir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        foreach (string? name in new[] { "opencode.jsonc", "opencode.json", "config.json" })
        {
            string path = Path.Combine(dir, "opencode", name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (Json.ParseLenient(File.ReadAllText(path))["provider"]?[provider] is JsonNode n)
                {
                    return n.DeepClone();
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return null;
    }

    /// <summary>
    /// Adds the two headers that pin every worker to the same z.ai node, keeping any the user set.
    /// </summary>
    private static void ApplyZaiSessionAffinityHeaders(JsonObject provider, string model)
    {
        if (provider["models"] is not JsonObject models)
        {
            models = [];
            provider["models"] = models;
        }

        if (models[model] is not JsonObject m)
        {
            m = [];
            models[model] = m;
        }

        if (m["headers"] is not JsonObject headers)
        {
            headers = [];
            m["headers"] = headers;
        }

        bool hasAffinity = false;
        bool hasSessionId = false;
        foreach (string? k in headers.Select(h => h.Key))
        {
            if (string.Equals(k, "x-session-affinity", StringComparison.OrdinalIgnoreCase))
            {
                hasAffinity = true;
            }

            if (string.Equals(k, "X-Session-Id", StringComparison.OrdinalIgnoreCase))
            {
                hasSessionId = true;
            }
        }
        if (!hasAffinity)
        {
            headers["x-session-affinity"] = "lean-worker";
        }

        if (hasSessionId)
        {
            return;
        }

        headers["X-Session-Id"] = "lean-worker";
    }

    public Usage? Parse(JsonObject line, Outcome o)
    {
        o.SessionId ??= Json.Str(line, "sessionID");
        JsonObject? part = line["part"] as JsonObject;
        switch (Json.Str(line, "type"))
        {
            case "text":
                {
                    if (part is not null)
                    {
                        string? messageId = Json.Str(part, "messageID");
                        if (messageId != o.LastMessageId) { o.Texts.Clear(); o.LastMessageId = messageId; }
                        if (Json.Str(part, "text") is { Length: > 0 } t)
                        {
                            o.Texts.Add(t);
                        }
                    }

                    break;
                }
            case "error":
                {
                    o.IsError = true;
                    o.Texts.Add(line["error"]?.ToJsonString() ?? "error");
                    break;
                }
            case "tool_use" when (part?["state"]) is JsonObject state && Json.Str(state, "status") is "error"
                                 && Json.Str(state, "error") is { } err && IsPermissionError(err):
                {
                    o.Denials++;
                    break;
                }
            case "step_finish" when (part?["tokens"] is JsonObject tok):
                {
                    o.Turns++;
                    o.LastStepReason = Json.Str(part, "reason");
                    o.Thinking += Json.Num(tok["reasoning"]);
                    JsonObject? cache = tok["cache"] as JsonObject;
                    return new Usage(Json.Str(part, "id") ?? Guid.NewGuid().ToString(), Json.Str(part, "modelID") ?? string.Empty,
                        Json.Num(tok["input"]), Json.Num(tok["output"]), Json.Num(tok["reasoning"]),
                        Json.Num(cache?["read"]), Json.Num(cache?["write"]), 0);
                }
        }
        return null;
    }

    // opencode's messages for a rule that denies the call and for a rejected permission prompt.
    private static bool IsPermissionError(string error)
    {
        return error.Contains("specified a rule which prevents", StringComparison.Ordinal)
        || error.Contains("rejected permission", StringComparison.Ordinal);
    }

    public void Finish(Outcome o, int exitCode)
    {
        // opencode has no final result event: the run ends when the session goes idle. A last step that asked for
        // tools means the session was cut off before the model could answer (an auto-rejected permission prompt).
        o.HasResult = o.Texts.Count > 0 && o.LastStepReason is not "tool-calls";
        if (exitCode is not 0)
        {
            o.IsError = true;
        }

        o.Report = string.Join('\n', o.Texts).Trim();
        o.Subtype = o.IsError ? "error" : "success";
    }
}
