// Owner keys (docs/research/live-status/README.md, section 4.4): typed strings that tie a run to the
// session that launched it, resolved the same way at launch (RunOwner, recorded in status.json and
// the pointer) and at read time (the caller of `status`). Every key passes one boundary check: at
// most MaxLength characters of [A-Za-z0-9_.:@/-]. A --owner value that fails it is a usage error;
// an environment value that fails it is dropped without a word.
//
// The environment and /proc are parameters (a lookup function and a ProcFs), so a test drives the
// whole resolution without touching the process's own environment.

using System.Globalization;

namespace LeanWorker;

internal static class OwnerKeys
{
    public const int MaxLength = 200;

    public const string OwnerVariable = "LEAN_WORKER_OWNER";
    public const string RunIdVariable = "LEAN_WORKER_RUN_ID";
    public const string ClaudeSessionVariable = "CLAUDE_CODE_SESSION_ID";
    public const string ClaudePidVariable = "CLAUDE_PID";
    public const string OpencodeSessionVariable = "OPENCODE_SESSION_ID";
    public const string OpencodePidVariable = "OPENCODE_PID";
    public const string RelayNameVariable = "MSG_RELAY_NAME";

    public const string ClaudeSessionPrefix = "claude-session:";
    public const string OpencodeSessionPrefix = "opencode-session:";
    public const string PidPrefix = "pid:";

    /// <summary>
    /// The key of a lean-worker run (its run id): the owner of the runs launched inside its worker.
    /// </summary>
    public const string RunPrefix = "run:";

    public const string SourceOption = "option";
    public const string SourceOwnerEnv = "owner-env";
    public const string SourceParentRun = "parent-run";
    public const string SourceEnv = "env";
    public const string SourceNone = "none";

    /// <summary>
    /// The caller of <c>status</c>: the keys its runs are matched by, its relay name (display only)
    /// and the one key to pass to a launch's --owner (null when there is none).
    /// </summary>
    internal sealed record Caller(IReadOnlyList<string> Keys, string? RelayName, string? Key);

    /// <summary>
    /// The session keys found in an environment and the runtime they belong to.
    /// </summary>
    private sealed record Session(List<string> Keys, string? Runtime);

