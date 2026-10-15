using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The pure pieces of one <c>gate.commands</c> round (<see cref="GateRound"/>): the aggregate result the
/// chain decides on, the feedback text the next worker gets, the per-command counts and the summary array.
/// </summary>
public class GateRoundTests
{
    private const string RunDir = "/runs/r1";

    private static readonly string _nl = Environment.NewLine;

    internal static List<GateSpec> Specs(int feedbackMaxChars, params string[] names)
    {
        string entries = string.Join(',', names.Select(n => "{\"name\":\"" + n + "\",\"command\":[\"sh\"]}"));
        JsonObject profile = new() { ["gate"] = JsonNode.Parse("{\"commands\":[" + entries + "],\"feedbackMaxChars\":" + feedbackMaxChars.ToString(CultureInfo.InvariantCulture) + "}") };
        return GateSpec.FromProfileAll(profile, Directory.GetCurrentDirectory())!;
    }

    private static List<GateSpec> Specs3() => Specs(8000, "lint", "arch", "test");

    internal static Gate.GateResult Findings(int count, string? report = null, string feedback = "", int seconds = 0) =>
        new("findings", 1, count, report, feedback, Error: null, TimeSpan.FromSeconds(seconds));

    internal static Gate.GateResult Clean(int? count = 0, int seconds = 0) =>
        new("clean", 0, count, ReportPath: null, Feedback: string.Empty, Error: null, TimeSpan.FromSeconds(seconds));

    private static Gate.GateResult Error(int exitCode, int? count, string error) =>
        new("error", exitCode, count, ReportPath: null, Feedback: string.Empty, error, TimeSpan.Zero);

    // The aggregate without its feedback text, for a record comparison of everything else.
    private static Gate.GateResult Aggregate(params Gate.GateResult[] results) => AggregateRound(preRoundViolation: false, results);

    private static Gate.GateResult AggregateRound(bool preRoundViolation, params Gate.GateResult[] results) =>
        GateRound.Aggregate(Specs3(), results, round: 1, RunDir, preRoundViolation) with { Feedback = string.Empty };

    private static string Instruction(int total, int round, string counts)
    {
        return string.Create(CultureInfo.InvariantCulture, $"The previous worker finished, but the gate commands still report {total} finding(s) (round {round}: {counts}). ") +
            "Fix them; do not redo finished work. Check the current state first (git status, git diff --stat).";
    }

    [Fact]
    public void A_single_command_spec_gets_its_one_result_back_unchanged()
    {
        GateSpec legacy = GateSpec.FromProfile(new JsonObject { ["gate"] = new JsonObject { ["command"] = new JsonArray(["sh"]) } }, Directory.GetCurrentDirectory())!;
        Gate.GateResult result = Findings(3, "/r.sarif", "out");

        Assert.False(GateRound.IsMulti([legacy]));
        Assert.True(GateRound.IsMulti(Specs3()));
        Assert.Same(result, GateRound.Aggregate([legacy], [result], round: 1, RunDir));
    }

    [Fact]
    public void All_clean_commands_aggregate_to_clean() =>
        Assert.Equal(Clean(0), Aggregate(Clean(), Clean(), Clean()));

    [Fact]
    public void All_null_counts_aggregate_to_a_null_count() =>
        Assert.Equal(Clean(count: null), Aggregate(Clean(count: null), Clean(count: null), Clean(count: null)));

    [Fact]
    public void Findings_and_clean_aggregate_to_findings_with_the_summed_count_and_duration()
    {
        Gate.GateResult aggregate = Aggregate(Findings(3, "/r/lint.sarif", seconds: 1), Clean(count: null, seconds: 2), Findings(2, "/r/test.sarif", seconds: 3));

        Assert.Equal(Findings(5, report: null, seconds: 6), aggregate);
    }

    [Fact]
    public void An_error_after_findings_aggregates_to_the_failing_commands_error_and_exit_code() =>
        Assert.Equal(Error(2, 3, "arch: gate exited 2"), Aggregate(Findings(3), Error(2, count: null, "gate exited 2")));

    [Fact]
    public void An_error_with_a_count_adds_it_to_the_sum()
    {
        Gate.GateResult afterRunViolation = Error(1, 1, "trusted files changed");

        Assert.Equal(Error(1, 4, "arch: trusted files changed"), Aggregate(Findings(3), afterRunViolation));
    }

