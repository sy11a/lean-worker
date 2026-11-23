// The owner and --all scopes of `status` (docs/research/live-status/README.md, section 4.1, "What a
// reader does, per pointer", and reaping): the pointers of the per-user index, read as untrusted
// input through StatusFile, matched against the caller's owner keys (and, transitively, through
// parent_run), and turned into rows; the launcher identity of the ended rule is the pointer's.
// Pointers of runs that provably ended long ago are deleted here, one file at a time.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusIndexScan
{
    /// <summary>
    /// How long after its end (or, for a lost or gone run, after its pointer was made) a run's
    /// pointer is reaped.
    /// </summary>
    public static readonly TimeSpan ReapAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// The longest run_dir a pointer may name.
    /// </summary>
    private const int RunDirLimit = 4096;

    /// <summary>
    /// One pointer as the reader uses it: every field checked, the owner keys that pass the key
    /// check only.
    /// </summary>
    internal sealed record Pointer(string File, string RunDir, string? ParentRun, List<string> OwnerKeys, JsonObject Launcher, DateTimeOffset? CreatedAt)
    {
        public string RunId => Path.GetFileName(RunDir);

        /// <summary>
        /// The run ids its <c>run:&lt;id&gt;</c> owner keys name (a run launched with
        /// <c>--owner run:&lt;id&gt;</c> belongs to that run as a child does).
        /// </summary>
        public IEnumerable<string> OwnerRuns
        {
            get
            {
                return OwnerKeys
                    .Where(k => k.StartsWith(LeanWorker.OwnerKeys.RunPrefix, StringComparison.Ordinal))
                    .Select(k => LeanWorker.OwnerKeys.RunId(k[LeanWorker.OwnerKeys.RunPrefix.Length..]))
                    .OfType<string>();
            }
        }

        /// <summary>
        /// The run this one is listed under: its parent_run, else the first run its owner keys
        /// name that is in <paramref name="runs"/>; null when neither.
        /// </summary>
        public string? ParentIn(IReadOnlySet<string> runs) => ParentRun is { } parent && runs.Contains(parent) ? parent : OwnerRuns.FirstOrDefault(r => runs.Contains(r));
    }

    /// <summary>
    /// One index entry: its pointer, or why it cannot be used.
    /// </summary>
    private sealed record Entry(string File, Pointer? Pointer, string? Problem);

    /// <summary>
    /// The rows of the index in <paramref name="folder"/>: every pointer whose owner keys meet
    /// <paramref name="keys"/> or whose parent_run is such a run (null: every pointer), and every
    /// pointer that cannot be read (its owner cannot be told, so it is shown in every scope).
    /// Complete is false when the listing stopped at an entry that did not answer in time. With
    /// <paramref name="reap"/>, pointers whose row proves the run ended long ago are deleted.
    /// </summary>
    public static (List<StatusRow> Rows, bool Complete) Read(string? folder, IReadOnlyCollection<string>? keys, StatusReader reader,
        ReadBudget budget, bool reap)
    {
        if (folder is null)
        {
            return ([], true);
        }

        (List<string> names, bool complete) = StatusIndex.List(folder, budget);
        List<Entry> entries = [.. names.Order(StringComparer.Ordinal).Select(n => Load(folder, n, budget)).OfType<Entry>()];
        HashSet<string> shared = Shared(entries);
        HashSet<string> indexed = new(entries.Select(e => e.Pointer?.RunId).OfType<string>(), StringComparer.Ordinal);
        HashSet<string> scope = keys is null
            ? new HashSet<string>(entries.Select(e => e.File), StringComparer.Ordinal)
            : Match(entries, keys);
        DateTimeOffset now = reader.Clock.GetUtcNow();
        List<StatusRow> rows = [];
        foreach (Entry entry in entries)
        {
            if (entry.Pointer is not { } pointer)
            {
                rows.Add(UnreadablePointer(folder, entry));
                continue;
            }

            if (!scope.Contains(entry.File))
            {
                continue;
            }

            StatusRow row = RowFor(pointer, shared, indexed, reader, budget);
            rows.Add(row);
            if (reap && Reapable(row, pointer, now))
            {
                StatusIndex.Reap(folder, entry.File, budget);
            }
        }

        return (rows, complete);
    }

    /// <summary>
    /// The entry of one pointer file; null when it went away since the listing (another reader reaped it).
    /// </summary>
    private static Entry? Load(string folder, string name, ReadBudget budget)
    {
        StatusFile.Content content = StatusFile.Read(Path.Combine(folder, name), budget);
        if (content.Missing)
        {
            return null;
        }

        if (content.Bytes is not { } bytes)
        {
            return new Entry(name, Pointer: null, content.Problem);
        }

        if (StatusFile.ParseObject(bytes, out string? problem) is not { } doc)
        {
            return new Entry(name, Pointer: null, problem);
        }

        return Parse(name, doc) is { } pointer
            ? new Entry(name, pointer, Problem: null)
            : new Entry(name, Pointer: null, $"is not a {StatusIndex.Schema} the reader can use");
    }

    /// <summary>
    /// The fields of a pointer: the schema, an absolute and normalised run_dir and a launcher
    /// object are required; owner keys that fail the key check and a parent_run that is no run id
    /// are left out.
    /// </summary>
    internal static Pointer? Parse(string file, JsonObject doc)
    {
        if (Json.Str(doc, "schema") is not StatusIndex.Schema || Json.Str(doc, "run_dir") is not { } runDir
            || doc["launcher"] is not JsonObject launcher || !PlainAbsolute(runDir))
        {
            return null;
        }

        List<string> keys = doc["owner_keys"] is JsonArray array
            ? [.. array.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).Where(s => OwnerKeys.Valid(s)).Select(s => s!)]
            : [];
        return new Pointer(file, runDir, OwnerKeys.RunId(Json.Str(doc, "parent_run")), keys, launcher,
            StatusReader.Time(Json.Str(doc, "created_at")));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is an absolute path in its normal form (no "." or ".."
    /// part, no doubled or trailing separator) of a sensible length.
    /// </summary>
    internal static bool PlainAbsolute(string path)
    {
        if (path.Length is 0 or > RunDirLimit || path.Any(c => c is '\0') || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal)
                   && string.Equals(Path.TrimEndingDirectorySeparator(path), path, StringComparison.Ordinal)
                   && Path.GetFileName(path).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // The run dirs that two or more pointers name.
    private static HashSet<string> Shared(List<Entry> entries)
    {
        IEnumerable<string> dirs = entries.Select(e => e.Pointer?.RunDir).OfType<string>()
            .GroupBy(d => d, StringComparer.Ordinal).Where(g => g.Skip(1).Any()).Select(g => g.Key);
        return new HashSet<string>(dirs, StringComparer.Ordinal);
    }

    /// <summary>
    /// The pointers in scope: those whose owner keys meet <paramref name="keys"/>, then, until
    /// nothing changes, those whose parent_run, or a <c>run:&lt;id&gt;</c> owner key, names a run in
    /// scope. A run key among the caller's keys (the caller is that run) puts that run's own
    /// children in scope too.
    /// </summary>
    private static HashSet<string> Match(List<Entry> entries, IReadOnlyCollection<string> keys)
    {
        HashSet<string> wanted = new(keys, StringComparer.Ordinal);
        HashSet<string> files = new(StringComparer.Ordinal);
        HashSet<string> runs = new(keys.Where(k => k.StartsWith(OwnerKeys.RunPrefix, StringComparison.Ordinal)).Select(k => k[OwnerKeys.RunPrefix.Length..]),
            StringComparer.Ordinal);
        List<Pointer> pointers = [.. entries.Select(e => e.Pointer).OfType<Pointer>()];
        foreach (Pointer p in pointers.Where(p => p.OwnerKeys.Exists(k => wanted.Contains(k))))
        {
            _ = files.Add(p.File);
            _ = runs.Add(p.RunId);
        }

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Pointer p in pointers.Where(p => !files.Contains(p.File) && p.ParentIn(runs) is not null))
            {
                _ = files.Add(p.File);
                _ = runs.Add(p.RunId);
                grew = true;
            }
        }

        return files;
    }

    /// <summary>
    /// The row of one pointer: its parent is its parent_run (the pointer's, else the status
    /// file's), else the first indexed run a <c>run:&lt;id&gt;</c> owner key names, so it is listed
    /// under that run.
    /// </summary>
    private static StatusRow RowFor(Pointer p, HashSet<string> shared, HashSet<string> indexed, StatusReader reader, ReadBudget budget)
    {
        string? owner = p.OwnerKeys.Count > 0 ? p.OwnerKeys[0] : null;
        string? ownerRun = p.OwnerRuns.FirstOrDefault(r => indexed.Contains(r));
        if (shared.Contains(p.RunDir))
        {
            return new StatusRow(p.RunDir, StatusReader.StampName(p.RunId), StatusStates.SharedDir, Liveness.Unknown, Status: null,
                "another pointer names the same run dir; neither row can be trusted", StartedAt: null, EndedAt: null, Attention: true, Quiet: null)
            {
                Pointer = p.File,
                PointerLauncher = p.Launcher,
                PointerCreatedAt = p.CreatedAt,
                OwnerKey = owner,
                ParentRun = p.ParentRun ?? ownerRun,
            };
        }

        StatusRow row = reader.Read(p.RunDir, budget, p.Launcher);
        return row with { Pointer = p.File, PointerLauncher = p.Launcher, PointerCreatedAt = p.CreatedAt, OwnerKey = owner ?? row.OwnerKey, ParentRun = p.ParentRun ?? row.ParentRun ?? ownerRun };
    }

    private static StatusRow UnreadablePointer(string folder, Entry entry)
    {
        return new StatusRow(Path.Combine(folder, entry.File), entry.File, StatusStates.Unreadable, Liveness.Unknown, Status: null,
            $"index pointer {entry.Problem ?? "cannot be used"}; not deleted", StartedAt: null, EndedAt: null, Attention: true, Quiet: null)
        {
            Pointer = entry.File,
        };
    }

    /// <summary>
    /// Whether a pointer may go, on proof only: the run is final and ended more than
    /// <see cref="ReapAfter"/> ago, or it is lost or gone and its pointer is older than that.
    /// Orphaned, unknown, unreadable, ending and shared-dir rows never go.
    /// </summary>
    internal static bool Reapable(StatusRow row, Pointer pointer, DateTimeOffset now)
    {
        if (row.State is StatusStates.Lost or StatusStates.Gone)
        {
            return pointer.CreatedAt is { } created && now - created > ReapAfter;
        }

        if (row.State is StatusStates.Orphaned or StatusStates.Unknown or StatusStates.Unreadable or StatusStates.Ending or StatusStates.SharedDir)
        {
            return false;
        }

        return row.Ended && row.EndedAt is { } ended && now - ended > ReapAfter;
    }
}
