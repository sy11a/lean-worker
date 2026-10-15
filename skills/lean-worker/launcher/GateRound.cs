// The multi-command gate round's pure pieces: aggregating the per-command results into the one
// GateResult the chain decides on, composing the feedback text the next worker gets, formatting the
// per-command breakdown for the result line, and building the summary's "commands" array. None of it
// touches the filesystem or a process, and the single-command (legacy) gate never reaches it: a spec
// list whose first Name is null is answered with the one result unchanged, so the legacy form's
// records, lines and texts are produced by exactly the code that produced them before commands
// existed.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// The aggregation and formatting of one gate round over N commands. Pure: everything here is a
/// function of the specs (profile order), the results of the commands that ran and the run
/// directory. Commands that did not run (the round ended at their predecessor's error) are simply
/// absent from the results, and everything here follows the results list.
/// </summary>
internal static class GateRound
{
    /// <summary>
    /// Whether the spec list is the <c>gate.commands</c> form: at least one spec and a name on the
    /// first. The single-command form resolves to exactly one spec with a null name.
    /// </summary>
    public static bool IsMulti(IReadOnlyList<GateSpec> specs) => specs is { Count: > 0 } && specs[0].Name is not null;

    /// <summary>
    /// The one result the chain decision feeds on. The legacy form returns <paramref name="results"/>
    /// element 0 itself; the commands form builds it: outcome "error" when any command errored, else
    /// "findings" when any had findings, else "clean"; the failing command's exit code (else 1 for
    /// findings, 0 for clean); the sum of the non-null counts (null when every count is null); the
    /// error as "name: message" of the first errored command; no report path; the summed duration; and
    /// the composed feedback text. When the round's only failure is a pre-round trust violation
    /// against the first command (no command ran, the worker changed a file the gate trusts),
    /// <paramref name="preRoundViolation"/> is true and <paramref name="results"/> is exactly the one
    /// error result; its message is then used unprefixed so the chain names the worker, not command 1,
    /// for the change.
    /// </summary>
    public static Gate.GateResult Aggregate(IReadOnlyList<GateSpec> specs, IReadOnlyList<Gate.GateResult> results, int round, string runDir, bool preRoundViolation = false)
    {
        if (!IsMulti(specs))
        {
            return results[0];
        }

        int failed = FailedIndex(results);
        bool findings = results.Any(r => r.Outcome is "findings");
        int? count = null;
        foreach (Gate.GateResult r in results)
        {
            if (r.Count is { } c)
            {
                count = (count ?? 0) + c;
            }
        }

        string outcome = "clean";
        int exitCode = 0;
        string? error = null;
        if (failed >= 0)
        {
            outcome = "error";
            exitCode = results[failed].ExitCode;
            error = preRoundViolation && failed is 0
                ? results[failed].Error
                : $"{specs[failed].Name}: {results[failed].Error}";
        }
        else if (findings)
        {
            outcome = "findings";
            exitCode = 1;
        }

        TimeSpan duration = results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
        string feedback = ComposeFeedback(specs, results, round, runDir, specs[0].FeedbackMaxChars);
        return new Gate.GateResult(outcome, exitCode, count, ReportPath: null, feedback, error, duration);
    }

    /// <summary>
    /// The whole text that follows the gate heading in the next round's task: the instruction line
    /// with the total and the per-command counts of this round, then one section per command that
    /// had findings, in profile order, each with its report path and captured stdout. Clean commands
    /// appear only in the count list. Each section is cut to <paramref name="feedbackMaxChars"/>
    /// divided by the number of sections (each command's spec let the gate capture up to the full
    /// total), so one noisy tool cannot crowd out the others and clean tools use none of the budget.
    /// </summary>
    public static string ComposeFeedback(IReadOnlyList<GateSpec> specs, IReadOnlyList<Gate.GateResult> results, int round, string runDir, int feedbackMaxChars)
    {
        string nl = Environment.NewLine;
        string instruction =
            $"The previous worker finished, but the gate commands still report {Total(results).ToString(CultureInfo.InvariantCulture)} finding(s) (round {round.ToString(CultureInfo.InvariantCulture)}: {Counts(specs, results)}). Fix them; do not redo finished work. Check the current state first (git status, git diff --stat).";
        List<string> sections = [];
        int withFindings = results.Count(r => r.Outcome is "findings");
        if (withFindings is 0)
        {
            return instruction;
        }

        int budget = Math.Max(1, feedbackMaxChars / withFindings);
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Outcome is not "findings")
            {
                continue;
            }

            string? reportPath = results[i].ReportPath;
            string report = reportPath is { Length: > 0 } ? reportPath : "(no report)";
            string stdout = Cut(results[i].Feedback, budget, Path.Combine(runDir, Gate.FileName(specs[i], "log")));
            sections.Add($"### {specs[i].Name}: {(results[i].Count ?? 0).ToString(CultureInfo.InvariantCulture)} finding(s){nl}{nl}Full report: {report}{nl}{nl}{stdout}");
        }

        return instruction + nl + nl + string.Join(nl + nl, sections);
    }

    /// <summary>
    /// The per-command counts for the result line, e.g. <c>lint 3, arch 0, test 2</c>: profile
    /// order, the commands that ran, each with its count (0 for a null count).
    /// </summary>
    public static string Counts(IReadOnlyList<GateSpec> specs, IReadOnlyList<Gate.GateResult> results)
    {
        return string.Join(", ", Enumerable.Range(0, results.Count)
            .Select(i => $"{specs[i].Name} {(results[i].Count ?? 0).ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>
    /// The summary's <c>gate.commands</c> array: one object per command that ran (name, outcome,
    /// exit code, count, report path, error, duration), in profile order.
    /// </summary>
    public static JsonArray BuildCommandsJson(IReadOnlyList<GateSpec> specs, IReadOnlyList<Gate.GateResult> results)
    {
        JsonArray commands = [];
        for (int i = 0; i < results.Count; i++)
        {
            commands.Add(Gate.ResultFields(results[i], name: specs[i].Name));
        }

        return commands;
    }

    private static int Total(IReadOnlyList<Gate.GateResult> results) => results.Sum(r => r.Count ?? 0);

    /// <summary>
    /// The index of the first errored result, or -1 when no command errored. Used to build both
    /// the aggregate's error text (<c>{name}: {error}</c>) and its exit code from a single search.
    /// </summary>
    private static int FailedIndex(IReadOnlyList<Gate.GateResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Outcome is "error")
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The same cut Gate.JoinStdout applies — cap the text and point at the full log — so a section
    /// names its own command's gate-&lt;name&gt;.log, not the generic gate.log.
    /// </summary>
    private static string Cut(string text, int max, string logPath)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..max] + '\n' + $"[truncated; full gate output: {logPath}]";
    }
}