    // Exit code -1 (no exit code) is recorded on many paths: a check before a later command, a start
    // failure, a timeout, a cancellation. Only the pre-round flag, not the -1, drops the command name.
    [Theory]
    [InlineData("trusted files changed between gate commands: /w/a.props")]
    [InlineData("a gate PATH directory changed between gate commands: /bin")]
    [InlineData("the gate could not be run: denied")]
    [InlineData("gate timed out after 1 minutes")]
    [InlineData("gate cancelled")]
    public void A_failure_without_an_exit_code_of_a_later_command_names_it(string error)
    {
        Assert.Equal(Error(-1, 3, "arch: " + error), AggregateRound(preRoundViolation: false, Findings(3), Error(-1, count: null, error)));
        Assert.Equal(Error(-1, 3, "arch: " + error), AggregateRound(preRoundViolation: true, Findings(3), Error(-1, count: null, error)));
    }

    [Fact]
    public void A_pre_round_violation_before_the_first_command_names_no_command()
    {
        const string Changed = "the worker changed files the gate trusts: /w/a.props";

        Assert.Equal(Error(-1, count: null, Changed), AggregateRound(preRoundViolation: true, Error(-1, count: null, Changed)));
    }

    [Fact]
    public void A_first_command_failure_that_is_not_pre_round_names_it()
    {
        const string StartFailure = "the gate could not be run: denied";

        Assert.Equal(Error(-1, count: null, "lint: " + StartFailure), AggregateRound(preRoundViolation: false, Error(-1, count: null, StartFailure)));
    }

    [Fact]
    public void The_feedback_has_the_count_line_and_one_section_per_command_with_findings_in_profile_order()
    {
        Gate.GateResult aggregate = GateRound.Aggregate(
            Specs3(), [Findings(3, "/r/lint.sarif", "lint stdout"), Clean(), Findings(2, report: null, "test stdout")], round: 2, RunDir);

        string expected =
            Instruction(5, 2, "lint 3, arch 0, test 2") + _nl + _nl +
            "### lint: 3 finding(s)" + _nl + _nl + "Full report: /r/lint.sarif" + _nl + _nl + "lint stdout" + _nl + _nl +
            "### test: 2 finding(s)" + _nl + _nl + "Full report: (no report)" + _nl + _nl + "test stdout";
        Assert.Equal(expected, aggregate.Feedback);
    }

    [Fact]
    public void Each_section_is_cut_to_its_share_of_the_budget_naming_its_own_log()
    {
        string feedback = GateRound.ComposeFeedback(
            Specs(20, "lint", "arch", "test"), [Findings(1, "/l", "0123456789abcdef"), Clean(), Findings(1, "/t", "short")], round: 1, RunDir, feedbackMaxChars: 20);

        Assert.Contains(_nl + "0123456789\n[truncated; full gate output: " + Path.Combine(RunDir, "gate-lint.log") + "]" + _nl, feedback, StringComparison.Ordinal);
        Assert.EndsWith(_nl + "short", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("gate-test.log", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void One_section_with_findings_gets_the_whole_budget()
    {
        string stdout = new('x', 20);

        string feedback = GateRound.ComposeFeedback(Specs(20, "lint", "arch"), [Clean(), Findings(1, "/a", stdout)], round: 1, RunDir, feedbackMaxChars: 20);

        Assert.EndsWith(_nl + stdout, feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_list_the_commands_that_ran_in_profile_order_with_null_as_zero()
    {
        Assert.Equal("lint 3, arch 0, test 2", GateRound.Counts(Specs3(), [Findings(3), Clean(count: null), Findings(2)]));
        Assert.Equal("lint 3, arch 0", GateRound.Counts(Specs3(), [Findings(3), Error(2, count: null, "gate exited 2")]));
    }

    [Fact]
    public void The_commands_array_has_one_object_per_command_that_ran()
    {
        JsonArray commands = GateRound.BuildCommandsJson(Specs3(), [Findings(3, "/r/lint.sarif", seconds: 2), Error(2, count: null, "gate exited 2")]);

        Assert.Equal(2, commands.Count);
        Assert.Equal(["name", "outcome", "exit_code", "count", "report_path", "error", "duration_ms"], GateCommandScripts.Keys(commands[0]!.AsObject()), StringComparer.Ordinal);
        GateCommandScripts.AssertFields(commands[0]!.AsObject(), /*lang=json,strict*/ """{"name":"lint","outcome":"findings","exit_code":1,"count":3,"report_path":"/r/lint.sarif","error":null,"duration_ms":2000}""");
        GateCommandScripts.AssertFields(commands[1]!.AsObject(), /*lang=json,strict*/ """{"name":"arch","outcome":"error","exit_code":2,"count":null,"report_path":null,"error":"gate exited 2","duration_ms":0}""");
    }

    [Fact]
    public void A_commands_spec_names_its_own_files() =>
        Assert.Equal(["gate-lint.log", "gate-lint.json"], new[] { Gate.FileName(Specs3()[0], "log"), Gate.FileName(Specs3()[0], "json") }, StringComparer.Ordinal);
}
