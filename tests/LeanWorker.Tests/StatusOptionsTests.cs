using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The options of `status --wait` and `status --watch` (docs/research/live-status/README.md, section 4.2): each new
/// flag, its default and bounds, the time format of --started-after, and every combination the design forbids as a
/// usage error with exit 2 and a cleaned one-line message.
/// </summary>
public sealed class StatusOptionsTests
{
    private const string UsageStart = "lean-worker status: ";

    private static string[] Words(string args) => args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        using FakeProcRoot proc = new();
        await using StringWriter output = new();
        StatusReader reader = new(proc.Fs, new FakeClock(StatusDocs.Now), FakeProcRoot.DefaultBoot);
        int code = await StatusCommand.RunAsync(args, output, reader, Path.GetTempPath());
        return (code, output.ToString());
    }

    private static string Refusal(string[] args)
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => StatusOptions.Parse(args));
        return ex.Message;
    }

    [Fact]
    public void Without_options_a_wait_has_no_interval_timeout_grace_expect_or_start_time_of_its_own()
    {
        StatusCommand.StatusArgs a = StatusOptions.Parse(["--here", "--wait"]);

        Assert.True(a.Wait);
        Assert.False(a.Watch);
        Assert.False(a.Any);
        Assert.False(a.Bell);
        Assert.Null(a.Interval);
        Assert.Null(a.Timeout);
        Assert.Null(a.Grace);
        Assert.Null(a.Expect);
        Assert.Null(a.StartedAfter);
    }

    [Fact]
    public void The_defaults_are_an_interval_of_2_seconds_a_timeout_of_120_minutes_and_a_grace_of_10_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), StatusWait.DefaultInterval);
        Assert.Equal(TimeSpan.FromMinutes(120), StatusWait.DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), StatusWait.DefaultGrace);
    }

    [Fact]
    public void Every_flag_of_a_wait_is_read()
    {
        string[] args =
        [
            "--owner", "k:1", "--wait", "--any", "--interval", "5", "--timeout", "90m", "--grace", "30s",
            "--expect", "3", "--started-after", "2026-11-22T13:02:31Z",
        ];

        StatusCommand.StatusArgs a = StatusOptions.Parse(args);

        Assert.True(a.Wait);
        Assert.True(a.Any);
        Assert.Equal(5, a.Interval);
        Assert.Equal(TimeSpan.FromMinutes(90), a.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), a.Grace);
        Assert.Equal(3, a.Expect);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 13, 2, 31, TimeSpan.Zero), a.StartedAfter);
    }

    [Fact]
    public void Every_flag_of_a_watch_is_read()
    {
        StatusCommand.StatusArgs a = StatusOptions.Parse(["--all", "--watch", "--bell", "--interval", "7", "--started-after", "2026-11-22T13:02:31Z"]);

        Assert.True(a.Watch);
        Assert.True(a.Bell);
        Assert.Equal(7, a.Interval);
        Assert.False(a.Wait);
        Assert.True(a.StartedAfter is not null);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3600", 3600)]
    public void An_interval_from_1_to_3600_seconds_is_taken(string value, int seconds)
    {
        Assert.Equal(seconds, StatusOptions.Parse(["--here", "--wait", "--interval", value]).Interval);
        Assert.Equal(seconds, StatusOptions.Parse(["--here", "--watch", "--interval", value]).Interval);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("5s")]
    [InlineData("abc")]
    [InlineData("")]
    public void An_interval_outside_1_to_3600_or_not_a_whole_number_is_refused(string value)
    {
        string message = Refusal(["--here", "--wait", "--interval", value]);

        Assert.Contains("--interval", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10000", 10000)]
    public void An_expect_from_1_to_10000_is_taken(string value, int runs) =>
        Assert.Equal(runs, StatusOptions.Parse(["--here", "--wait", "--expect", value]).Expect);

    [Theory]
    [InlineData("0")]
    [InlineData("10001")]
    [InlineData("-3")]
    [InlineData("two")]
    [InlineData("")]
    public void An_expect_outside_1_to_10000_or_not_a_whole_number_is_refused(string value)
    {
        string message = Refusal(["--here", "--wait", "--expect", value]);

        Assert.Contains("--expect", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1s", 1)]
    [InlineData("90s", 90)]
    [InlineData("25m", 1500)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void A_timeout_is_a_duration_in_seconds_minutes_hours_or_days(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), StatusOptions.Parse(["--here", "--wait", "--timeout", value]).Timeout);

    [Theory]
    [InlineData("0s")]
    [InlineData("0m")]
    [InlineData("0d")]
    public void A_timeout_of_zero_is_refused_as_not_above_zero(string value)
    {
        string message = Refusal(["--here", "--wait", "--timeout", value]);

        Assert.Contains("--timeout", message, StringComparison.Ordinal);
        Assert.Contains("above zero", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("90")]
    [InlineData("5x")]
    [InlineData("-5m")]
    [InlineData("1.5h")]
    [InlineData("m")]
    [InlineData("soon")]
    [InlineData("")]
    public void A_timeout_or_grace_that_is_not_a_duration_is_refused(string value)
    {
        Assert.Contains("--timeout", Refusal(["--here", "--wait", "--timeout", value]), StringComparison.Ordinal);
        Assert.Contains("--grace", Refusal(["--here", "--wait", "--grace", value]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("1h", 3600)]
    public void A_grace_is_a_duration(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), StatusOptions.Parse(["--here", "--wait", "--grace", value]).Grace);

    [Theory]
    [InlineData("2026-11-22T13:02:31Z")]
    [InlineData("2026-11-22T15:02:31+02:00")]
    [InlineData("2026-11-22T08:02:31-05:00")]
    [InlineData("2026-11-22T13:02:31")]
    public void A_start_time_is_a_date_and_time_with_Z_or_an_offset_and_without_one_it_is_UTC(string value)
    {
        DateTimeOffset expected = new(2026, 11, 22, 13, 2, 31, TimeSpan.Zero);

        Assert.Equal(expected, StatusOptions.Parse(["--here", "--started-after", value]).StartedAfter);
        Assert.Equal(expected, StatusOptions.Parse(["--here", "--wait", "--started-after", value]).StartedAfter);
    }

    [Fact]
    public void A_start_time_may_carry_fractions_of_a_second()
    {
        DateTimeOffset parsed = StatusOptions.Parse(["--here", "--started-after", "2026-11-22T13:02:31.5Z"]).StartedAfter!.Value;

        Assert.Equal(new DateTimeOffset(2026, 11, 22, 13, 2, 31, 500, TimeSpan.Zero), parsed);
    }

    // The design's <t> is a time (what `date -u +%FT%TZ` prints); a duration is --since's.
    [Theory]
    [InlineData("30m")]
    [InlineData("2026-11-22")]
    [InlineData("13:02:31")]
    [InlineData("yesterday")]
    [InlineData("")]
    public void A_start_time_that_is_not_a_date_and_time_is_refused(string value)
    {
        string message = Refusal(["--here", "--started-after", value]);

        Assert.Contains("--started-after", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--here --json --wait", "pass one mode")]
    [InlineData("--here --wait --json", "pass one mode")]
    [InlineData("--here --watch --json", "pass one mode")]
    [InlineData("--here --wait --watch", "pass one mode")]
    [InlineData("--here --bell", "--bell goes with --watch")]
    [InlineData("--here --json --bell", "--bell goes with --watch")]
    [InlineData("--here --wait --bell", "--bell goes with --watch")]
    [InlineData("--here --any", "go with --wait")]
    [InlineData("--here --expect 2", "go with --wait")]
    [InlineData("--here --grace 5m", "go with --wait")]
    [InlineData("--here --timeout 5m", "go with --wait")]
    [InlineData("--here --watch --any", "go with --wait")]
    [InlineData("--here --watch --expect 2", "go with --wait")]
    [InlineData("--here --watch --grace 5m", "go with --wait")]
    [InlineData("--here --watch --timeout 5m", "go with --wait")]
    [InlineData("--here --json --timeout 5m", "go with --wait")]
    [InlineData("--here --interval 5", "--interval goes with --wait or --watch")]
    [InlineData("--here --json --interval 5", "--interval goes with --wait or --watch")]
    [InlineData("--whoami --wait", "--whoami takes no scope")]
    [InlineData("--whoami --watch", "--whoami takes no scope")]
    [InlineData("--whoami --json", "--whoami takes no scope")]
    [InlineData("--whoami --here", "--whoami takes no scope")]
    [InlineData("--whoami --owner k:1", "--whoami takes no scope")]
    [InlineData("--whoami --started-after 2026-11-22T13:02:31Z", "--whoami takes no scope")]
    [InlineData("--whoami --since 1h", "--whoami takes no scope")]
    public async Task A_combination_the_design_forbids_is_exit_2_with_a_one_line_messageAsync(string args, string message)
    {
        (int code, string output) = await RunAsync(Words(args));

        Assert.Equal(2, code);
        Assert.StartsWith(UsageStart, output, StringComparison.Ordinal);
        Assert.Contains(message, output, StringComparison.Ordinal);
        Assert.Contains("usage: status", output, StringComparison.Ordinal);
        _ = Assert.Single(StatusIndexRig.Lines(output));
        Assert.EndsWith("\n", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--here --wait --any --expect 2 --grace 1m --timeout 1h --interval 3 --started-after 2026-11-22T13:02:31Z")]
    [InlineData("--here --watch --bell --interval 3 --started-after 2026-11-22T13:02:31Z")]
    [InlineData("--all --watch")]
    [InlineData("--owner k:1 --wait")]
    [InlineData("--run /x --wait")]
    [InlineData("--here --json --started-after 2026-11-22T13:02:31Z")]
    [InlineData("--here --started-after 2026-11-22T13:02:31Z")]
    public void The_combinations_of_the_design_are_taken(string args)
    {
        StatusCommand.StatusArgs a = StatusOptions.Parse(Words(args));

        Assert.NotNull(a);
    }

    [Theory]
    [InlineData("--interval")]
    [InlineData("--timeout")]
    [InlineData("--grace")]
    [InlineData("--expect")]
    [InlineData("--started-after")]
    public async Task A_flag_without_its_value_is_exit_2_and_names_the_flagAsync(string flag)
    {
        (int code, string output) = await RunAsync("--here", "--wait", flag);

        Assert.Equal(2, code);
        Assert.Contains(flag + " needs a value", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--interval")]
    [InlineData("--timeout")]
    [InlineData("--grace")]
    [InlineData("--expect")]
    [InlineData("--started-after")]
    public async Task A_hostile_value_is_cleaned_in_the_usage_messageAsync(string flag)
    {
        (int code, string output) = await RunAsync("--here", "--wait", flag, StatusHostileText.Number);

        Assert.Equal(2, code);
        Assert.Contains("lean-worker status: " + flag, output, StringComparison.Ordinal);
        Assert.Empty(TerminalAssert.PlainProblems(output));
        _ = Assert.Single(StatusIndexRig.Lines(output));
    }

    [Theory]
    [InlineData("--wait")]
    [InlineData("--watch")]
    public async Task A_wait_or_watch_called_without_a_host_to_sleep_and_draw_on_is_exit_2Async(string mode)
    {
        (int code, string output) = await RunAsync("--here", mode);

        Assert.Equal(2, code);
        Assert.Contains(mode + " needs a host", output, StringComparison.Ordinal);
    }
}
