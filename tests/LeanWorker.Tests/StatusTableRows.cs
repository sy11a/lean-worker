using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace LeanWorker.Tests;

/// <summary>
/// Hand-made rows and frames for the tests of the watch table: a running row and an ended row built from
/// StatusDocs, and the helpers that read a frame back (its lines without escapes, their display width).
/// </summary>
internal static partial class StatusTableRows
{
    public const string Esc = "\u001b";
    public const string Scope = "owner cc:0f3e9a12 (example-track-1a)";

    /// <summary>
    /// A live run in its worker phase: 12 calls, $0.41 of $3.00 (14%), 3m41s, 402k/9.9k tokens, model MiniMax-M3 on
    /// the opencode runtime (oc), last tool Bash 4 seconds ago.
    /// </summary>
    public static StatusRow Running(string name = "fix-save-w1", Action<JsonObject>? tweak = null, TimeSpan? quiet = null)
    {
        JsonObject doc = StatusDocs.Make(name, worker: StatusDocs.Worker(), lastTool: StatusDocs.Tool("Bash", "done", StatusDocs.Now - TimeSpan.FromSeconds(4)));
        tweak?.Invoke(doc);
        return new StatusRow("/runs/" + name, name, StatusStates.Running, Liveness.Alive, doc, "last Bash 4s ago", StatusDocs.Now - TimeSpan.FromMinutes(5),
            EndedAt: null, Attention: quiet is not null, quiet);
    }

    /// <summary>
    /// A run that ended one minute ago in <paramref name="state"/> (a final state, orphaned or lost).
    /// </summary>
    public static StatusRow Ended(string name, string state = "success", Action<JsonObject>? tweak = null)
    {
        JsonObject doc = StatusDocs.Make(name, phase: "done", launcherPid: 900, end: StatusDocs.End(state, StatusDocs.Now - TimeSpan.FromMinutes(1)));
        tweak?.Invoke(doc);
        return new StatusRow("/runs/" + name, name, state, Liveness.Gone, doc, "/runs/" + name, StatusDocs.Now - TimeSpan.FromMinutes(5),
            StatusDocs.Now - TimeSpan.FromMinutes(1), Attention: StatusReader.IsFailure(state), Quiet: null);
    }

    public static StatusTable.FrameData Data(IEnumerable<StatusRow> rows, bool color = false, bool owner = false, params StatusRow[] unacknowledged) =>
        new(Scope, [.. rows], new HashSet<StatusRow>(unacknowledged, ReferenceEqualityComparer.Instance), color, owner);

    public static List<string> Frame(StatusTable.FrameData data, int width, int height = 40) => StatusTable.Frame(data, StatusDocs.Now, width, height);

    /// <summary>
    /// A frame line without its colour and bold escapes.
    /// </summary>
    public static string Plain(string line) => Sgr().Replace(line, string.Empty);

    /// <summary>
    /// How many terminal columns a line takes: one per character, two for a wide character (the CJK ranges) and for a surrogate pair.
    /// </summary>
    public static int Columns(string line)
    {
        string text = Plain(line);
        int columns = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                columns += 2;
            }
            else
            {
                columns += IsWide(text[i]) ? 2 : 1;
            }
        }

        return columns;
    }

    /// <summary>
    /// The column titles of a frame, in order.
    /// </summary>
    public static string[] Titles(List<string> frame) => Blanks().Split(Plain(frame[1]).Trim());

    private static bool IsWide(char c)
    {
        int code = c;
        return code is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6);
    }

    [GeneratedRegex("\u001b\\[[0-9;]*m", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Sgr();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Blanks();
}
