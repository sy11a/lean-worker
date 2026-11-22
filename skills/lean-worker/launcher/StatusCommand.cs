// `status`: the read-only view of runs from their status.json files (slice 1 of
// docs/research/live-status/README.md): one run (--run <dir>) or this repository's runs (--here),
// as a one-shot plain listing or --json. Exit 0 when listed (even when runs failed), 2 on a usage
// error. It never writes or deletes anything.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusCommand
{
    public const string Usage = "status --run <dir> [--json] | status --here [--runs-root <dir>] [--since <dur>] [--json]";

    /// <summary>
    /// --since for ended and legacy rows of --here when none is given.
    /// </summary>
    public static readonly TimeSpan DefaultSince = TimeSpan.FromHours(24);

    /// <summary>
    /// The longest usage-error message printed (it may echo an argument).
    /// </summary>
    private const int MessageLimit = 300;

    /// <summary>
    /// Scopes and modes of later slices: named in the usage error instead of "unknown option".
    /// </summary>
    private static readonly string[] _later = ["--owner", "--all", "--whoami", "--wait", "--watch", "--key"];

    /// <summary>
    /// The parsed arguments: the run dir (--run) or the scan (--here), its runs root and --since.
    /// </summary>
    internal sealed record StatusArgs(string? Run, bool Here, string? RunsRoot, string? Since, bool Json);

    /// <summary>
    /// The command with its inputs: the output, the reader (its /proc, clock and boot id; the
    /// machine's own is <see cref="StatusReader.Host"/>) and the directory it is called in.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, StatusReader reader, string cwd)
    {
        StatusArgs a;
        TimeSpan since;
        try
        {
            a = Parse(args);
            since = a.Since is { } s ? ParseDuration(s) : DefaultSince;
        }
        catch (LaunchException ex)
        {
            // The message may echo an argument (an unknown option, a --since value).
            await output.WriteLineAsync($"lean-worker status: {TerminalText.Clean(ex.Message, MessageLimit)}; usage: {Usage}").ConfigureAwait(false);
            return 2;
        }

        string scope;
        List<StatusRow> rows;
        int hidden = 0;
        if (a.Run is { } run)
        {
            scope = Path.GetFullPath(run, cwd);
            if (!Directory.Exists(scope))
            {
                await output.WriteLineAsync($"lean-worker status: no run dir {TerminalText.Clean(scope, StatusText.ScopeLimit)}; usage: {Usage}").ConfigureAwait(false);
                return 2;
            }

            rows = [reader.Read(scope)];
        }
        else
        {
            scope = a.RunsRoot is { } root ? Path.GetFullPath(root, cwd) : await DefaultRunsRootAsync(cwd).ConfigureAwait(false);
            List<StatusRow> all = [.. RunDirs(scope).Select(dir => reader.Read(dir))];
            DateTimeOffset cutoff = reader.Clock.GetUtcNow() - since;
            rows = [.. all.Where(r => Shown(r, cutoff))];
            hidden = all.Count - rows.Count;
        }

        await PrintAsync(output, a.Json, scope, StatusText.Order(rows), hidden, a.Since ?? "24h", reader.Clock.GetUtcNow()).ConfigureAwait(false);
        return 0;
    }

    private static async Task PrintAsync(TextWriter output, bool json, string scope, List<StatusRow> rows, int hidden, string since, DateTimeOffset now)
    {
        if (json)
        {
            JsonObject doc = StatusText.Document(rows, now);
            await output.WriteLineAsync(doc.ToJsonString(Json.Indented)).ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(StatusText.Header(scope, rows, hidden, since)).ConfigureAwait(false);
        foreach (string line in StatusText.Lines(rows, now))
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether --here lists a row: live, orphaned, unknown and unreadable rows always, whatever
    /// their age (they are what the operator has to see); ended rows with a final state (lost
    /// included) and legacy rows when they ended (or, without an end time, started) after the
    /// cutoff, or carry no time at all.
    /// </summary>
    private static bool Shown(StatusRow r, DateTimeOffset cutoff)
    {
        if (r.Live || r.State is StatusStates.Orphaned or StatusStates.Unknown or StatusStates.Unreadable)
        {
            return true;
        }

        return (r.EndedAt ?? r.StartedAt) is not { } t || t >= cutoff;
    }

    internal static StatusArgs Parse(string[] args)
    {
        string? run = null;
        string? runsRoot = null;
        string? since = null;
        bool here = false;
        bool json = false;
        ArgReader.Read(args, (arg, next) =>
        {
            if (_later.Contains(arg, StringComparer.Ordinal))
            {
                throw new LaunchException($"{arg} is not available yet; this version has --run and --here");
            }

            switch (arg)
            {
                case "--run":
                    {
                        run = next();
                        return true;
                    }
                case "--here":
                    {
                        here = true;
                        return true;
                    }
                case "--runs-root":
                    {
                        runsRoot = next();
                        return true;
                    }
                case "--since":
                    {
                        since = next();
                        return true;
                    }
                case "--json":
                    {
                        json = true;
                        return true;
                    }
                default:
                    return false;
            }
        });
        return Check(new StatusArgs(run, here, runsRoot, since, json));
    }

    private static StatusArgs Check(StatusArgs a)
    {
        if (a.Run is not null && a.Here)
        {
            throw new LaunchException("pass --run <dir> or --here, not both");
        }

        if (a.Run is null && !a.Here)
        {
            throw new LaunchException("pass a scope: --run <dir> or --here");
        }

        if (a.Run is not null && (a.RunsRoot is not null || a.Since is not null))
        {
            throw new LaunchException("--runs-root and --since go with --here");
        }

        return a;
    }

    /// <summary>
    /// A duration: a whole number and a unit, s, m, h or d (e.g. 90m, 24h, 7d).
    /// </summary>
    internal static TimeSpan ParseDuration(string text)
    {
        if (text.Length >= 2 && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n <= 100_000)
        {
            TimeSpan? span = text[^1] switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                _ => null,
            };
            if (span is { } value)
            {
                return value;
            }
        }

        throw new LaunchException($"--since needs a duration such as 90m, 24h or 7d, not '{text}'");
    }

    /// <summary>
    /// The runs root --here reads without --runs-root: the .lean-worker at the git root of the
    /// current directory when it exists (where the launcher writes), else .lean-worker beside the caller.
    /// </summary>
    private static async Task<string> DefaultRunsRootAsync(string cwd)
    {
        if (await RepoToken.RootAsync(cwd).ConfigureAwait(false) is { } gitRoot)
        {
            string candidate = Path.Combine(gitRoot, ".lean-worker");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(cwd, ".lean-worker");
    }

    /// <summary>
    /// The run dirs under a runs root: every directory in &lt;runs-root&gt;/runs; none when it does not exist.
    /// </summary>
    private static List<string> RunDirs(string runsRoot)
    {
        string runs = Path.Combine(runsRoot, "runs");
        return Directory.Exists(runs) ? [.. Directory.EnumerateDirectories(runs)] : [];
    }
}
