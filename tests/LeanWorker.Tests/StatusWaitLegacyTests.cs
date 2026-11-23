using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Legacy rows in `status --wait` (the review of #147, finding 6; docs/research/live-status/README.md, section 5): a
/// legacy row (a run dir without status.json) whose summary.json gives a status is a finished run of an older launcher
/// and is not waited on (the first line counts it); any other legacy row may be a run that has not written its status
/// yet or whose status writes failed, so it holds the wait through --grace as an unknown run does, then exit 8.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitLegacyTests : IDisposable
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(2);

    private readonly StatusLiveRig _rig = new();
    private int _next;

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    // A legacy run dir; <summary> is the status its summary.json gives, "none" (no summary.json), "torn" (a summary.json
    // that does not parse) or "directory" (a summary.json that is no file).
    private string Legacy(string name, string summary)
    {
        string id = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"20261122-1100{_next++:00}-{name}");
        string dir = StatusDocs.WriteRun(_rig.Idx.Root, id, doc: null, summaryStatus: summary is "none" or "torn" or "directory" ? null : summary);
        if (summary is "torn")
        {
            File.WriteAllText(Path.Combine(dir, "summary.json"), "{ torn");
        }
        else if (summary is "directory")
        {
            _ = Directory.CreateDirectory(Path.Combine(dir, "summary.json"));
        }

        return dir;
    }

    private async Task<StatusLiveRig.Result> WaitAsync(params string[] more)
    {
        StatusLiveRig.Result result = await _rig.CallAsync(["--here", "--runs-root", _rig.Idx.Root, "--wait", .. more]);
        return result;
    }

    [Theory]
    [InlineData("none", "legacy")]
    [InlineData("torn", "legacy")]
    [InlineData("directory", "unreadable")]
    public async Task A_legacy_row_without_a_usable_summary_status_holds_the_wait_through_the_grace_then_exit_8_not_exit_6Async(string summary, string state)
    {
        _ = Legacy("unfinished", summary);

        StatusLiveRig.Result r = await WaitAsync("--grace", "2m", "--interval", "10");

        Assert.Null(r.Error);
        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, _grace, _grace + Seconds(10));
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("legacy runs not waited on", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains(r.Lines, l => l.StartsWith(state, StringComparison.Ordinal) && l.Contains("unfinished", StringComparison.Ordinal));
        Assert.EndsWith("(exit 8)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_legacy_row_without_a_summary_status_gives_exit_7_when_the_timeout_is_shorter_than_the_graceAsync()
    {
        _ = Legacy("unfinished", "none");

        StatusLiveRig.Result r = await WaitAsync("--grace", "10m", "--timeout", "1m", "--interval", "10");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(60), _rig.Elapsed);
    }

    [Fact]
    public async Task A_finished_legacy_row_is_counted_in_the_first_line_and_an_unfinished_one_is_waited_on_in_the_same_scopeAsync()
    {
        StatusLiveRig.Live live = _rig.Add("live-one");
        _ = Legacy("finished-ok", "success");
        _ = Legacy("finished-bad", "error");
        _ = Legacy("unfinished", "none");
        _rig.At(Seconds(10), () => _rig.End(live));

        StatusLiveRig.Result r = await WaitAsync("--grace", "2m", "--interval", "10");

        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, _grace, _grace + Seconds(10));
        Assert.Contains("waiting on 2 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("2 legacy runs not waited on", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("live-one", r.Output, StringComparison.Ordinal);
        Assert.Contains("unfinished", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("finished-ok", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("finished-bad", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_still_wins_over_a_legacy_row_that_stayed_unsure_exit_1_not_8Async()
    {
        StatusLiveRig.Live live = _rig.Add("live-one");
        _ = Legacy("unfinished", "none");
        _rig.At(Seconds(10), () => _rig.End(live, "error"));

        StatusLiveRig.Result r = await WaitAsync("--grace", "2m", "--interval", "10");

        Assert.Equal(1, r.Code);
        Assert.InRange(_rig.Elapsed, _grace, _grace + Seconds(10));
        Assert.EndsWith("(exit 1)", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_success_does_not_end_the_wait_before_the_unfinished_legacy_row_is_given_upAsync()
    {
        StatusLiveRig.Live live = _rig.Add("live-one");
        _ = Legacy("unfinished", "none");
        _rig.At(Seconds(10), () => _rig.End(live));

        StatusLiveRig.Result r = await WaitAsync("--grace", "2m", "--interval", "10");

        // Not exit 0 at 10 seconds: the run that has not written its status yet may still be running.
        Assert.NotEqual(0, r.Code);
        Assert.True(_rig.Elapsed >= _grace);
    }

    [Fact]
    public async Task A_legacy_row_that_writes_its_status_file_during_the_wait_is_judged_by_it_and_ends_the_wait_at_onceAsync()
    {
        string dir = Legacy("late-writer", "none");
        _rig.At(Seconds(30), () => File.WriteAllText(Path.Combine(dir, "status.json"),
            StatusDocs.Make("late-writer", phase: "done", launcherPid: 900, end: StatusDocs.End("success", StatusDocs.Now)).ToJsonString()));

        StatusLiveRig.Result r = await WaitAsync("--grace", "10m", "--interval", "10");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(30), _rig.Elapsed);
    }

    [Fact]
    public async Task With_expect_a_finished_legacy_row_does_not_count_toward_the_number_and_an_unfinished_one_doesAsync()
    {
        _ = Legacy("finished-ok", "success");
        _ = Legacy("unfinished", "none");

        StatusLiveRig.Result r = await WaitAsync("--expect", "2", "--grace", "1m", "--interval", "10");

        Assert.Equal(8, r.Code);
        Assert.Contains("only 1 of 2 expected runs appeared", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_of_finished_legacy_rows_only_is_still_exit_6_and_never_sleepsAsync()
    {
        _ = Legacy("finished-ok", "success");
        _ = Legacy("finished-bad", "error");

        StatusLiveRig.Result r = await WaitAsync();

        Assert.Equal(6, r.Code);
        Assert.Empty(_rig.Pauses);
    }
}
