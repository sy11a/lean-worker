using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status --watch` with output redirected (docs/research/live-status/README.md, section 4.3, "Non-TTY mode"): one line per
/// run when the watch starts and one per change after that, never an escape or a bell, whatever the runs' files hold
/// and with --bell; it runs until it is stopped.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class StatusWatchLinesTests : IDisposable
{
    private readonly StatusLiveRig _rig = new();

    public StatusWatchLinesTests() => _rig.Terminal.OutputRedirected = true;

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    private async Task<StatusLiveRig.Result> WatchAsync(params string[] more)
    {
        StatusLiveRig.Result result = await _rig.CallAsync(["--owner", StatusLiveRig.Key, "--watch", .. more]);
        return result;
    }

    private static void AssertNoEscapeNoBell(StatusLiveRig.Result r)
    {
        Assert.DoesNotContain('\u001b', r.Output);
        Assert.DoesNotContain('\a', r.Output);
        Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(r.Output), b => b is 0x1b or 0x07);
        Assert.Empty(TerminalAssert.PlainProblems(r.Output.Replace("\r", string.Empty, StringComparison.Ordinal)));
        Assert.Equal(string.Empty, r.Stderr);
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ ", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Stamp();

    [Fact]
    public async Task At_the_start_there_is_one_line_per_run_with_its_state_and_counters_and_the_watch_runs_until_stoppedAsync()
    {
        _ = _rig.Add("run-a");
        _ = _rig.AddEnded("run-done");
        _rig.At(Seconds(30), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
        Assert.Equal(2, r.Lines.Length);
        Assert.Contains("2026-11-22T12:00:00Z run-a running 12 calls $0.41 3m41s", r.Lines, StringComparer.Ordinal);
        Assert.Contains("2026-11-22T12:00:00Z run-done success 12 calls $0.41 3m41s", r.Lines, StringComparer.Ordinal);
        Assert.All(r.Lines, l => Assert.Matches(Stamp(), l));
        AssertNoEscapeNoBell(r);
    }

    [Fact]
    public async Task A_change_of_state_is_one_line_with_the_old_and_the_new_state_and_nothing_is_printed_while_nothing_changesAsync()
    {
        StatusLiveRig.Live a = _rig.Add("run-a");
        _ = _rig.Add("run-b");
        _rig.At(Seconds(60), () => _rig.End(a));
        _rig.At(Seconds(90), () => _rig.CancelHost());
        string[] before = [];
        _rig.At(Seconds(58), () => before = StatusIndexRig.Lines(_rig.Out.ToString()));

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Equal(2, before.Length);
        Assert.Equal(3, r.Lines.Length);
        Assert.Equal("2026-11-22T12:01:00Z run-a running -> success 12 calls $0.41 3m41s", r.Lines[2]);
    }

    [Fact]
    public async Task A_run_that_ends_in_failure_shows_why_in_parenthesesAsync()
    {
        StatusLiveRig.Live a = _rig.Add("run-a");
        _rig.At(Seconds(10), () => _rig.End(a, "launch-failed", error: "no such profile"));
        _rig.At(Seconds(20), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Equal("2026-11-22T12:00:10Z run-a running -> failed (no such profile)", r.Lines[1]);
    }

    [Fact]
    public async Task A_run_that_appears_later_gets_a_line_with_its_state_and_no_arrowAsync()
    {
        _ = _rig.Add("run-a");
        _rig.At(Seconds(10), () => _ = _rig.Add("run-late"));
        _rig.At(Seconds(20), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Equal(2, r.Lines.Length);
        Assert.Equal("2026-11-22T12:00:10Z run-late running 12 calls $0.41 3m51s", r.Lines[1]);
        Assert.DoesNotContain("->", r.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Never_an_escape_or_a_bell_not_even_with_bell_and_a_run_that_endsAsync()
    {
        StatusLiveRig.Live a = _rig.Add("run-a");
        _rig.At(Seconds(5), () => _rig.End(a));
        _rig.At(Seconds(10), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync("--bell");

        Assert.Equal(2, r.Lines.Length);
        AssertNoEscapeNoBell(r);
        Assert.DoesNotContain("\u001b[?1049h", r.Output, StringComparison.Ordinal);
        Assert.Equal(0, _rig.Terminal.Registered);
    }

    [Fact]
    public async Task The_reads_are_one_interval_apartAsync()
    {
        _ = _rig.Add("run-a");
        _rig.At(Seconds(31), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync("--interval", "5");

        Assert.Equal(0, r.Code);
        Assert.All(_rig.Pauses, p => Assert.Equal(Seconds(5), p));
        Assert.Equal(7, _rig.Pauses.Count);
    }

    [Fact]
    public async Task The_default_interval_is_2_seconds_in_line_mode_tooAsync()
    {
        _ = _rig.Add("run-a");
        _rig.At(Seconds(9), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.All(_rig.Pauses, p => Assert.Equal(Seconds(2), p));
        Assert.Equal(0, r.Code);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_ends_the_watch_with_exit_0_and_no_exceptionAsync()
    {
        _ = _rig.Add("run-a");
        _rig.CancelHost();

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Null(r.Error);
        Assert.Equal(0, r.Code);
    }

    [Fact]
    public async Task Started_after_limits_the_runs_of_the_linesAsync()
    {
        _ = _rig.Add("run-old", startedAgo: TimeSpan.FromHours(3));
        _ = _rig.Add("run-new", startedAgo: TimeSpan.FromMinutes(5));
        _rig.At(Seconds(5), () => _rig.CancelHost());
        string after = StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromHours(1));

        StatusLiveRig.Result r = await WatchAsync("--started-after", after);

        string line = Assert.Single(r.Lines);
        Assert.Contains("run-new", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_read_reaps_and_a_later_one_does_notAsync()
    {
        TimeSpan old = TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
        StatusIndexRig.Added first = _rig.Idx.Add("old-1", [StatusLiveRig.Key], endedAgo: old, startedAgo: old + TimeSpan.FromHours(1));
        StatusIndexRig.Added? second = null;
        _rig.At(Seconds(3), () => second = _rig.Idx.Add("old-2", [StatusLiveRig.Key], endedAgo: old, startedAgo: old + TimeSpan.FromHours(1)));
        _rig.At(Seconds(9), () => _rig.CancelHost());

        StatusLiveRig.Result r = await WatchAsync();

        Assert.Equal(0, r.Code);
        Assert.False(File.Exists(Path.Combine(_rig.Idx.Folder, first.Pointer)));
        Assert.True(File.Exists(Path.Combine(_rig.Idx.Folder, second!.Pointer)));
    }
}