    /// <summary>
    /// Whether <paramref name="key"/> passes the boundary check: 1 to <see cref="MaxLength"/>
    /// characters, each one of <c>[A-Za-z0-9_.:@/-]</c>.
    /// </summary>
    public static bool Valid(string? key) =>
        key is { Length: > 0 and <= MaxLength } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '@' or '/' or '-');

    /// <summary>
    /// A run id (a run dir's name) as a key may hold it: letters, digits, dots, underscores and
    /// dashes, not "." or ".."; null for anything else.
    /// </summary>
    public static string? RunId(string? id)
    {
        if (id is null or "." or ".." || id.Length > MaxLength - RunPrefix.Length)
        {
            return null;
        }

        return id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? id : null;
    }

    /// <summary>
    /// The owner of a launch, in the order of section 4.4: the --owner key, LEAN_WORKER_OWNER, then
    /// LEAN_WORKER_RUN_ID (the owner is that run; the session keys found are inherited, recorded
    /// but not matched), else the session keys of Claude Code and opencode. The first rule that
    /// gives a key sets the source; every key found is recorded. <paramref name="option"/> was
    /// checked when the options were read.
    /// </summary>
    public static RunOwner AtLaunch(string? option, Func<string, string?> env, ProcFs proc)
    {
        List<string> keys = [];
        string? source = null;
        if (Valid(option))
        {
            keys.Add(option!);
            source = SourceOption;
        }

        if (Value(env, OwnerVariable) is { } fromEnv && Valid(fromEnv))
        {
            keys.Add(fromEnv);
            source ??= SourceOwnerEnv;
        }

        string? parent = RunId(Value(env, RunIdVariable));
        Session session = SessionKeys(env, proc);
        List<string> inherited = [];
        if (parent is not null)
        {
            keys.Add(RunPrefix + parent);
            inherited.AddRange(session.Keys);
            source ??= SourceParentRun;
        }
        else
        {
            keys.AddRange(session.Keys);
            source ??= session.Keys.Count > 0 ? SourceEnv : null;
        }

        return new RunOwner([.. keys.Distinct(StringComparer.Ordinal)], [.. inherited.Distinct(StringComparer.Ordinal)],
            source ?? SourceNone, session.Runtime, RelayName(env), parent);
    }

    /// <summary>
    /// The caller of <c>status</c>, resolved as at launch without an --owner: inside a lean-worker
    /// run (LEAN_WORKER_RUN_ID) the caller is that run; else its session keys; LEAN_WORKER_OWNER,
    /// when it passes the key check, is one more key in either case. The key for --owner is the run
    /// key, else the session key, else the pid key, else the LEAN_WORKER_OWNER key.
    /// </summary>
    public static Caller CallerOf(Func<string, string?> env, ProcFs proc)
    {
        string? relay = RelayName(env);
        string? owner = Value(env, OwnerVariable) is { } fromEnv && Valid(fromEnv) ? fromEnv : null;
        List<string> keys;
        string? key;
        if (RunId(Value(env, RunIdVariable)) is { } run)
        {
            keys = [RunPrefix + run];
            key = RunPrefix + run;
        }
        else
        {
            Session session = SessionKeys(env, proc);
            keys = session.Keys;
            key = session.Keys.Find(k => !k.StartsWith(PidPrefix, StringComparison.Ordinal)) ?? session.Keys.FirstOrDefault();
        }

        if (owner is not null)
        {
            keys.Add(owner);
        }

        return new Caller([.. keys.Distinct(StringComparer.Ordinal)], relay, key ?? owner);
    }

    /// <summary>
    /// A key as a header or an OWNER cell shows it: <c>cc:</c> or <c>oc:</c> and the first eight
    /// characters of a session id, <c>pid:&lt;pid&gt;</c> without the start ticks, anything else as it is.
    /// </summary>
    public static string Short(string key)
    {
        if (key.StartsWith(ClaudeSessionPrefix, StringComparison.Ordinal))
        {
            return "cc:" + Head(key[ClaudeSessionPrefix.Length..]);
        }

        if (key.StartsWith(OpencodeSessionPrefix, StringComparison.Ordinal))
        {
            return "oc:" + Head(key[OpencodeSessionPrefix.Length..]);
        }

        int at = key.IndexOf('@', StringComparison.Ordinal);
        return key.StartsWith(PidPrefix, StringComparison.Ordinal) && at > 0 ? key[..at] : key;
    }

    private static string Head(string id) => id.Length > 8 ? id[..8] : id;

    private static Session SessionKeys(Func<string, string?> env, ProcFs proc)
    {
        List<string> keys = [];
        string? runtime = null;
        if (Typed(ClaudeSessionPrefix, Value(env, ClaudeSessionVariable)) is { } claudeSession)
        {
            keys.Add(claudeSession);
            runtime = "claude";
        }

        if (PidKey(Value(env, ClaudePidVariable), proc) is { } claudePid)
        {
            keys.Add(claudePid);
            runtime ??= "claude";
        }

        if (Typed(OpencodeSessionPrefix, Value(env, OpencodeSessionVariable)) is { } opencodeSession)
        {
            keys.Add(opencodeSession);
            runtime ??= "opencode";
        }

        if (PidKey(Value(env, OpencodePidVariable), proc) is { } opencodePid)
        {
            keys.Add(opencodePid);
            runtime ??= "opencode";
        }

        return new Session(keys, runtime);
    }

    private static string? Typed(string prefix, string? id) => id is { Length: > 0 } && Valid(prefix + id) ? prefix + id : null;

    /// <summary>
    /// <c>pid:&lt;pid&gt;@&lt;start ticks&gt;</c> for a pid whose stat line is there and not a
    /// zombie's; null otherwise (the ticks keep a recycled pid from matching).
    /// </summary>
    internal static string? PidKey(string? text, ProcFs proc)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            return null;
        }

        ProcProbe probe = proc.Stat(pid);
        if (probe.Kind is not ProbeKind.Present || probe.Stat.Exited)
        {
            return null;
        }

        string key = string.Create(CultureInfo.InvariantCulture, $"{PidPrefix}{pid}@{probe.Stat.StartTicks}");
        return Valid(key) ? key : null;
    }

    /// <summary>
    /// MSG_RELAY_NAME, display only: trimmed, at most <see cref="MaxLength"/> characters and no
    /// control or format character; null otherwise.
    /// </summary>
    private static string? RelayName(Func<string, string?> env)
    {
        string? name = Value(env, RelayNameVariable)?.Trim();
        return name is { Length: > 0 and <= MaxLength } && !name.Any(c => TerminalText.Unsafe(c)) ? name : null;
    }

    private static string? Value(Func<string, string?> env, string name) => env(name) is { Length: > 0 } v ? v : null;
}
