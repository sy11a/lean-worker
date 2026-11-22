// Runtimes: how a worker is started and how its output stream is read. Claude Code (`claude -p`) and
// opencode (`opencode run`) are supported; both stream JSON lines the launcher meters live.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Runtimes
{
    /// <summary>
    /// Resolves the named runtime ("claude" or "opencode").
    /// </summary>
    public static IRuntime Get(string name)
    {
        return name switch
        {
            "claude" => new ClaudeRuntime(),
            "opencode" => new OpencodeRuntime(),
            _ => throw new LaunchException($"invalid runtime '{name}' (claude | opencode)"),
        };
    }

    /// <summary>
    /// The command that runs this launcher's `hook` subcommand, for the worker's pre-tool hook.
    /// </summary>
    public static string HookCommand(string runDir)
    {
        string self = Environment.ProcessPath ?? throw new LaunchException("cannot locate the launcher executable");
        string dll = Path.Combine(AppContext.BaseDirectory, "LeanWorker.dll");
        string exe = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"{Q(self)} {Q(dll)}"
            : Q(self);
        return $"{exe} hook --run-dir {Q(Path.GetFullPath(runDir))}";
    }

    /// <summary>
    /// The run id the worker's environment carries as LEAN_WORKER_RUN_ID: the run dir's name.
    /// </summary>
    public static string RunId(string runDir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDir)));

    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The API key for a non-Anthropic provider: its keyEnv variable, else opencode's stored login.
    /// </summary>
    public static string ProviderKey(Provider p)
    {
        if (p.KeyEnv is { } env && Environment.GetEnvironmentVariable(env) is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }

        string auth = Path.Combine(DataHome(), "opencode", "auth.json");
        if (File.Exists(auth) && Json.ParseLenient(File.ReadAllText(auth))[p.Name] is JsonObject entry
            && Json.Str(entry, "key") is { Length: > 0 } key)
        {
            return key;
        }

        throw new LaunchException($"no API key for provider '{p.Name}': set {p.KeyEnv ?? "its keyEnv variable"} or log in with `opencode auth login`");
    }

    public static string DataHome()
    {
        return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    public static string Quote(string s)
    {
        if (s.Length is 0)
        {
            return "\"\"";
        }

        if (s.Contains(' ', StringComparison.Ordinal) || s.Contains('(', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{s}\"";
        }

        return s;
    }
}