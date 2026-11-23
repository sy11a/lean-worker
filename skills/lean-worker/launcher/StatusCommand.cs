// `status`: the read-only view of runs from their status.json files (docs/research/live-status/
// README.md, sections 4.1 and 4.2): the caller's own runs (the default, through the per-user
// index), one owner's (--owner), every indexed run (--all), this repository's runs (--here, a scan)
// or one run (--run <dir>), as a one-shot plain listing or --json; --whoami prints the caller's
// owner keys. Exit 0 when listed (even when runs failed), 2 on a usage error or when no caller
// identity can be found. It writes nothing; a scope that reads the index deletes the pointers of
// runs that provably ended long ago (StatusIndexScan), and nothing else.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusCommand
{
    public const string Usage = "status [--owner <key> | --all | --here [--runs-root <dir>] | --run <dir>] [--since <dur>] [--json] | status --whoami [--key]";

    /// <summary>
    /// --since for ended and legacy rows of --here and --all when none is given.
    /// </summary>
    public static readonly TimeSpan DefaultSince = TimeSpan.FromHours(24);

    /// <summary>
    /// What `status` says when nothing tells who the caller is.
    /// </summary>
    internal const string NoCaller = "cannot tell which session you are; pass --owner, --here or --all";

    /// <summary>
    /// The longest usage-error message printed (it may echo an argument).
    /// </summary>
    private const int MessageLimit = 300;

    /// <summary>
    /// The parsed arguments: the run dir (--run) or the scan (--here), its runs root, --since and
    /// --json; the index scopes (--all, --owner) and the --whoami mode (with --key).
    /// </summary>
    internal sealed record StatusArgs(string? Run, bool Here, string? RunsRoot, string? Since, bool Json)
    {
        public bool All { get; init; }

        public string? Owner { get; init; }

        public bool Whoami { get; init; }

        public bool Key { get; init; }
    }

    /// <summary>
    /// What one scope gave: the header's scope text, the rows (every round of a chain), the window
    /// for ended rows (null: none), whether rows show their owner, the caller for --json, the
    /// scope's name and a note for the header.
    /// </summary>
    private sealed record Listing(string Scope, List<StatusRow> Rows, TimeSpan? Since, bool OwnerColumn, JsonObject? Caller, string Name, string? Note);

    /// <summary>
    /// The command with its inputs: the output, the reader (its /proc, clock, boot id and scan
    /// budget; the machine's own is <see cref="StatusReader.Host"/>), the directory it is called
    /// in and the environment (the caller's variables and the index folder; the process's own is
    /// <see cref="StatusEnvironment.Host"/>). Without one (<see cref="StatusEnvironment.None"/>)
    /// there is no caller identity and no index, so only --run and --here list anything.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, StatusReader reader, string cwd, StatusEnvironment? env = null)
    {
        env ??= StatusEnvironment.None;
        StatusArgs a;
        TimeSpan? since;
        try
        {
            a = Parse(args);
            since = a.Since is { } s ? ParseDuration(s) : null;
        }
        catch (LaunchException ex)
        {
            // The message may echo an argument (an unknown option, a --since value).
            await output.WriteLineAsync($"lean-worker status: {TerminalText.Clean(ex.Message, MessageLimit)}; usage: {Usage}").ConfigureAwait(false);
            return 2;
        }

        if (a.Whoami)
        {
            return await WhoamiAsync(output, a.Key, OwnerKeys.CallerOf(env.Variable, reader.Proc)).ConfigureAwait(false);
        }

        ReadBudget budget = reader.NewBudget();
        DateTimeOffset now = reader.Clock.GetUtcNow();
        Listing? listing;
        if (a.Run is { } run)
        {
            listing = RunScope(run, cwd, reader, budget, now);
        }
        else if (a.Here)
        {
            listing = await HereScopeAsync(a, cwd, reader, budget, now, since).ConfigureAwait(false);
        }
        else
        {
            listing = IndexScope(a, env, reader, budget, now, since);
        }

        if (listing is null)
        {
            string message = a.Run is not null
                ? $"no run dir {TerminalText.Clean(Path.GetFullPath(a.Run, cwd), StatusText.ScopeLimit)}; usage: {Usage}"
                : NoCaller;
            await output.WriteLineAsync("lean-worker status: " + message).ConfigureAwait(false);
            return 2;
        }

        await PrintAsync(output, a.Json, listing, reader, now, a.Since ?? "24h").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WhoamiAsync(TextWriter output, bool key, OwnerKeys.Caller caller)
    {
        if (caller.Keys.Count is 0 || caller.Key is null)
        {
            await output.WriteLineAsync("lean-worker status: " + NoCaller).ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<string> keys = key ? [caller.Key] : caller.Keys;
        foreach (string k in keys)
        {
            await output.WriteLineAsync(TerminalText.Clean(k, OwnerKeys.MaxLength)).ConfigureAwait(false);
        }

        return 0;
    }

    private static Listing? RunScope(string run, string cwd, StatusReader reader, ReadBudget budget, DateTimeOffset now)
    {
        string scope = Path.GetFullPath(run, cwd);
        // A check that does not answer in time leaves the read to say so in the row.
        if (budget.Run(() => Directory.Exists(scope), out bool exists) && !exists)
        {
            return null;
        }

        return new Listing(scope, [StatusText.Checked(reader.Read(scope, budget), now)], Since: null, OwnerColumn: false, Caller: null, "run", Note: null);
    }

    private static async Task<Listing> HereScopeAsync(StatusArgs a, string cwd, StatusReader reader, ReadBudget budget, DateTimeOffset now, TimeSpan? since)
    {
        string scope = a.RunsRoot is { } root ? Path.GetFullPath(root, cwd) : await DefaultRunsRootAsync(cwd).ConfigureAwait(false);
        (List<string> dirs, bool complete) = RunDirs(scope, budget);
        List<StatusRow> rows = [.. dirs.Select(dir => StatusText.Checked(reader.Read(dir, budget), now))];
        return new Listing(scope, rows, since ?? DefaultSince, OwnerColumn: true, Caller: null, "here", complete ? null : ListingStopped);
    }

    /// <summary>
    /// The index scopes: every pointer (--all), one owner's (--owner) or the caller's (no scope);
    /// null when there is no scope and nothing tells who the caller is.
    /// </summary>
    private static Listing? IndexScope(StatusArgs a, StatusEnvironment env, StatusReader reader, ReadBudget budget, DateTimeOffset now, TimeSpan? since)
    {
        IReadOnlyList<string>? keys;
        string scope;
        JsonObject? caller = null;
        if (a.All)
        {
            keys = null;
            scope = env.IndexFolder is { } folder ? "all runs in " + folder : "all runs (no index folder: HOME is not set)";
        }
        else if (a.Owner is { } owner)
        {
            keys = [owner];
            scope = "owner " + OwnerKeys.Short(owner);
            caller = new JsonObject { ["keys"] = RunOwner.Array(keys) };
        }
        else
        {
            OwnerKeys.Caller who = OwnerKeys.CallerOf(env.Variable, reader.Proc);
            if (who.Keys.Count is 0)
            {
                return null;
            }

            keys = who.Keys;
            scope = "owner " + OwnerKeys.Short(keys[0]) + (who.RelayName is { } relay ? " (" + relay + ")" : string.Empty);
            caller = new JsonObject { ["keys"] = RunOwner.Array(keys), ["relay_name"] = who.RelayName };
        }

        (List<StatusRow> rows, bool complete) = StatusIndexScan.Read(env.IndexFolder, keys, reader, budget, reap: true);
        TimeSpan? window = since ?? (a.All ? DefaultSince : null);
        return new Listing(scope, [.. rows.Select(r => StatusText.Checked(r, now))], window, OwnerColumn: a.All, caller,
            a.All ? "all" : "owner", complete ? null : ListingStopped);
    }

    private const string ListingStopped = "the listing stopped at an entry that did not answer in time";

    /// <summary>
    /// The listing: a gate chain is one row (its worst round), --since hides old ended rows, the
    /// rows go in display order with nested runs right after their parent; --json keeps every round.
    /// </summary>
    private static async Task PrintAsync(TextWriter output, bool json, Listing listing, StatusReader reader, DateTimeOffset now, string sinceText)
    {
        StatusLayout.View view = StatusLayout.Chains(listing.Rows, reader, now);
        // A chain is hidden only when every one of its rounds would be: never while one is live or needs to be seen.
        List<StatusRow> shown = listing.Since is { } window ? [.. view.Rows.Where(r => view.Rounds[r].Exists(m => Shown(m, now - window)))] : view.Rows;
        int hidden = view.Rows.Count - shown.Count;
        List<(StatusRow Row, int Depth)> nested = StatusLayout.Nest(StatusText.Order(shown), view.ByRunId);
        if (json)
        {
            IEnumerable<StatusRow> rounds = nested.SelectMany(n => view.Rounds[n.Row].Select(r => r with { Depth = n.Depth }));
            JsonObject doc = StatusText.Document(rounds, now, listing.Name, listing.Caller);
            await output.WriteLineAsync(doc.ToJsonString(Json.Indented)).ConfigureAwait(false);
            return;
        }

        string header = StatusText.Header(listing.Scope, shown, hidden, sinceText);
        await output.WriteLineAsync(listing.Note is { } note ? header + ", " + note : header).ConfigureAwait(false);
        foreach (string line in StatusText.Lines([.. nested.Select(n => n.Row with { Depth = n.Depth })], now, listing.OwnerColumn))
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a listing with a window lists a row: live, orphaned, unknown, unreadable and
    /// shared-dir rows always, whatever their age (they are what the operator has to see); ended
    /// rows with a final state (lost and gone included) and legacy rows when they ended (or,
    /// without an end time, started) after the cutoff, or carry no time at all.
    /// </summary>
    private static bool Shown(StatusRow r, DateTimeOffset cutoff)
    {
        if (r.Live || r.State is StatusStates.Orphaned or StatusStates.Unknown or StatusStates.Unreadable or StatusStates.SharedDir)
        {
            return true;
        }

        return (r.EndedAt ?? r.StartedAt) is not { } t || t >= cutoff;
    }

    internal static StatusArgs Parse(string[] args) => StatusOptions.Parse(args);

    /// <summary>
    /// A duration: a whole number and a unit, s, m, h or d (e.g. 90m, 24h, 7d).
    /// </summary>
    internal static TimeSpan ParseDuration(string text) => StatusOptions.ParseDuration(text);

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
    /// The run dirs under a runs root: every directory in &lt;runs-root&gt;/runs and every symbolic
    /// link there (listed, never followed: the reader makes it an unreadable row); none when the
    /// folder does not exist. One level only, so no link can lead the scan elsewhere or round a
    /// loop. The listing runs on the thread with the deadline: .NET's enumeration stats the target
    /// of an entry that is a link (to tell a directory), which can block; Complete is false when
    /// the listing did not finish in time, and the entries found until then are kept.
    /// </summary>
    private static (List<string> Dirs, bool Complete) RunDirs(string runsRoot, ReadBudget budget) =>
        budget.List(add => Collect(Path.Combine(runsRoot, "runs"), add));

    private static void Collect(string runs, Action<string> add)
    {
        if (!Directory.Exists(runs))
        {
            return;
        }

        IEnumerable<FileSystemInfo> entries = new DirectoryInfo(runs).EnumerateFileSystemInfos();
        foreach (FileSystemInfo e in entries.Where(e => e is DirectoryInfo || e.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            add(e.FullName);
        }
    }
}
