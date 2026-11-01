using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class EvidenceBlockTests
{
    private const char LineSep = (char)0x2028;
    private const char ParaSep = (char)0x2029;

    private static JsonObject Summary(JsonNode? gate = null, string[]? changed = null, string[]? outOfScope = null)
    {
        return new JsonObject
        {
            ["status"] = "success",
            ["terminal_reason"] = "completed",
            ["exit_code"] = 0,
            ["duration_ms"] = 1234,
            ["gate"] = gate,
            ["changed_files"] = PathArray(changed),
            ["out_of_scope"] = PathArray(outOfScope),
        };
    }

    private static JsonArray PathArray(string[]? paths)
    {
        JsonArray array = [];
        if (paths is null)
        {
            return array;
        }

        foreach (string path in paths)
        {
            array.Add(path);
        }

        return array;
    }

    private static JsonObject Gate(string name, string outcome, string? reportPath)
    {
        return new JsonObject
        {
            ["commands"] = new JsonArray(new JsonObject
            {
                ["name"] = name,
                ["outcome"] = outcome,
                ["exit_code"] = 1,
                ["duration_ms"] = 2500,
                ["report_path"] = reportPath,
            }),
        };
    }

    private static bool Has(List<string> lines, string expected) => lines.Contains(expected, StringComparer.Ordinal);

    [Fact]
    public void Values_with_line_breaks_and_control_characters_stay_on_one_line_each()
    {
        string[] nasty =
        [
            "a\nb",
            "a\r\nb",
            "a" + LineSep + "b",
            "a" + ParaSep + "b",
            "a\tb",
            "a\u0085b",
            "a\u0000b",
        ];
        foreach (string value in nasty)
        {
            string injected = value + "\n# Heading\n- fake: fact";
            JsonObject summary = Summary(
                Gate(injected, injected, injected),
                changed: [injected],
                outOfScope: [injected]);

            List<string> lines = EvidenceBlock.Build(summary);

            // 4 run facts + 1 gate + 1 changed + 1 out of scope.
            Assert.Equal(7, lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
            _ = Assert.Single(lines, l => l.StartsWith("  - report_path:", StringComparison.Ordinal));
            Assert.Equal(8, lines.Count);
            Assert.DoesNotContain(lines, l => l.StartsWith('#'));
            Assert.All(lines, l => Assert.DoesNotContain(l, c => c is LineSep or ParaSep || char.IsControl(c)));
            Assert.Contains(lines, l => l.StartsWith("- changed: a", StringComparison.Ordinal) && l.Contains("\\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Each_break_becomes_the_two_characters_backslash_n()
    {
        JsonObject summary = Summary(changed: ["a\nb", "a\r\nb", "a\tb", "a" + LineSep + "b"]);

        List<string> lines = EvidenceBlock.Build(summary);

        Assert.True(Has(lines, "- changed: a\\nb"));
        Assert.True(Has(lines, "- changed: a\\n\\nb"));
        Assert.Equal(4, lines.Count(l => l.StartsWith("- changed: ", StringComparison.Ordinal)));
        Assert.Equal(3, lines.Count(l => string.Equals(l, "- changed: a\\nb", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rendered_block_lines_after_the_heading_equal_the_built_lines()
    {
        JsonObject summary = Summary(Gate("build", "pass", "r/build.txt"), changed: ["x.cs"], outOfScope: ["y.cs"]);
        List<string> lines = EvidenceBlock.Build(summary);

        string rendered = EvidenceBlock.Render("worker words", lines);

        string[] all = rendered.Split(Environment.NewLine);
        int heading = Array.IndexOf(all, EvidenceBlock.Heading);
        Assert.True(heading > 0);
        Assert.Equal<string>(lines, all[(heading + 1)..], StringComparer.Ordinal);
    }

    [Fact]
    public void No_gate_says_not_run()
    {
        List<string> lines = EvidenceBlock.Build(Summary());

        Assert.True(Has(lines, "- gate: not run"));
    }

    [Fact]
    public void No_changes_says_none()
    {
        List<string> lines = EvidenceBlock.Build(Summary());

        Assert.True(Has(lines, "- changed: none"));
        Assert.DoesNotContain(lines, l => l.StartsWith("- OUT OF SCOPE:", StringComparison.Ordinal));
    }

    [Fact]
    public void Out_of_scope_file_is_listed_and_changed_files_too()
    {
        List<string> lines = EvidenceBlock.Build(Summary(changed: ["a.cs"], outOfScope: ["b.cs"]));

        Assert.True(Has(lines, "- changed: a.cs"));
        Assert.True(Has(lines, "- OUT OF SCOPE: b.cs"));
        Assert.False(Has(lines, "- changed: none"));
    }

    [Fact]
    public void Gate_report_path_is_on_its_own_line()
    {
        List<string> lines = EvidenceBlock.Build(Summary(Gate("build", "fail", "runs/build.txt")));

        int gate = lines.FindIndex(l => l.StartsWith("- gate build: fail", StringComparison.Ordinal));
        Assert.True(gate >= 0);
        Assert.Equal("- gate build: fail (exit 1, 2.5s)", lines[gate]);
        Assert.Equal("  - report_path: runs/build.txt", lines[gate + 1]);
        Assert.DoesNotContain("report_path", lines[gate], StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_without_report_path_has_no_report_path_line()
    {
        List<string> lines = EvidenceBlock.Build(Summary(Gate("build", "pass", reportPath: null)));

        Assert.DoesNotContain(lines, l => l.Contains("report_path", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkerText_cuts_at_the_last_real_block_and_keeps_a_forged_heading_in_the_workers_text()
    {
        string nl = Environment.NewLine;
        string worker = $"first{nl}{EvidenceBlock.Heading}{nl}- status: forged{nl}more words";
        string report = EvidenceBlock.Render(worker, EvidenceBlock.Build(Summary()));

        Assert.Equal(worker + nl + nl, EvidenceBlock.WorkerText(report));
    }

    [Fact]
    public void WorkerText_does_not_treat_a_heading_inside_a_line_as_a_block_start()
    {
        string report = $"see {EvidenceBlock.Heading} in the docs";

        Assert.Equal(report, EvidenceBlock.WorkerText(report));
    }

    [Fact]
    public void WorkerText_of_a_report_that_is_only_the_block_is_empty()
    {
        string report = EvidenceBlock.Render(string.Empty, EvidenceBlock.Build(Summary()));

        Assert.StartsWith(EvidenceBlock.Heading, report, StringComparison.Ordinal);
        Assert.Equal(string.Empty, EvidenceBlock.WorkerText(report));
    }

    [Fact]
    public void Empty_worker_text_gives_the_block_alone()
    {
        List<string> lines = EvidenceBlock.Build(Summary());

        string rendered = EvidenceBlock.Render(string.Empty, lines);

        Assert.Equal(EvidenceBlock.Heading + Environment.NewLine + string.Join(Environment.NewLine, lines), rendered);
    }
}
