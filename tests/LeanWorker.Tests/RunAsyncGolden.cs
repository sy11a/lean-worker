using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Golden tests of Launcher.RunAsync against a stub "claude" executable (mode bare, Anthropic model, dummy key).
/// /// They pin the printed result block, summary.json, the runs.jsonl row and the run dir's files, so a refactor
/// of /// RunAsync cannot change behaviour unnoticed. Only volatile parts (paths, durations, timestamps) are
/// normalized.
/// </summary>
internal static partial class RunAsyncGolden
{
    public const string Notes = "golden notes";

    public const string SuccessStream = """
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
        """;

    public const string ErrorStream = """
        printf '%s\n' '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"it broke","session_id":"s2","total_cost_usd":0.0002,"num_turns":1,"permission_denials":[]}'
        """;

    /// <summary>
    /// Writes &lt;root&gt;/profiles.json with one profile, so a LaunchRun under test resolves a profile (and its
    /// "gate" key) without going through --profile defaults.
    /// </summary>
    public static void WriteProfile(string root, string name, JsonObject profile)
    {
        JsonObject doc = new() { ["defaultProfile"] = name, ["profiles"] = new JsonObject { [name] = profile } };
        File.WriteAllText(Path.Combine(root, "profiles.json"), doc.ToJsonString());
    }

    public static readonly string[] SummaryKeys =
    [
        "schema_version", "timestamp", "name", "run_dir", "profile", "runtime", "provider", "model", "model_reason", "effort",
        "mode", "billing", "cache_ttl", "hooks", "status", "subtype", "terminal_reason", "exit_code", "num_turns", "api_calls",
        "duration_ms", "total_cost_usd", "reported_cost_usd", "budget_usd", "wrap_up_usd", "wrapped_up", "hook_checks",
        "continued_from", "escalate_to", "gate", "model_traits", "tokens", "context_first_call", "first_call_cache_read",
        "first_call_cache_read_share", "context_peak", "permission_denials", "write_scope", "changed_files", "out_of_scope",
        "session_id", "quota_before", "quota_after", "quota_used_pct", "notes",
    ];

    public const string BareWrapUpNote = "bare mode skips hooks, so wrap-up is off; the budget is still enforced";

    private static string Sha12(string content) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content)), 0, 6).ToLowerInvariant();

    public static string SystemPath(string root) => Path.Combine(root, "system", $"{Sha12(Notes)}.md");

    private static string Stub(string script)
    {
        string dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        string path = Path.Combine(dir, "claude");
        File.WriteAllText(path, "#!/bin/sh\ncat >/dev/null\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    public static string NewRoot()
    {
        string root = Directory.CreateTempSubdirectory("lw-runs").FullName;
        File.WriteAllText(Path.Combine(root, "project.md"), Notes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(root, "task.md"), "do nothing");
        return root;
    }

    /// <summary>
    /// Runs RunAsync with the stub on PATH; returns the exit code and stdout, or rethrows what RunAsync throws.
    /// </summary>
    public static async Task<(int Code, string Stdout)> RunAsync(string root, string script, Action<Options>? configure = null, string? apiKey = "dummy-test-key")
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string oldCwd = Directory.GetCurrentDirectory();
        string cleanCwd = Directory.CreateTempSubdirectory("lw-cwd").FullName;
        await using StringWriter outWriter = new();
        TextWriter oldOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("PATH", Stub(script) + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
            Directory.SetCurrentDirectory(cleanCwd);
            Console.SetOut(outWriter);
            Options o = new() { TaskFile = Path.Combine(root, "task.md"), RunsRoot = root, Model = "anthropic/claude-haiku-4-5", Mode = "bare", Name = "golden" };
            configure?.Invoke(o);
            int code = await Launcher.RunAsync(o);
            return (code, outWriter.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    public static string RunDirFrom(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("run:", StringComparison.Ordinal)).Split("run:", 2)[1].Trim();

    /// <summary>
    /// Paths become &lt;ROOT&gt; / &lt;RUN&gt;, the "0m00s" duration &lt;DUR&gt;, a run dir's timestamp &lt;TS&gt;.
    /// </summary>
    public static string Normalize(string text, string root)
    {
        string t = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        t = Regex.Replace(t, Regex.Escape(Path.Combine(root, "runs")) + @"/\d{8}-\d{6}-([A-Za-z0-9._-]+)", "<RUN:$1>", RegexOptions.None, TimeSpan.FromSeconds(5));
        t = t.Replace(root, "<ROOT>", StringComparison.Ordinal);
        return DurationPattern().Replace(t, "<DUR>");
    }

    [GeneratedRegex(@"\b\d+m\d{2}s\b", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex DurationPattern();

    public static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    public static JsonObject Summary(string runDir) => JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json")))!.AsObject();

    public static void AssertSummaryKeys(JsonObject summary) =>
        Assert.Equal(SummaryKeys, summary.Select(kv => kv.Key), StringComparer.Ordinal);

    public static void AssertFiles(string runDir, params string[] expected) =>
        Assert.Equal(expected, Directory.GetFiles(runDir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    public static void AssertLedgerHasOneRowEqualTo(string root, JsonObject summary)
    {
        string[] rows = File.ReadAllLines(Path.Combine(root, "runs.jsonl"));
        string row = Assert.Single(rows);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(row), summary), row);
    }

    public static string[] Notes_(JsonObject s) => [.. s["notes"]!.AsArray().Select(n => n!.GetValue<string>())];

    public static string BareCommand(string root, string maxBudget = "2")
    {
        return
            "claude -p --bare --model claude-haiku-4-5 --effort medium --append-system-prompt-file " + SystemPath(root) +
            " --tools Read,Edit,Write,Glob,Grep,Bash --disallowedTools " + string.Join(' ', Launcher.DeniedFloor.Select(e => Runtimes.Quote(e))) +
            " --strict-mcp-config --permission-mode acceptEdits --max-budget-usd " + maxBudget +
            " --no-session-persistence --output-format stream-json --verbose --include-partial-messages";
    }
}
