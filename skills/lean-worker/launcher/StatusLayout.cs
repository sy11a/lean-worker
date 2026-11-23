// How `status` lays its rows out (docs/research/live-status/README.md, sections 4.2 and 4.4): a
// gate chain is one row, its latest round (issue #142), and a run launched inside another run is
// listed right after that run, indented two spaces per level. --json keeps every round of a chain
// as its own entry, each with a `chain` field.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusLayout
{
    /// <summary>
    /// The deepest nesting level shown; deeper runs are shown at this level.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// The rows of a listing: one per chain (<see cref="Rows"/>), the rounds behind each one
    /// (<see cref="Rounds"/>, every round of a chain in round order) and the shown row of every run
    /// id, a chain's earlier rounds included (<see cref="ByRunId"/>).
    /// </summary>
    internal sealed record View(
        List<StatusRow> Rows,
        Dictionary<StatusRow, List<StatusRow>> Rounds,
        Dictionary<string, StatusRow> ByRunId);

    /// <summary>
    /// Groups the rows by their status file's chain id: the chain's row is its worst round
    /// (attention over live over ended), the latest (the highest round number) among equals; an
    /// earlier round with an end is final, not ending, though its launcher still runs the later
    /// round (<see cref="StatusReader.EarlierRound"/>). A row without a chain id is a chain of its own.
    /// </summary>
    public static View Chains(IReadOnlyList<StatusRow> rows, StatusReader reader, DateTimeOffset now)
    {
        List<StatusRow> shown = [];
        Dictionary<StatusRow, List<StatusRow>> rounds = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, StatusRow> byRunId = new(StringComparer.Ordinal);
        foreach (List<StatusRow> members in Groups(rows))
        {
            StatusRow latest = members[^1];
            string? chainId = members.Count > 1 ? ChainId(latest) : null;
            List<StatusRow> derived = [.. members.Select(m => Derive(m, latest, chainId, members.Count, reader, now))];
            StatusRow head = derived[^1];
            foreach (StatusRow d in derived.Where(d => Rank(d) > Rank(head)))
            {
                head = d;
            }

            shown.Add(head);
            rounds[head] = derived;
            foreach (StatusRow m in members)
            {
                _ = byRunId.TryAdd(m.RunId, head);
            }
        }

        return new View(shown, rounds, byRunId);
    }

    // Attention over live over ended: what a chain's row shows.
    private static int Rank(StatusRow r)
    {
        if (r.Attention)
        {
            return 2;
        }

        return r.Live ? 1 : 0;
    }

    /// <summary>
    /// The chains, each in round order. Every round of a chain is run by the same launcher process
    /// (Launcher.RunAsync loops over the rounds), which the launcher names the chain after its first
    /// round's run dir; so a chain is the run whose id is the chain id plus the rows of that chain
    /// id whose launcher identity (pid, start ticks and boot id, the same in the status file and
    /// the pointer) is that run's, in the shape an honest chain has (<see cref="Shaped"/>). When
    /// they do not, every row of the chain id stands alone, and none is hidden behind another.
    /// </summary>
    private static IEnumerable<List<StatusRow>> Groups(IReadOnlyList<StatusRow> rows)
    {
        IEnumerable<IGrouping<string, StatusRow>> groups = rows.Select((r, i) => (Row: r, Key: ChainKey(r, i)))
            .GroupBy(x => x.Key, x => x.Row, StringComparer.Ordinal);
        foreach (IGrouping<string, StatusRow> group in groups)
        {
            List<StatusRow> all = [.. group.OrderBy(r => Round(r))];
            List<StatusRow> members = all.Count is 1 ? [] : Members(all);
            HashSet<StatusRow> chained = new(ReferenceEqualityComparer.Instance);
            if (members.Count > 1)
            {
                chained.UnionWith(members);
                yield return members;
            }

            foreach (StatusRow alone in all.Where(r => !chained.Contains(r)))
            {
                yield return [alone];
            }
        }
    }

    /// <summary>
    /// The rows of <paramref name="claims"/> (one chain id, in round order) that are the chain: its
    /// first run and the rows launched by that run's launcher, when they have a chain's shape;
    /// empty when they are no chain.
    /// </summary>
    private static List<StatusRow> Members(List<StatusRow> claims)
    {
        string? id = ChainId(claims[0]);
        if (claims.Find(m => string.Equals(m.RunId, id, StringComparison.Ordinal)) is not { } first || LauncherIdentity(first) is not { } launcher)
        {
            return [];
        }

        List<StatusRow> members = [.. claims.Where(r => string.Equals(LauncherIdentity(r), launcher, StringComparison.Ordinal))];
        return Shaped(members, first) ? members : [];
    }

    /// <summary>
    /// Whether <paramref name="members"/> (in round order) have the shape an honest chain has; the
    /// launcher identity can be copied from another run's files, so the proof alone is not enough.
    /// The launcher numbers a chain's rounds 1, 2, ... in one process (round 1 is the run whose dir
    /// names the chain; Launcher.RunAsync starts round k+1 only after round k's gate said continue,
    /// and the gate runs only after a <c>success</c> worker, which is the status round k's end
    /// records). So the rounds are exactly 1..N with <paramref name="first"/> as round 1; every
    /// round below N ended <c>success</c> or has no end (its final write was lost); and a round that
    /// reads live and not ending can only be round N (the one round its launcher still runs).
    /// </summary>
    private static bool Shaped(List<StatusRow> members, StatusRow first)
    {
        if (!ReferenceEquals(members[0], first))
        {
            return false;
        }

        for (int i = 0; i < members.Count; i++)
        {
            StatusRow r = members[i];
            if (Round(r) != i + 1)
            {
                return false;
            }

            bool last = i == members.Count - 1;
            if (!last && r.Status?["end"] is { } end
                && !(end is JsonObject ended && string.Equals(Json.Str(ended, "status"), "success", StringComparison.Ordinal)))
            {
                return false;
            }

            if (!last && r.Live && r.State is not StatusStates.Ending)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The launcher identity a row proves: pid, start ticks and boot id of its status file, which
    /// the pointer it was read through (if any) must give too; null when either lacks one.
    /// </summary>
    private static string? LauncherIdentity(StatusRow r)
    {
        string? own = Identity(r.Status?["launcher"] as JsonObject);
        return r.PointerLauncher is null || string.Equals(Identity(r.PointerLauncher), own, StringComparison.Ordinal) ? own : null;
    }

    /// <summary>
    /// The launcher a row names, unproven: its pointer's (written once at launch), else its status
    /// file's; null when it names none in full. Only for making a wait stricter (StatusWait), never
    /// for grouping.
    /// </summary>
    internal static string? NamedLauncher(StatusRow r) => Identity(r.PointerLauncher ?? r.Status?["launcher"] as JsonObject);

    private static string? Identity(JsonObject? launcher)
    {
        long? pid = StatusProbe.NumOrNull(launcher?["pid"]);
        long? ticks = StatusProbe.NumOrNull(launcher?["start_ticks"]);
        string? boot = Json.Str(launcher, "boot_id");
        return pid is > 0 && ticks is { } t && boot is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{pid}/{t}/{boot}")
            : null;
    }

    private static StatusRow Derive(StatusRow m, StatusRow latest, string? chainId, int count, StatusReader reader, DateTimeOffset now)
    {
        if (chainId is null)
        {
            return m;
        }

        // A round derived again is checked again, as every row is before it is shown.
        StatusRow d = ReferenceEquals(m, latest) ? m : StatusText.Checked(reader.EarlierRound(m), now);
        return d with
        {
            Chain = new JsonObject
            {
                ["id"] = chainId,
                ["round"] = Round(m),
                ["rounds"] = count,
                ["latest"] = ReferenceEquals(m, latest),
            },
        };
    }

    // The status file's chain id, else a key of the row's own (its index), so it is a chain alone.
    private static string ChainKey(StatusRow r, int index) =>
        ChainId(r) is { } id ? "c:" + id : "i:" + index.ToString(CultureInfo.InvariantCulture);

    internal static string? ChainId(StatusRow r) => Json.Str(r.Status?["run"] as JsonObject, "chain_id") is { Length: > 0 } id ? id : null;

    private static long Round(StatusRow r) => StatusProbe.NumOrNull((r.Status?["run"] as JsonObject)?["round"]) ?? 0;

    /// <summary>
    /// The rows in <paramref name="ordered"/>'s order, each run launched inside another listed run
    /// moved right after it, one level deeper (at most <see cref="MaxDepth"/>). A parent_run that
    /// names no listed run leaves the row at the top level; a cycle of parent_runs is broken where
    /// the walk first meets it, so every row is listed exactly once.
    /// </summary>
    public static List<(StatusRow Row, int Depth)> Nest(IReadOnlyList<StatusRow> ordered, IReadOnlyDictionary<string, StatusRow> byRunId)
    {
        HashSet<StatusRow> listed = new(ordered, ReferenceEqualityComparer.Instance);
        Dictionary<StatusRow, List<StatusRow>> children = new(ReferenceEqualityComparer.Instance);
        List<StatusRow> roots = [];
        foreach (StatusRow r in ordered)
        {
            if (r.ParentRun is { } p && byRunId.TryGetValue(p, out StatusRow? parent) && !ReferenceEquals(parent, r) && listed.Contains(parent))
            {
                if (!children.TryGetValue(parent, out List<StatusRow>? list))
                {
                    list = [];
                    children[parent] = list;
                }

                list.Add(r);
            }
            else
            {
                roots.Add(r);
            }
        }

        List<(StatusRow Row, int Depth)> result = [];
        HashSet<StatusRow> seen = new(ReferenceEqualityComparer.Instance);
        // The roots first; then whatever only a cycle reaches, from the first row of it in order.
        foreach (StatusRow start in roots.Concat(ordered))
        {
            Walk(start, children, seen, result);
        }

        return result;
    }

    private static void Walk(StatusRow start, Dictionary<StatusRow, List<StatusRow>> children, HashSet<StatusRow> seen, List<(StatusRow Row, int Depth)> result)
    {
        Stack<(StatusRow Row, int Depth)> stack = new();
        stack.Push((start, 0));
        while (stack.Count > 0)
        {
            (StatusRow row, int depth) = stack.Pop();
            if (!seen.Add(row))
            {
                continue;
            }

            result.Add((row, Math.Min(depth, MaxDepth)));
            if (children.TryGetValue(row, out List<StatusRow>? kids))
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    stack.Push((kids[i], depth + 1));
                }
            }
        }
    }
}
