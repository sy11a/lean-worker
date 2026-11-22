// The one-shot output of `status`: one header line and one line per run, plain text without
// escapes (docs/research/live-status/README.md, section 4.2), and the --json document.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusText
{
    public const string ListSchema = "lean-worker.status-list/1";

    private static readonly CultureInfo _ic = CultureInfo.InvariantCulture;

    /// <summary>
    /// The rows in display order: attention first (newest first), then live runs by start, then the
    /// rest, newest first.
    /// </summary>
    public static List<StatusRow> Order(IEnumerable<StatusRow> rows) => [.. rows.OrderBy(r => Group(r)).ThenBy(r => SortKey(r))];

    // Live rows by start (oldest first); attention and ended rows newest first.
    private static long SortKey(StatusRow r) => Group(r) is 1 ? Ticks(r.StartedAt) : -Ticks(r.EndedAt ?? r.StartedAt);

    private static int Group(StatusRow r)
    {
        if (r.Attention)
        {
            return 0;
        }

        return r.Live ? 1 : 2;
    }

    private static long Ticks(DateTimeOffset? t) => t?.UtcTicks ?? 0;

    /// <summary>
    /// The header: what was read and how many runs are live, ended and need attention.
    /// </summary>
    public static string Header(string scope, IReadOnlyCollection<StatusRow> rows, int hidden, string? since)
    {
        int attention = rows.Count(r => r.Attention);
        string text = string.Create(_ic,
            $"lean-worker status: {TerminalText.Clean(scope, ScopeLimit)}, {rows.Count(r => r.Live)} live, {rows.Count(r => r.Ended)} ended, {attention} {(attention is 1 ? "needs" : "need")} attention");
        int legacy = rows.Count(r => r.State is StatusStates.Legacy);
        if (legacy > 0)
        {
            text += string.Create(_ic, $", {legacy} legacy");
        }

        if (hidden > 0)
        {
            text += string.Create(_ic, $", {hidden} older than {since} not shown");
        }

        return text;
    }

    /// <summary>
    /// The longest value of each column the plain output prints (state, name, runtime, model,
    /// calls, cost, elapsed, detail); a longer one is cut with a trailing <see cref="TerminalText.CutMark"/>.
    /// </summary>
    internal static readonly int[] CellLimits = [24, 120, 16, 64, 24, 32, 16, 300];

    /// <summary>
    /// The longest scope the header prints.
    /// </summary>
    internal const int ScopeLimit = 300;

    /// <summary>
    /// The run lines, the columns padded to the widest value. Every cell passes through
    /// <see cref="TerminalText.Clean"/> with its column's limit, so one run stays one line whatever
    /// its files hold.
    /// </summary>
    public static List<string> Lines(IReadOnlyList<StatusRow> rows, DateTimeOffset now)
    {
        List<string[]> cells = [.. rows.Select(r => Cells(r, now).Select((cell, c) => TerminalText.Clean(cell, CellLimits[c])).ToArray())];
        int columns = cells.Count > 0 ? cells[0].Length : 0;
        int[] widths = [.. Enumerable.Range(0, columns).Select(c => cells.Max(row => row[c].Length))];
        return [.. cells.Select(row => string.Join("  ", row.Select((cell, c) => Pad(cell, widths[c], c))).TrimEnd())];
    }

    // Numbers right-aligned (calls, cost, elapsed); text left-aligned; the detail is last and not padded.
    private static string Pad(string cell, int width, int column)
    {
        return column switch
        {
            4 or 5 or 6 => cell.PadLeft(width),
            7 => cell,
            _ => cell.PadRight(width),
        };
    }

    private static string[] Cells(StatusRow r, DateTimeOffset now)
    {
        if (r.Status is not { } doc)
        {
            return [r.State, r.Name, "-", "-", "-", "-", "-", r.Detail];
        }

        JsonObject? spec = doc["spec"] as JsonObject;
        JsonObject? live = doc["live"] as JsonObject;
        string runtime = Json.Str(spec, "runtime") switch { "claude" => "cc", "opencode" => "oc", { } other => other, null => "-" };
        string calls = Json.Num(live?["api_calls"]).ToString(_ic) + " calls";
        string cost = Money(Json.Dec(live, "cost_usd")) + "/" + Money(Json.Dec(spec, "budget_usd"));
        return [StateText(r, live), r.Name, runtime, Json.Str(spec, "model") ?? "-", calls, cost, Duration(Elapsed(r, doc, live, now)), r.Detail];
    }

    private static string StateText(StatusRow r, JsonObject? live)
    {
        return r.State is StatusStates.Gate && StatusProbe.NumOrNull(live?["gate_round"]) is { } round
            ? "gate r" + round.ToString(_ic)
            : r.State;
    }

    /// <summary>
    /// The run's elapsed time: the writer's monotonic count, plus the time since its last write
    /// while the run is live.
    /// </summary>
    private static TimeSpan Elapsed(StatusRow r, JsonObject doc, JsonObject? live, DateTimeOffset now)
    {
        TimeSpan elapsed = TimeSpan.FromMilliseconds(Json.Num(live?["elapsed_ms"]));
        if (r.Live && StatusReader.Time(Json.Str(doc, "updated_at")) is { } updated && now > updated)
        {
            elapsed += now - updated;
        }

        return elapsed;
    }

    /// <summary>
    /// The detail of a live run: the gate round, the last tool and its age, or the quiet age.
    /// </summary>
    public static string LiveDetail(string phase, JsonObject? live, DateTimeOffset now, TimeSpan? quiet)
    {
        if (phase is RunPhase.Done or RunPhase.Failed)
        {
            return $"phase {phase} written, launcher still running";
        }

        if (phase is RunPhase.Gate)
        {
            return StatusProbe.NumOrNull(live?["gate_round"]) is { } round ? "gate round " + round.ToString(_ic) + " running" : "gate running";
        }

        string tool = ToolText(live?["last_tool"] as JsonObject, now);
        if (quiet is { } q)
        {
            return "quiet " + Duration(q) + (tool.Length > 0 ? ", " + tool : string.Empty);
        }

        return tool.Length > 0 ? tool : "-";
    }

    private static string ToolText(JsonObject? tool, DateTimeOffset now)
    {
        if (Json.Str(tool, "name") is not { } name)
        {
            return string.Empty;
        }

        TimeSpan? age = StatusReader.Time(Json.Str(tool, "at")) is { } at && now > at ? now - at : null;
        if (Json.Str(tool, "state") is StreamActivity.Running)
        {
            return age is { } running ? $"{name} running for {Duration(running)}" : $"{name} running";
        }

        return age is { } since ? $"last {name} {Duration(since)} ago" : $"last {name}";
    }

    /// <summary>
    /// The detail of an ended run: the failure message for a launch failure (with the summary's
    /// status when it was written), else the run dir and the turn count.
    /// </summary>
    public static string EndDetail(string runDir, JsonObject doc, JsonObject end)
    {
        if (Json.Str(end, "status") is RunStatus.LaunchFailed)
        {
            string error = Json.Str(end, "error") ?? "launcher failed";
            return Json.Bool(end, "after_summary") ? $"(after summary: {Json.Str(end, "summary_status")}) {error}" : error;
        }

        if (Json.Str(end, "status") is "launcher-signalled")
        {
            return "(signalled)";
        }

        string turns = StatusProbe.NumOrNull(doc["live"]?["turns"]) is { } t ? ", " + t.ToString(_ic) + " turns" : string.Empty;
        return runDir + turns;
    }

    /// <summary>
    /// The --json document: each run its status.json content (or the directory-only fields) plus
    /// the derived state, liveness and detail.
    /// </summary>
    public static JsonObject Document(IEnumerable<StatusRow> rows, DateTimeOffset now)
    {
        return new JsonObject
        {
            ["schema"] = ListSchema,
            ["generated_at"] = RunStatus.Stamp(now),
            ["runs"] = new JsonArray([.. rows.Select(r => (JsonNode)RowJson(r))]),
        };
    }

    private static JsonObject RowJson(StatusRow r)
    {
        JsonObject o = r.Status?.DeepClone().AsObject() ?? new JsonObject
        {
            ["dir"] = Path.GetFullPath(r.Dir),
            ["id"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(r.Dir)),
            ["name"] = r.Name,
            ["started_at"] = r.StartedAt is { } t ? RunStatus.Stamp(t) : null,
            ["summary_status"] = r.SummaryStatus,
        };
        o["state"] = r.State;
        o["liveness"] = r.Liveness;
        o["detail"] = r.Detail;
        if (r.Quiet is { } q)
        {
            o["quiet_ms"] = (long)q.TotalMilliseconds;
        }

        return o;
    }

    private static string Money(decimal? usd) => usd is { } d ? "$" + d.ToString("0.00", _ic) : "-";

    /// <summary>
    /// A duration as the table shows it: 45s, 3m41s, 1h02m.
    /// </summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        if (t.TotalHours >= 1)
        {
            return string.Create(_ic, $"{(long)t.TotalHours}h{t.Minutes:00}m");
        }

        return t.TotalMinutes >= 1 ? string.Create(_ic, $"{t.Minutes}m{t.Seconds:00}s") : string.Create(_ic, $"{t.Seconds}s");
    }
}
