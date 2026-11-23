// The one-shot listing of `status` (docs/research/live-status/README.md, section 4.2): one row per
// gate chain, --since hides old ended rows, --started-after keeps the runs started at or after a
// time, and the rows are printed in display order with nested runs right after their parent
// (plain), or as the --json document with every round of a chain.

using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusListing
{
    /// <summary>
    /// The rows a listing shows: the chain view of the read, the chain rows kept (one per chain)
    /// and how many the --since window hid.
    /// </summary>
    internal sealed record Shown(StatusLayout.View View, List<StatusRow> Rows, int Hidden);

    /// <summary>
    /// The chain rows of <paramref name="listing"/> that are shown: a chain is hidden by --since only
    /// when every one of its rounds would be (never while one is live or needs to be seen), and
    /// with <paramref name="startedAfter"/> only the chains whose first round started at or after it
    /// are kept (a run whose start time cannot be told is not).
    /// </summary>
    public static Shown Select(StatusScope.Listing listing, StatusReader reader, DateTimeOffset now, DateTimeOffset? startedAfter)
    {
        StatusLayout.View view = StatusLayout.Chains(listing.Rows, reader, now);
        List<StatusRow> inWindow = listing.Since is { } window ? [.. view.Rows.Where(r => view.Rounds[r].Exists(m => InWindow(m, now - window)))] : view.Rows;
        List<StatusRow> rows = startedAfter is { } t ? [.. inWindow.Where(r => StartedAfter(view.Rounds[r][0], t))] : inWindow;
        return new Shown(view, rows, view.Rows.Count - inWindow.Count);
    }

    /// <summary>
    /// The key of a shown row across reads (for --wait and --watch): its first round's index
    /// pointer, else that round's run dir, so a gate chain keeps its key as rounds are added.
    /// </summary>
    public static string UnitKey(StatusRow head, StatusLayout.View view)
    {
        StatusRow first = view.Rounds.TryGetValue(head, out List<StatusRow>? rounds) && rounds.Count > 0 ? rounds[0] : head;
        return first.Pointer is { } pointer ? "p:" + pointer : "d:" + Path.GetFullPath(first.Dir);
    }

    /// <summary>
    /// Whether a run (a chain's first round) started at or after <paramref name="t"/>.
    /// </summary>
    public static bool StartedAfter(StatusRow first, DateTimeOffset t) => first.StartedAt is { } s && s >= t;

    /// <summary>
    /// The listing: the header and one line per shown row (plain), or the --json document.
    /// </summary>
    public static async Task PrintAsync(TextWriter output, bool json, StatusScope.Listing listing, Shown shown, DateTimeOffset now, string sinceText)
    {
        List<(StatusRow Row, int Depth)> nested = StatusLayout.Nest(StatusText.Order(shown.Rows), shown.View.ByRunId);
        if (json)
        {
            IEnumerable<StatusRow> rounds = nested.SelectMany(n => shown.View.Rounds[n.Row].Select(r => r with { Depth = n.Depth }));
            JsonObject doc = StatusText.Document(rounds, now, listing.Name, listing.Caller);
            await output.WriteLineAsync(doc.ToJsonString(Json.Indented)).ConfigureAwait(false);
            return;
        }

        string header = StatusText.Header(listing.Scope, shown.Rows, shown.Hidden, sinceText);
        await output.WriteLineAsync(listing.Note is { } note ? header + ", " + note : header).ConfigureAwait(false);
        await PrintLinesAsync(output, [.. nested.Select(n => n.Row with { Depth = n.Depth })], now, listing.OwnerColumn).ConfigureAwait(false);
    }

    /// <summary>
    /// The run lines of rows already in display order and nested.
    /// </summary>
    public static async Task PrintLinesAsync(TextWriter output, List<StatusRow> rows, DateTimeOffset now, bool ownerColumn)
    {
        foreach (string line in StatusText.Lines(rows, now, ownerColumn))
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
    private static bool InWindow(StatusRow r, DateTimeOffset cutoff)
    {
        if (r.Live || r.State is StatusStates.Orphaned or StatusStates.Unknown or StatusStates.Unreadable or StatusStates.SharedDir)
        {
            return true;
        }

        return (r.EndedAt ?? r.StartedAt) is not { } t || t >= cutoff;
    }
}
