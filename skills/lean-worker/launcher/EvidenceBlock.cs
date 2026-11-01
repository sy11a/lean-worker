// The evidence block: the launcher's own record of what the run did, written next to the worker's
// report so a reader sees the facts and not only the worker's prose. The lines are built from the
// summary the launcher fills, so they cannot drift from summary.json.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// The evidence block the launcher appends to the run report: the run's status, exit code, terminal
/// reason and duration, the gate's results and the files the run changed, as plain Markdown lines.
/// The lines are built from the summary object the launcher writes to <c>summary.json</c> — the same
/// values, never recomputed — and the worker's own text is left untouched: the block is added after
/// it, separated by a blank line, and never truncated.
/// </summary>
internal static class EvidenceBlock
{
    /// <summary>
    /// The heading the block starts with in the report file.
    /// </summary>
    public const string Heading = "## Evidence (written by the launcher)";

    /// <summary>
    /// The evidence lines of one run, in the order they appear in the block: the run's status, exit
    /// code, terminal reason and duration; one line per gate command (its outcome, exit code and
    /// duration) with its report path on a line of its own when it has one, or a single "not run"
    /// line when no gate ran; then one line per changed file and one line per file outside the write
    /// scope. Every value is put on one line, so nothing in it can start a line of its own.
    /// </summary>
    public static List<string> Build(JsonObject summary)
    {
        string status = OneLine(Json.Str(summary, "status") ?? "(none)");
        string terminalReason = OneLine(Json.Str(summary, "terminal_reason") ?? "(none)");
        string exitCode = Json.Num(summary["exit_code"]).ToString(CultureInfo.InvariantCulture);
        string durationMs = Json.Num(summary["duration_ms"]).ToString(CultureInfo.InvariantCulture);

        List<string> lines =
        [
            $"- status: {status}",
            $"- exit_code: {exitCode}",
            $"- terminal_reason: {terminalReason}",
            $"- duration_ms: {durationMs}",
        ];
        lines.AddRange(GateLines(summary["gate"] as JsonObject));
        lines.AddRange(ChangedLines(summary));
        return lines;
    }

    /// <summary>
    /// The worker's own text of a report file: everything before the launcher's evidence block. A
    /// handoff to the next worker carries the previous worker's report, not the launcher's evidence
    /// about it. The cut is at the last heading that starts a line (the launcher's own trailing
    /// block), so a worker text that mentions the heading inside a line keeps all of its own words.
    /// </summary>
    public static string WorkerText(string report)
    {
        int cut = LastBlockStart(report);
        return cut < 0 ? report : report[..cut];
    }

    /// <summary>
    /// Where the launcher's own evidence block starts: the last occurrence of the heading that begins
    /// a line — at the very start of the text or right after a newline. The launcher appends its
    /// block last, so that is the one occurrence that is cut away.
    /// </summary>
    private static int LastBlockStart(string report)
    {
        int cut = -1;
        int index = report.IndexOf(Heading, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index is 0 || report[index - 1] is '\n')
            {
                cut = index;
            }

            index = report.IndexOf(Heading, index + 1, StringComparison.Ordinal);
        }

        return cut;
    }

    /// <summary>
    /// The report file's content: the worker's own text, unchanged, then a blank line and the
    /// evidence block. The text is never edited or cut — the block only follows it.
    /// </summary>
    public static string Render(string report, IReadOnlyList<string> lines)
    {
        string nl = Environment.NewLine;
        StringBuilder sb = new(report);
        if (report.Length > 0)
        {
            if (!report.EndsWith('\n'))
            {
                sb.Append(nl);
            }

            sb.Append(nl);
        }

        sb.Append(Heading);
        foreach (string line in lines)
        {
            sb.Append(nl);
            sb.Append(line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The gate lines: one per command that ran, in profile order, or the single "not run" line
    /// when the run had no gate. The commands form lists its own commands; the single-command
    /// (legacy) form has no per-command array, and its one aggregate result is the one command.
    /// </summary>
    private static List<string> GateLines(JsonObject? gate)
    {
        if (gate is null)
        {
            return ["- gate: not run"];
        }

        List<string> lines = [];
        if (gate["commands"] is JsonArray commands)
        {
            int index = 0;
            foreach (JsonNode? command in commands)
            {
                if (command is JsonObject entry)
                {
                    lines.AddRange(GateCommandLines(entry, index));
                }

                index++;
            }
        }

        return lines.Count > 0 ? lines : GateCommandLines(gate, 0);
    }

    /// <summary>
    /// One gate command's lines: its name (or its index when it has none), its outcome, its exit code
    /// and its duration in seconds; then its report path on its own indented line when the gate wrote
    /// one.
    /// </summary>
    private static List<string> GateCommandLines(JsonObject command, int index)
    {
        string name = Json.Str(command, "name") ?? string.Empty;
        string label = OneLine(name.Length > 0 ? name : index.ToString(CultureInfo.InvariantCulture));
        string outcome = OneLine(Json.Str(command, "outcome") ?? "(none)");
        string exitCode = Json.Num(command["exit_code"]).ToString(CultureInfo.InvariantCulture);
        string duration = Seconds(Json.Num(command["duration_ms"]));

        List<string> lines = [$"- gate {label}: {outcome} (exit {exitCode}, {duration}s)"];
        string? reportPath = Json.Str(command, "report_path");
        if (reportPath is { Length: > 0 })
        {
            lines.Add($"  - report_path: {OneLine(reportPath)}");
        }

        return lines;
    }

    /// <summary>
    /// The changed-file lines: one per file the run changed, then one per file outside the write
    /// scope. A run that changed nothing says so.
    /// </summary>
    private static List<string> ChangedLines(JsonObject summary)
    {
        List<string> changed = Json.StrList(summary, "changed_files") ?? [];
        List<string> outOfScope = Json.StrList(summary, "out_of_scope") ?? [];

        List<string> lines = [];
        if (changed.Count is 0)
        {
            lines.Add("- changed: none");
        }
        else
        {
            lines.AddRange(changed.Select(path => $"- changed: {OneLine(path)}"));
        }

        lines.AddRange(outOfScope.Select(path => $"- OUT OF SCOPE: {OneLine(path)}"));
        return lines;
    }

    /// <summary>
    /// A value on one line: every character that could end the line — a carriage return, a line feed,
    /// any other control character, and the line and paragraph separators — becomes the two characters
    /// <c>\n</c>. A path from <c>git status -z</c> or a message from a gate can carry a newline, and
    /// without this it would start a line of its own in the block.
    /// </summary>
    private static string OneLine(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            if (c is '\r' or '\n' || char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                sb.Append("\\n");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A duration in whole milliseconds as seconds, at most three decimals.
    /// </summary>
    private static string Seconds(long durationMs)
    {
        double seconds = Math.Round(durationMs / 1000d, 3, MidpointRounding.ToEven);
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
