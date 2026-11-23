// The frame of `status --watch` on a terminal (docs/research/live-status/README.md, sections 4.2 and
// 4.3): a header, the column titles and one row per run, every cell cut to its column so no line
// wraps. Columns are dropped by width in a fixed order; below the five fixed columns the frame only
// says the terminal is too narrow. Every value from a status file or pointer passes through
// TerminalText.Clean with its limit first, so the only escapes in a frame are the ones added here.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusTable
{
    /// <summary>
    /// The narrowest frame: STATE, NAME at its narrowest, CALLS, COST and ELAPSED with their gaps.
    /// </summary>
    public const int MinWidth = 54;

    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";

    private const int NameMin = 16;
    private const int NameMax = 28;
    private const int Gap = 2;

    private static readonly CultureInfo _ic = CultureInfo.InvariantCulture;

    /// <summary>
    /// What one frame shows: the scope text, the rows in display order (nested, with their depth),
    /// the rows ended while the table was open and not acknowledged, whether STATE is coloured and
    /// whether rows carry an OWNER column.
    /// </summary>
    internal sealed record FrameData(string Scope, List<StatusRow> Rows, IReadOnlySet<StatusRow> Unacknowledged, bool Color, bool OwnerColumn);

    /// <summary>
    /// One column: its title, its width (LAST's is its narrowest), whether it is right-aligned and its cell.
    /// </summary>
    private sealed record Column(string Title, int Width, bool Right, Func<StatusRow, DateTimeOffset, string> Cell);

    private static readonly Column _state = new("STATE", 11, Right: false, (r, _) => StatusText.StateText(r, Live(r)));

    // NAME's width is the layout's; its cell is built in Text (indented by depth).
    private static readonly Column _nameMarker = new("NAME", NameMin, Right: false, (r, _) => r.Name);
    private static readonly Column _calls = new("CALLS", 5, Right: true, (r, _) => r.Status is null ? "-" : Json.Num(Live(r)?["api_calls"]).ToString(_ic));
    private static readonly Column _cost = new("COST", 7, Right: true, (r, _) => r.Status is null ? "-" : StatusText.Money(Json.Dec(Live(r), "cost_usd")));
    private static readonly Column _elapsed = new("ELAPSED", 7, Right: true,
        (r, now) => r.Status is { } doc ? StatusText.Duration(StatusText.Elapsed(r, doc, Live(r), now)) : "-");
    private static readonly Column _owner = new("OWNER", 12, Right: false, (r, _) => r.OwnerKey is { } key ? OwnerKeys.Short(key) : "-");
    private static readonly Column _profile = new("PROFILE", 13, Right: false, (r, _) => Json.Str(Spec(r), "profile") ?? "-");
    private static readonly Column _inOut = new("IN/OUT", 11, Right: true, (r, _) => Tokens(r));
    private static readonly Column _model = new("MODEL", 13, Right: false, (r, _) => Json.Str(Spec(r), "model") ?? "-");
    private static readonly Column _runtime = new("RT", 2, Right: false, (r, _) => Runtime(r));
    private static readonly Column _budget = new("BUDG", 4, Right: true, (r, _) => Budget(r));
    private static readonly Column _last = new("LAST", 12, Right: false, (r, now) => Last(r, now));

    /// <summary>
    /// The frame's lines for a window of <paramref name="width"/> by <paramref name="height"/>, with
    /// the escapes for colour and bold; at most <paramref name="height"/> lines, none wider than
    /// <paramref name="width"/> columns.
    /// </summary>
    public static List<string> Frame(FrameData data, DateTimeOffset now, int width, int height)
    {
        if (width < MinWidth)
        {
            return [Fit(string.Create(_ic, $"terminal too narrow ({MinWidth} columns needed)"), width, right: false)];
        }

        (List<Column> columns, int nameWidth) = Layout(width, data.OwnerColumn);
        List<string> lines = [Fit(Header(data, now), width, right: false), Join(columns.Select(c => (c, c.Title)), nameWidth)];
        int room = Math.Max(0, height - lines.Count);
        int shown = data.Rows.Count <= room ? data.Rows.Count : Math.Max(0, room - 1);
        lines.AddRange(data.Rows.Take(shown).Select(r => RowLine(r, columns, nameWidth, data, now)));
        if (shown < data.Rows.Count && room > 0)
        {
            lines.Add(Fit(string.Create(_ic, $"... {data.Rows.Count - shown} more (status --all for the full list)"), width, right: false));
        }

        return [.. lines.Take(Math.Max(1, height))];
    }

    /// <summary>
    /// The columns that fit in <paramref name="width"/>: the optional ones go in the order OWNER
    /// (only with --here and --all), PROFILE, IN/OUT, MODEL, RT, BUDG, LAST until the rest fits;
    /// NAME takes what is left up to 28, and LAST anything beyond that.
    /// </summary>
    private static (List<Column> Columns, int NameWidth) Layout(int width, bool ownerColumn)
    {
        List<Column> dropOrder = [.. ownerColumn ? [_owner] : Array.Empty<Column>(), _profile, _inOut, _model, _runtime, _budget, _last];
        HashSet<Column> kept = [.. dropOrder];
        int Needed() => Ordered(kept).Sum(c => c.Width) + (Gap * (Ordered(kept).Count - 1));
        foreach (Column c in dropOrder.Where(_ => Needed() > width))
        {
            _ = kept.Remove(c);
        }

        int spare = width - Needed();
        int nameWidth = Math.Min(NameMax, NameMin + spare);
        spare -= nameWidth - NameMin;
        List<Column> columns = [.. Ordered(kept).Select(c => c == _last ? c with { Width = c.Width + spare } : c)];
        return (columns, nameWidth);
    }

    // The columns in display order: STATE NAME [OWNER] [PROFILE] [RT] [MODEL] CALLS [IN/OUT] COST [BUDG] ELAPSED [LAST].
    private static List<Column> Ordered(HashSet<Column> kept)
    {
        Column[] all = [_state, _nameMarker, _owner, _profile, _runtime, _model, _calls, _inOut, _cost, _budget, _elapsed, _last];
        return [.. all.Where(c => c == _state || c == _nameMarker || c == _calls || c == _cost || c == _elapsed || kept.Contains(c))];
    }

    private static string Header(FrameData data, DateTimeOffset now)
    {
        int attention = data.Rows.Count(r => r.Attention);
        return string.Create(_ic,
            $"{TerminalText.Clean(data.Scope, StatusText.ScopeLimit)}   live {data.Rows.Count(r => r.Live)}  ended {data.Rows.Count(r => r.Ended)}  attention {attention}   {now:HH:mm:ss}");
    }

    private static string RowLine(StatusRow r, List<Column> columns, int nameWidth, FrameData data, DateTimeOffset now)
    {
        bool bold = data.Unacknowledged.Contains(r);
        List<string> cells = Cells(columns.Select(c => (c, Text(r, c, now))), nameWidth);
        string colour = data.Color ? Colour(r) : string.Empty;
        // The STATE cell is the first one: coloured, then the row's own attribute again.
        if (colour.Length > 0)
        {
            cells[0] = colour + cells[0] + Reset + (bold ? Bold : string.Empty);
        }

        string text = string.Join(new string(' ', Gap), cells).TrimEnd();
        return bold ? Bold + text + Reset : text;
    }

    // A cell's text before it is fitted: cleaned with the one-shot output's limits.
    private static string Text(StatusRow r, Column c, DateTimeOffset now)
    {
        if (c == _nameMarker)
        {
            return new string(' ', 2 * Math.Clamp(r.Depth, 0, StatusLayout.MaxDepth)) + TerminalText.Clean(r.Name, StatusText.CellLimits[1]);
        }

        return TerminalText.Clean(c.Cell(r, now), StatusText.CellLimits[^1]);
    }

    private static string Join(IEnumerable<(Column Column, string Text)> cells, int nameWidth) =>
        string.Join(new string(' ', Gap), Cells(cells, nameWidth)).TrimEnd();

    private static List<string> Cells(IEnumerable<(Column Column, string Text)> cells, int nameWidth) =>
        [.. cells.Select(x => Fit(x.Text, x.Column == _nameMarker ? nameWidth : x.Column.Width, x.Column.Right))];

    /// <summary>
    /// <paramref name="text"/> in exactly <paramref name="width"/> terminal columns: padded, or cut
    /// with a trailing <see cref="TerminalText.CutMark"/>. A wide character (CJK, a surrogate pair)
    /// counts two columns, so a cut never lets a line wrap.
    /// </summary>
    internal static string Fit(string text, int width, bool right)
    {
        int used = 0;
        StringBuilder sb = new();
        for (int i = 0; i < text.Length; i++)
        {
            bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            int w = pair || Wide(text[i]) ? 2 : 1;
            if (used + w > width || (used + w == width && Rest(text, i + (pair ? 2 : 1))))
            {
                return Pad(sb.Append(TerminalText.CutMark).ToString(), used + 1, width, right: false);
            }

            _ = pair ? sb.Append(text, i++, 2) : sb.Append(text[i]);
            used += w;
        }

        return Pad(sb.ToString(), used, width, right);
    }

    // Whether anything follows position i (so the last column must hold the cut mark instead).
    private static bool Rest(string text, int i) => i < text.Length;

    private static string Pad(string text, int used, int width, bool right)
    {
        string pad = new(' ', Math.Max(0, width - used));
        return right ? pad + text : text + pad;
    }

    // East Asian wide and fullwidth ranges: two columns each.
    private static bool Wide(char c)
    {
        int code = c;
        return code is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6);
    }

    /// <summary>
    /// The STATE colour: red for a failed end (orphaned, lost and gone included), yellow for
    /// wrap-up, ending and quiet runs, green for success; none otherwise.
    /// </summary>
    private static string Colour(StatusRow r)
    {
        if (r.Ended)
        {
            return StatusWait.Judge(r) switch
            {
                StatusWait.Verdict.Success => Green,
                StatusWait.Verdict.WrappedUp => Yellow,
                _ => Red,
            };
        }

        return r.Live && (r.State is StatusStates.WrapUp or StatusStates.Ending || r.Quiet is not null) ? Yellow : string.Empty;
    }

    private static JsonObject? Live(StatusRow r) => r.Status?["live"] as JsonObject;

    private static JsonObject? Spec(StatusRow r) => r.Status?["spec"] as JsonObject;

    private static string Runtime(StatusRow r)
    {
        return Json.Str(Spec(r), "runtime") switch
        {
            "claude" => "cc",
            "opencode" => "oc",
            { } other => other,
            null => "-",
        };
    }

    // Input (with cache reads and writes) over output tokens: 2.8M/60k.
    private static string Tokens(StatusRow r)
    {
        if (Live(r)?["tokens"] is not JsonObject t)
        {
            return "-";
        }

        long input = Clamp(Json.Num(t["input"])) + Clamp(Json.Num(t["cache_read"])) + Clamp(Json.Num(t["cache_write"]));
        return Compact(input) + "/" + Compact(Clamp(Json.Num(t["output"])));
    }

    // A count from a file, kept in a range no sum can overflow.
    private static long Clamp(long n) => Math.Clamp(n, 0, 1_000_000_000_000_000);

    private static string Compact(long n)
    {
        return n switch
        {
            < 1_000 => n.ToString(_ic),
            < 10_000 => (n / 1_000d).ToString("0.0", _ic) + "k",
            < 1_000_000 => (n / 1_000).ToString(_ic) + "k",
            < 10_000_000 => (n / 1_000_000d).ToString("0.0", _ic) + "M",
            _ => (n / 1_000_000).ToString(_ic) + "M",
        };
    }

    private static string Budget(StatusRow r)
    {
        decimal? cost = Json.Dec(Live(r), "cost_usd");
        decimal? budget = Json.Dec(Spec(r), "budget_usd");
        if (cost is not { } c || budget is not { } b || b <= 0 || c < 0 || c / b > 999)
        {
            return "-";
        }

        return decimal.Round(c / b * 100m, 0, MidpointRounding.AwayFromZero).ToString(_ic) + "%";
    }

    /// <summary>
    /// The LAST cell: a quiet run's quiet age with a mark, a gate round's age, the last tool and its
    /// age for a live or orphaned run, nothing for a final end, the detail for a row without a status file.
    /// </summary>
    private static string Last(StatusRow r, DateTimeOffset now)
    {
        if (r.Status is not { } doc)
        {
            return r.Detail;
        }

        if (r.Quiet is { } quiet)
        {
            return "quiet " + Short(quiet) + " !";
        }

        if (r.Live && Json.Str(doc, "phase") is RunPhase.Gate)
        {
            return "gate" + Age(StatusReader.Time(Json.Str(doc, "updated_at")), now);
        }

        if (r.Ended && !r.Attention)
        {
            return "-";
        }

        JsonObject? tool = Live(r)?["last_tool"] as JsonObject;
        return Json.Str(tool, "name") is { } name ? name.ToLowerInvariant() + Age(StatusReader.Time(Json.Str(tool, "at")), now) : "-";
    }

    private static string Age(DateTimeOffset? at, DateTimeOffset now) => at is { } t && now > t ? " " + Short(now - t) : string.Empty;

    /// <summary>
    /// An age as the table shows it: 4s, 1m12s, 41m, 2h05m.
    /// </summary>
    internal static string Short(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(10) || t >= TimeSpan.FromHours(1))
        {
            return StatusText.Duration(t);
        }

        return string.Create(_ic, $"{t.Minutes}m");
    }
}
