using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status --watch` on a terminal (docs/research/live-status/README.md, section 4.3), driven through a fake terminal and a
/// sleep that moves the fake clock: the alternate screen and the cursor, the way out on q, a stop signal, a second stop
/// signal and an exception, the acknowledgement of ended rows, the bell, the resize, NO_COLOR, the interval and the
/// scope that is read again every refresh.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class StatusWatchTests : IDisposable
{
    private const string Enter = "\u001b[?1049h\u001b[?25l";
    private const string Leave = "\u001b[0m\u001b[?25h\u001b[?1049l";
    private const string Begin = "\u001b[?2026h\u001b[H";
    private const string Bold = "\u001b[1m";

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    private static string[] Frames(string output) => output.Split(Begin)[1..];

    private static string RowOf(string frame, string name) => frame.Split("\r\n").Single(l => l.Contains(name, StringComparison.Ordinal));

    private static string TimeOf(string frame) => HeaderTime().Match(frame).Groups["time"].Value;

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    private async Task<StatusLiveRig.Result> WatchAsync(params string[] more)
    {
        StatusLiveRig.Result result = await _rig.CallAsync(["--owner", StatusLiveRig.Key, "--watch", .. more]);
        return result;
    }

    [GeneratedRegex(@"(?<time>\d\d:\d\d:\d\d)\s*\u001b\[K", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HeaderTime();

    [Fact]
    public async Task The_watch_enters_the_alternate_screen_and_hides_the_cursor_once_and_restores_both_on_qAsync()
    {
        _ = _rig.Add("fix-run");
        _rig.At(Seconds(5), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.StartsWith(Enter, r.Output, StringComparison.Ordinal);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
        Assert.Equal(1, Count(r.Output, "\u001b[?1049h"));
        Assert.Equal(1, Count(r.Output, "\u001b[?25l"));
        Assert.Equal(1, Count(r.Output, "\u001b[?25h"));
        Assert.Equal(1, Count(r.Output, "\u001b[?1049l"));
        Assert.Equal(3, Frames(r.Output).Length);
        Assert.Equal(1, _rig.Terminal.Registered);
        Assert.Equal(1, _rig.Terminal.Unregistered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_capital_Q_and_Ctrl_C_read_as_a_key_leave_the_watch_tooAsync(bool controlC)
    {
        _ = _rig.Add("fix-run");
        _rig.At(Seconds(3), () =>
        {
            if (controlC)
            {
                _rig.Terminal.PressControlC();
            }
            else
            {
                _rig.Terminal.Press('Q');
            }
        });

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
        Assert.Equal(1, Count(r.Output, Leave));
    }

    [Fact]
    public async Task Other_keys_do_nothingAsync()
    {
        _ = _rig.Add("fix-run");
        _rig.At(Seconds(1), () => _rig.Terminal.Press('x'));
        _rig.At(Seconds(2), () => _rig.Terminal.Press('z'));
        _rig.At(Seconds(7), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Equal(0, r.Code);
        Assert.Equal(4, Frames(r.Output).Length);
        Assert.Equal(3, _rig.Terminal.KeysRead);
    }

    [Fact]
    public async Task A_stop_signal_ends_the_watch_with_the_terminal_restored_once_and_the_default_action_cancelledAsync()
    {
        _ = _rig.Add("fix-run");
        bool cancelled = false;
        _rig.At(Seconds(5), () => cancelled = _rig.Terminal.FireStop());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.True(cancelled);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
        Assert.Equal(1, Count(r.Output, Leave));
        Assert.Equal(1, _rig.Terminal.Unregistered);
    }

    [Fact]
    public async Task A_second_stop_signal_restores_the_terminal_at_once_and_lets_the_default_action_endAsync()
    {
        _ = _rig.Add("fix-run");
        bool first = false;
        bool second = true;
        string atSecond = string.Empty;
        _rig.At(Seconds(5), () =>
        {
            first = _rig.Terminal.FireStop();
            second = _rig.Terminal.FireStop();
            atSecond = _rig.Out.ToString();
        });

        StatusLiveRig.Result r = await WatchAsync();

        Assert.True(first);
        Assert.False(second);
        Assert.EndsWith(Leave, atSecond, StringComparison.Ordinal);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
        Assert.Equal(0, r.Code);
    }

    [Fact]
    public async Task When_reading_a_key_throws_the_terminal_is_restored_and_the_exception_is_not_swallowedAsync()
    {
        _ = _rig.Add("fix-run");
        _rig.Terminal.ReadFails = new InvalidOperationException("the terminal broke");

        StatusLiveRig.Result r = await WatchAsync();

        _ = Assert.IsType<InvalidOperationException>(r.Error);
        Assert.StartsWith(Enter, r.Output, StringComparison.Ordinal);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
        Assert.Equal(1, _rig.Terminal.Unregistered);
    }

    [Fact]
    public async Task When_the_loop_throws_after_some_frames_the_terminal_is_restored_nonethelessAsync()
    {
        _ = _rig.Add("fix-run");
        _rig.At(Seconds(5), () => throw new IOException("the pipe broke"));

        StatusLiveRig.Result r = await WatchAsync();

        _ = Assert.IsType<IOException>(r.Error);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
        Assert.Equal(1, Count(r.Output, "\u001b[?1049h"));
        Assert.Equal(1, Count(r.Output, "\u001b[?1049l"));
    }

    [Fact]
    public async Task The_host_token_ends_the_watch_with_exit_0_and_the_terminal_restoredAsync()
    {
        _ = _rig.Add("fix-run");
        _rig.At(Seconds(5), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.EndsWith(Leave, r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_is_one_synchronized_write_without_a_full_clear_and_every_line_erases_to_its_endAsync()
    {
        _ = _rig.Add("fix-run");
        _rig.At(Seconds(3), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.DoesNotContain("\u001b[2J", r.Output, StringComparison.Ordinal);
        foreach (string frame in Frames(r.Output))
        {
            string text = frame.Split(Leave)[0];
            Assert.EndsWith("\u001b[K\u001b[J\u001b[?2026l", text, StringComparison.Ordinal);
            string[] lines = text.Split("\r\n");
            Assert.Equal(3, lines.Length);
            Assert.All(lines[..^1], l => Assert.EndsWith("\u001b[K", l, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Ended_rows_stay_bold_until_a_is_pressed_and_a_run_that_ends_after_that_is_bold_againAsync()
    {
        StatusLiveRig.Live a = _rig.Add("run-a");
        StatusLiveRig.Live b = _rig.Add("run-b");
        _ = _rig.AddEnded("run-old");
        _rig.At(Seconds(5), () => _rig.End(a));
        _rig.At(Seconds(11), () => _rig.Terminal.Press('a'));
        _rig.At(Seconds(16), () => _rig.End(b));
        _rig.At(Seconds(20), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        string[] frames = Frames(r.Output);
        string[] afterEnd = [.. frames.Where(f => RowOf(f, "run-a").Contains("success", StringComparison.Ordinal))];
        Assert.NotEmpty(afterEnd);
        Assert.StartsWith(Bold, RowOf(afterEnd[0], "run-a"), StringComparison.Ordinal);
        string[] beforeAck = [.. afterEnd.Where(f => string.CompareOrdinal(TimeOf(f), "12:00:11") < 0)];
        Assert.All(beforeAck, f => Assert.StartsWith(Bold, RowOf(f, "run-a"), StringComparison.Ordinal));
        string[] afterAck = [.. afterEnd.Where(f => string.CompareOrdinal(TimeOf(f), "12:00:11") >= 0)];
        Assert.NotEmpty(afterAck);
        Assert.All(afterAck, f => Assert.DoesNotContain(Bold, RowOf(f, "run-a"), StringComparison.Ordinal));
        Assert.Contains(frames, f => TimeOf(f) is "12:00:11");
        string last = frames[^1];
        Assert.StartsWith(Bold, RowOf(last, "run-b"), StringComparison.Ordinal);
        Assert.DoesNotContain(Bold, RowOf(last, "run-a"), StringComparison.Ordinal);
        Assert.All(frames, f => Assert.DoesNotContain(Bold, RowOf(f, "run-old"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_bell_nothing_ringsAsync()
    {
        StatusLiveRig.Live a = _rig.Add("run-a");
        _rig.At(Seconds(5), () => _rig.End(a));
        _rig.At(Seconds(9), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.DoesNotContain('\a', r.Output);
    }

    [Fact]
    public async Task With_bell_it_rings_once_for_every_run_that_ends_after_the_first_frame_and_never_for_a_run_that_had_endedAsync()
    {
        _ = _rig.AddEnded("ended-at-start");
        StatusLiveRig.Live a = _rig.Add("run-a");
        StatusLiveRig.Live b = _rig.Add("run-b");
        StatusLiveRig.Live c = _rig.Add("run-c");
        _ = _rig.Add("never-ends");
        _rig.At(Seconds(5), () => _rig.End(a));
        _rig.At(Seconds(9), () => _rig.End(b, "error"));
        _rig.At(Seconds(9), () => _rig.End(c));
        _rig.At(Seconds(25), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync("--bell");

        string[] frames = Frames(r.Output);
        Assert.Equal(3, Count(r.Output, "\a"));
        Assert.DoesNotContain('\a', frames[0]);
        string[] ringing = [.. frames.Where(f => f.Contains('\a', StringComparison.Ordinal))];
        Assert.Equal(2, ringing.Length);
        Assert.Equal(1, Count(ringing[0], "\a"));
        Assert.Equal(2, Count(ringing[1], "\a"));
        Assert.Equal("12:00:06", TimeOf(ringing[0]));
        Assert.Equal("12:00:10", TimeOf(ringing[1]));
    }

    [Fact]
    public async Task A_resize_signal_redraws_at_once_at_the_new_widthAsync()
    {
        _ = _rig.Add("run-a");
        _rig.Terminal.Width = 130;
        _rig.At(Seconds(5), () =>
        {
            _rig.Terminal.Width = 60;
            _rig.Terminal.FireResize();
        });
        _rig.At(Seconds(8), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        string[] frames = Frames(r.Output);
        string[] times = [.. frames.Select(f => TimeOf(f))];
        Assert.Equal(["12:00:00", "12:00:02", "12:00:04", "12:00:05", "12:00:07"], times);
        Assert.Contains("PROFILE", frames[2], StringComparison.Ordinal);
        Assert.DoesNotContain("PROFILE", frames[3], StringComparison.Ordinal);
        Assert.DoesNotContain("MODEL", frames[3], StringComparison.Ordinal);
        Assert.DoesNotContain("MODEL", frames[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_size_of_the_window_is_read_for_every_frame_even_without_a_signalAsync()
    {
        _ = _rig.Add("run-a");
        _rig.Terminal.Width = 130;
        _rig.At(Seconds(3), () => _rig.Terminal.Width = 70);
        _rig.At(Seconds(5), () => _rig.Terminal.Height = 2);
        _rig.At(Seconds(7), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        string[] frames = Frames(r.Output);
        Assert.Contains("PROFILE", frames[1], StringComparison.Ordinal);
        Assert.DoesNotContain("PROFILE", frames[2], StringComparison.Ordinal);
        Assert.DoesNotContain("run-a", frames[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_narrower_than_54_columns_shows_only_that_the_terminal_is_too_narrowAsync()
    {
        _ = _rig.Add("run-a");
        _rig.Terminal.Width = 40;
        _rig.At(Seconds(3), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.All(Frames(r.Output), f => Assert.DoesNotContain("run-a", f, StringComparison.Ordinal));
        Assert.All(Frames(r.Output), f => Assert.Contains("terminal too narrow (54 columns needed)", f, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", false)]
    [InlineData("anything", false)]
    public async Task The_state_is_coloured_unless_no_color_is_set_to_somethingAsync(string? noColor, bool coloured)
    {
        StatusLiveRig.Live a = _rig.Add("run-a");
        _rig.At(Seconds(3), () => _rig.End(a));
        _rig.At(Seconds(7), () => _rig.Terminal.Press('q'));
        Func<string, string?> env = noColor is null ? StatusIndexRig.NoCaller : StatusIndexRig.Env(("NO_COLOR", noColor));

        StatusLiveRig.Result r = await _rig.CallAsync(env, "--owner", StatusLiveRig.Key, "--watch");

        Assert.Contains(Frames(r.Output), f => RowOf(f, "run-a").Contains("success", StringComparison.Ordinal));
        Assert.Equal(coloured, r.Output.Contains("\u001b[32m", StringComparison.Ordinal));
        Assert.True(coloured || !r.Output.Contains("\u001b[3", StringComparison.Ordinal), "colour escapes under NO_COLOR");
    }

    [Theory]
    [InlineData("--interval 1", 4500, "12:00:00 12:00:01 12:00:02 12:00:03 12:00:04")]
    [InlineData("--interval 5", 12500, "12:00:00 12:00:05 12:00:10")]
    [InlineData("", 8500, "12:00:00 12:00:02 12:00:04 12:00:06 12:00:08")]
    public async Task The_interval_is_the_period_between_reads_and_the_default_is_2_secondsAsync(string option, int quitAtMs, string times)
    {
        _ = _rig.Add("run-a");
        _rig.At(TimeSpan.FromMilliseconds(quitAtMs), () => _rig.Terminal.Press('q'));
        string[] more = option.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        StatusLiveRig.Result r = await WatchAsync(more);

        string[] seen = [.. Frames(r.Output).Select(f => TimeOf(f))];
        Assert.Equal(times.Split(' '), seen);
    }

    [Fact]
    public async Task The_scope_is_read_again_every_refresh_so_a_run_launched_later_appearsAsync()
    {
        _ = _rig.Add("run-a");
        _rig.At(Seconds(5), () => _ = _rig.Add("run-late"));
        _rig.At(Seconds(9), () => _rig.Terminal.Press('q'));

        StatusLiveRig.Result r = await WatchAsync();

        string[] frames = Frames(r.Output);
        Assert.DoesNotContain("run-late", frames[2], StringComparison.Ordinal);
        Assert.Contains("run-late", frames[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Started_after_limits_the_runs_of_the_tableAsync()
    {
        _ = _rig.Add("run-old", startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("run-new", startedAgo: TimeSpan.FromMinutes(5));
        _rig.At(Seconds(3), () => _rig.Terminal.Press('q'));
        string after = StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromHours(1));

        StatusLiveRig.Result r = await WatchAsync("--started-after", after);

        Assert.All(Frames(r.Output), f => Assert.Contains("run-new", f, StringComparison.Ordinal));
        Assert.All(Frames(r.Output), f => Assert.DoesNotContain("run-old", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_first_read_reaps_and_the_watch_exits_with_0_wherever_the_runs_endedAsync()
    {
        TimeSpan old = TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
        StatusIndexRig.Added first = _rig.Idx.Add("old-1", [StatusLiveRig.Key], endedAgo: old, startedAgo: old + TimeSpan.FromHours(1));
        StatusIndexRig.Added? second = null;
        _rig.At(Seconds(3), () => second = _rig.Idx.Add("old-2", [StatusLiveRig.Key], endedAgo: old, startedAgo: old + TimeSpan.FromHours(1)));
        _rig.At(Seconds(9), () => _rig.Terminal.Press('q'));
        StatusLiveRig.Live failing = _rig.Add("failing");
        _rig.At(Seconds(5), () => _rig.End(failing, "error"));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Equal(0, r.Code);
        Assert.False(File.Exists(Path.Combine(_rig.Idx.Folder, first.Pointer)));
        Assert.True(File.Exists(Path.Combine(_rig.Idx.Folder, second!.Pointer)));
    }

    [Fact]
    public async Task A_caller_with_no_identity_gets_exit_2_before_the_screen_is_touchedAsync()
    {
        StatusLiveRig.Result r = await _rig.CallAsync("--watch");

        Assert.Equal(2, r.Code);
        Assert.DoesNotContain("\u001b", r.Output, StringComparison.Ordinal);
        Assert.Equal(0, _rig.Terminal.Registered);
    }
}
