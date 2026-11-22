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
    /// Groups the rows by their status file's chain id: the latest round (the highest round number)
    /// stands for the chain; an earlier round with an end is final, not ending, though its launcher
    /// still runs the later round (<see cref="StatusReader.EarlierRound"/>). A row without a chain id
    /// is a chain of its own.
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
            shown.Add(head);
            rounds[head] = derived;
            foreach (StatusRow m in members)
            {
                _ = byRunId.TryAdd(m.RunId, head);
            }
        }

        return new View(shown, rounds, byRunId);
    }

    /// <summary>
    /// The chains, each in round order: rows of one chain id among which is the chain's first run
    /// (the launcher names a chain after its first round's run dir) and whose round numbers are all
    /// distinct (a chain has one run dir per round). Rows that claim a chain id otherwise are no
    /// chain's rounds, so each of them stands alone and none is hidden behind another.
    /// </summary>
    private static IEnumerable<List<StatusRow>> Groups(IReadOnlyList<StatusRow> rows)
    {
        IEnumerable<IGrouping<string, StatusRow>> groups = rows.Select((r, i) => (Row: r, Key: ChainKey(r, i)))
            .GroupBy(x => x.Key, x => x.Row, StringComparer.Ordinal);
        foreach (IGrouping<string, StatusRow> group in groups)
        {
            List<StatusRow> members = [.. group.OrderBy(r => Round(r))];
            if (members.Count is 1 || IsChain(members))
            {
                yield return members;
                continue;
            }

            foreach (StatusRow alone in members)
            {
                yield return [alone];
            }
        }
    }

    private static bool IsChain(List<StatusRow> members)
    {
        string? id = ChainId(members[0]);
        return members.Exists(m => string.Equals(m.RunId, id, StringComparison.Ordinal))
               && new HashSet<long>(members.Select(r => Round(r))).Count == members.Count;
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

    private static string? ChainId(StatusRow r) => Json.Str(r.Status?["run"] as JsonObject, "chain_id") is { Length: > 0 } id ? id : null;

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
