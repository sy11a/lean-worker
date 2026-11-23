// The scopes of `status` (docs/research/live-status/README.md, section 4.2): one run (--run), this
// repository's runs (--here, a scan), every indexed run (--all), one owner's (--owner) or the
// caller's own (no scope, through the per-user index). One read of a scope gives one listing; the
// one-shot listing reads once, --wait and --watch read again every interval.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusScope
{
    /// <summary>
    /// --since for ended and legacy rows of --here and --all when none is given.
    /// </summary>
    public static readonly TimeSpan DefaultSince = TimeSpan.FromHours(24);

    private const string ListingStopped = "the listing stopped at an entry that did not answer in time";

    /// <summary>
    /// What one scope gave: the header's scope text, the rows (every round of a chain), the window
    /// for ended rows (null: none), whether rows show their owner, the caller for --json, the
    /// scope's name and a note for the header.
    /// </summary>
    internal sealed record Listing(string Scope, List<StatusRow> Rows, TimeSpan? Since, bool OwnerColumn, JsonObject? Caller, string Name, string? Note);

    /// <summary>
    /// One read of the scope <paramref name="a"/> names, every row checked at <paramref name="now"/>;
    /// null when --run names no directory, or there is no scope and nothing tells who the caller
    /// is. Only a read with <paramref name="reap"/> deletes the pointers of runs that provably ended
    /// long ago (the one-shot call's read, and the first read of --wait and --watch).
    /// </summary>
    public static async Task<Listing?> ReadAsync(StatusCommand.StatusArgs a, string cwd, StatusReader reader, StatusEnvironment env,
        DateTimeOffset now, TimeSpan? since, bool reap)
    {
        ReadBudget budget = reader.NewBudget();
        if (a.Run is { } run)
        {
            return RunScope(run, cwd, reader, budget, now);
        }

        return a.Here
            ? await HereScopeAsync(a, cwd, reader, budget, now, since).ConfigureAwait(false)
            : IndexScope(a, env, reader, budget, now, since, reap);
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

    private static async Task<Listing> HereScopeAsync(StatusCommand.StatusArgs a, string cwd, StatusReader reader, ReadBudget budget, DateTimeOffset now, TimeSpan? since)
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
    private static Listing? IndexScope(StatusCommand.StatusArgs a, StatusEnvironment env, StatusReader reader, ReadBudget budget, DateTimeOffset now,
        TimeSpan? since, bool reap)
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

        (List<StatusRow> rows, bool complete) = StatusIndexScan.Read(env.IndexFolder, keys, reader, budget, reap);
        TimeSpan? window = since ?? (a.All ? DefaultSince : null);
        return new Listing(scope, [.. rows.Select(r => StatusText.Checked(r, now))], window, OwnerColumn: a.All, caller,
            a.All ? "all" : "owner", complete ? null : ListingStopped);
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
