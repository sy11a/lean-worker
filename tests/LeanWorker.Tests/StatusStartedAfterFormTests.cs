using System.Globalization;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The form of a --started-after time (the review of #147, finding 5): a date and time with Z or an offset, what
/// `date -u +%FT%TZ` prints. A time without one is a usage error (exit 2) whose message says how to write it; the
/// message stays clean when the value is hostile; nothing is read or waited on after the refusal.
/// </summary>
public sealed class StatusStartedAfterFormTests : IDisposable
{
    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Theory]
    [InlineData("2026-11-22T13:02:31Z", 13, 2, 31, 0)]
    [InlineData("2026-11-22T14:02:31+01:00", 13, 2, 31, 0)]
    [InlineData("2026-11-22T05:02:31-08:00", 13, 2, 31, 0)]
    [InlineData("2026-11-22T18:32:31+05:30", 13, 2, 31, 0)]
    [InlineData("2026-11-22T13:02:31.250Z", 13, 2, 31, 250)]
    public void A_time_with_Z_or_an_offset_means_the_same_instant_in_every_mode(string value, int hour, int minute, int second, int millisecond)
    {
        DateTimeOffset expected = new(2026, 11, 22, hour, minute, second, millisecond, TimeSpan.Zero);

        foreach (string mode in new[] { "--here", "--json", "--wait", "--watch" })
        {
            string[] args = mode is "--here" ? ["--here", "--started-after", value] : ["--here", mode, "--started-after", value];
            DateTimeOffset? parsed = StatusOptions.Parse(args).StartedAfter;
            Assert.Equal(expected, parsed);
            Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
        }
    }

    [Fact]
    public void The_time_that_date_u_prints_is_one_the_parser_takes()
    {
        // `date -u +%FT%TZ` prints the date, a T, the time and a Z.
        DateTimeOffset instant = new(2026, 11, 23, 9, 5, 7, TimeSpan.Zero);
        string printed = instant.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        Assert.Equal("2026-11-23T09:05:07Z", printed);
        Assert.Equal(instant, StatusOptions.Parse(["--here", "--wait", "--started-after", printed]).StartedAfter);
    }

    // No Z and no offset (read as UTC before, hours off west or east of it), a date or a time alone, a bare zone, a count
    // of seconds, a duration, a word and nothing.
    [Theory]
    [InlineData("2026-11-22T13:02:31")]
    [InlineData("2026-11-22T13:02:31.5")]
    [InlineData("2026-11-22T13:02")]
    [InlineData("2026-11-22 13:02:31")]
    [InlineData("2026-11-22")]
    [InlineData("2026-11-22Z")]
    [InlineData("13:02:31Z")]
    [InlineData("13:02:31")]
    [InlineData("Z")]
    [InlineData("+01:00")]
    [InlineData("1700000000")]
    [InlineData("30m")]
    [InlineData("yesterday")]
    [InlineData("")]
    public void A_time_without_Z_or_an_offset_and_what_is_no_time_is_a_usage_error_and_the_message_says_how_to_write_one(string value)
    {
        string[][] calls = [["--here", "--started-after", value], ["--here", "--wait", "--started-after", value], ["--here", "--watch", "--started-after", value], ["--here", "--json", "--started-after", value]];

        foreach (string[] args in calls)
        {
            LaunchException ex = Assert.Throws<LaunchException>(() => StatusOptions.Parse(args));
            Assert.Contains("--started-after needs a date and time with Z or an offset", ex.Message, StringComparison.Ordinal);
            Assert.Contains("2026-11-22T13:02:31Z", ex.Message, StringComparison.Ordinal);
            Assert.Contains("date -u +%FT%TZ", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--wait")]
    [InlineData("--json")]
    [InlineData("--watch")]
    [InlineData("--here")]
    public async Task The_command_ends_with_exit_2_and_one_clean_line_for_a_hostile_valueAsync(string mode)
    {
        string[] args = mode is "--here" ? ["--here", "--started-after", StatusHostileText.Value] : ["--here", mode, "--started-after", StatusHostileText.Value];

        (int code, string output) = await _rig.CallAsync(args);

        Assert.Equal(2, code);
        string line = Assert.Single(StatusIndexRig.Lines(output));
        Assert.StartsWith("lean-worker status: --started-after needs a date and time with Z or an offset", line, StringComparison.Ordinal);
        Assert.Contains("date -u +%FT%TZ", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\a', output);
        Assert.Empty(TerminalAssert.PlainProblems(output));
    }

    [Fact]
    public async Task A_hostile_value_that_passes_for_a_time_at_a_glance_is_refused_and_echoed_cleanAsync()
    {
        string value = "2026-11-22T13:02:31" + StatusHostileText.Number;

        (int code, string output) = await _rig.CallAsync("--here", "--wait", "--started-after", value);

        Assert.Equal(2, code);
        _ = Assert.Single(StatusIndexRig.Lines(output));
        Assert.DoesNotContain("SECRET\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', output);
        Assert.Empty(TerminalAssert.PlainProblems(output));
    }
}
