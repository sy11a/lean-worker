using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// ProcStat.Parse: the fields of a /proc/&lt;pid&gt;/stat line the live status needs, counted after the
/// LAST ')' because the command name may hold spaces and parentheses.
/// </summary>
public class ProcStatTests
{
    // Fields 3 to 22 of a stat line are: state, then 18 numbers; the 13 zeros stand for fields 9 to 21.
    private const string Zeros = "0 0 0 0 0 0 0 0 0 0 0 0 0";

    [Fact]
    public void A_plain_line_gives_state_group_session_and_start_ticks()
    {
        ProcStat? stat = ProcStat.Parse(FakeProcRoot.Line(41240, "claude", 'S', pgrp: 41240, session: 41230, startTicks: 7432151));

        Assert.Equal(new ProcStat('S', 41240, 41230, 7432151), stat);
        Assert.False(stat!.Value.Exited);
    }

    [Fact]
    public void A_command_name_with_spaces_and_parentheses_is_skipped_as_a_whole()
    {
        ProcStat? stat = ProcStat.Parse(FakeProcRoot.Line(12, "a b) (c d) e", 'R', pgrp: 20, session: 30, startTicks: 400));

        Assert.Equal(new ProcStat('R', 20, 30, 400), stat);
    }

    [Fact]
    public void A_command_name_that_looks_like_stat_fields_does_not_fool_the_parser()
    {
        // Counting from the first ')' would read "R 9 9 9 ..." out of the name.
        string line = FakeProcRoot.Line(12, "evil) R 9 9 9 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 9999 (x", 'S', pgrp: 20, session: 30, startTicks: 400);

        Assert.Equal(new ProcStat('S', 20, 30, 400), ProcStat.Parse(line));
    }

    [Theory]
    [InlineData('Z', true)]
    [InlineData('X', true)]
    [InlineData('S', false)]
    [InlineData('R', false)]
    [InlineData('D', false)]
    public void Zombie_and_dead_entries_are_exited_and_nothing_else_is(char state, bool exited)
    {
        ProcStat? stat = ProcStat.Parse(FakeProcRoot.Line(5, "w", state, 1, 1, 10));

        Assert.Equal(exited, stat!.Value.Exited);
    }

    [Fact]
    public void A_line_with_exactly_the_fields_up_to_the_start_time_parses() =>
        Assert.Equal(new ProcStat('S', 3, 4, 19), ProcStat.Parse("1 (x) S 1 3 4 0 -1 " + Zeros + " 19"));

    [Fact]
    public void A_line_with_one_field_too_few_is_null() =>
        Assert.Null(ProcStat.Parse("1 (x) S 1 3 4 0 -1 " + Zeros));

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1 (x")]
    [InlineData("1 (x)")]
    [InlineData("1 (x) S 1 notanumber 4 0 -1 " + Zeros + " 19")]
    [InlineData("1 (x) S 1 3 notanumber 0 -1 " + Zeros + " 19")]
    [InlineData("1 (x) S 1 3 4 0 -1 " + Zeros + " -19")]
    [InlineData("1 (x) S 1 3 4 0 -1 " + Zeros + " nineteen")]
    [InlineData("1 (x) SS 1 3 4 0 -1 " + Zeros + " 19")]
    public void Garbage_is_null(string line) => Assert.Null(ProcStat.Parse(line));
}
