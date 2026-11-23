using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Where --started-after takes a run's start from (the review of #147, finding 4): the status file's start, else the
/// creation time of the pointer the run was read through, else the stamp of its run id, in that order; a run with none of
/// them is kept, in the one-shot listing and in the scope of `--wait`, also with --expect (a run dropped there would be
/// exit 8 after the grace, or silently not waited on). shared-dir rows have no status file, so their pointer's time counts.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusStartedAfterSourcesTests : IDisposable
{
    private static readonly TimeSpan _new = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _old = TimeSpan.FromHours(3);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    // The time --started-after takes in these tests: an hour ago; _new is after it and _old before it.
    private static string T() => StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromHours(1));

    // A live run whose three times are given: the status file's start, the pointer's creation time and the run id's stamp
    // (each null when the run has none of that kind).
    private StatusLiveRig.Live Timed(string name, TimeSpan? status, TimeSpan? pointer, TimeSpan? stamp)
    {
        string id = stamp is { } s ? StatusIndexRig.Id(StatusDocs.Now - s, name) : "no-stamp-in-" + name;
        StatusLiveRig.Live run = _rig.Add(name, startedAgo: status ?? TimeSpan.FromMinutes(5), id: id,
            tweak: d => _ = status is null && ((JsonObject)d["spec"]!).Remove("started_at"));
        if (pointer is { } p)
        {
            run.Run.PointerDoc["created_at"] = StatusDocs.Stamp(StatusDocs.Now - p);
        }
        else
        {
            _ = run.Run.PointerDoc.Remove("created_at");
        }

        _ = _rig.Idx.WritePointer(run.Run.Pointer, run.Run.PointerDoc.ToJsonString());
        return run;
    }

    private async Task<string[]> NamesAsync()
    {
        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--started-after", T(), "--json");
        Assert.Equal(0, r.Code);
        return [.. StatusIndexRig.JsonNames(r.Output).Order(StringComparer.Ordinal)];
    }

    // name, status start, pointer creation, id stamp (minutes ago; -1 when there is none), whether the run is kept
    [Theory]
    [InlineData("status-new-rest-old", 10, 180, 180, true)]
    [InlineData("status-old-rest-new", 180, 10, 10, false)]
    [InlineData("pointer-new-stamp-old", -1, 10, 180, true)]
    [InlineData("pointer-old-stamp-new", -1, 180, 10, false)]
    [InlineData("stamp-new", -1, -1, 10, true)]
    [InlineData("stamp-old", -1, -1, 180, false)]
    [InlineData("nothing", -1, -1, -1, true)]
    [InlineData("status-new-nothing-else", 10, -1, -1, true)]
    [InlineData("status-old-nothing-else", 180, -1, -1, false)]
    [InlineData("pointer-new-nothing-else", -1, 10, -1, true)]
    [InlineData("pointer-old-nothing-else", -1, 180, -1, false)]
    public async Task The_start_is_the_status_files_else_the_pointers_else_the_run_ids_in_that_orderAsync(string name, int statusAgo, int pointerAgo, int stampAgo, bool kept)
    {
        _ = Timed(name, Minutes(statusAgo), Minutes(pointerAgo), Minutes(stampAgo));
        _ = Timed("anchor-new", _new, _new, _new);

        string[] names = await NamesAsync();

        string[] expected = kept ? [.. new[] { "anchor-new", name }.Order(StringComparer.Ordinal)] : ["anchor-new"];
        Assert.Equal(expected, names);
    }

    private static TimeSpan? Minutes(int m) => m < 0 ? null : TimeSpan.FromMinutes(m);

    [Fact]
    public async Task A_shared_dir_row_is_judged_by_its_own_pointers_creation_time_not_by_the_run_idAsync()
    {
        // Two pointers name one run dir: both rows are shared-dir, with no status file read. Their run id stamps (5 minutes
        // ago) would keep both; their pointers' times (3 hours and 10 minutes ago) tell them apart.
        _ = _rig.Idx.Add("twin", [StatusLiveRig.Key], startedAgo: _old, id: "20261122-115500-twin");
        _ = _rig.Idx.Add("twin", [StatusLiveRig.Key], startedAgo: _new, id: "20261122-115500-twin");

        // Each call of the rig's command writes to the same writer, so these two go through the index rig's own.
        (int _, string recent) = await _rig.Idx.CallAsync("--owner", StatusLiveRig.Key, "--started-after", T(), "--json");
        (int _, string everything) = await _rig.Idx.CallAsync("--owner", StatusLiveRig.Key, "--started-after", StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromHours(4)), "--json");

        List<JsonObject> kept = StatusIndexRig.JsonRows(recent);
        _ = Assert.Single(kept);
        Assert.Equal("shared-dir", kept[0]["state"]!.GetValue<string>());
        Assert.Equal(2, StatusIndexRig.JsonRows(everything).Count);
    }

    [Fact]
    public async Task A_shared_dir_row_with_no_pointer_time_falls_back_to_the_run_ids_stampAsync()
    {
        foreach (string id in new[] { "20261122-115500-twin", "20261122-080000-twin" })
        {
            StatusIndexRig.Added a = _rig.Idx.Add("twin", [StatusLiveRig.Key], id: id);
            StatusIndexRig.Added b = _rig.Idx.Add("twin", [StatusLiveRig.Key], id: id);
            foreach (StatusIndexRig.Added p in new[] { a, b })
            {
                _ = p.PointerDoc.Remove("created_at");
                _ = _rig.Idx.WritePointer(p.Pointer, p.PointerDoc.ToJsonString());
            }
        }

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--started-after", T(), "--json");

        // The two rows of the 11:55 run dir are kept (their stamp is after 11:00); the two of the 08:00 one are not.
        Assert.Equal(2, StatusIndexRig.JsonRows(r.Output).Count);
    }

    [Fact]
    public async Task In_a_wait_with_expect_runs_with_no_time_at_all_count_toward_the_number_and_are_waited_onAsync()
    {
        StatusLiveRig.Live one = Timed("untimed-one", status: null, pointer: null, stamp: null);
        StatusLiveRig.Live two = Timed("untimed-two", status: null, pointer: null, stamp: null);
        _ = Timed("stale-run", _old, _old, _old);
        _rig.At(Seconds(10), () => _rig.End(one));
        _rig.At(Seconds(20), () => _rig.End(two));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", T(), "--expect", "2", "--grace", "1m");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(20), _rig.Elapsed);
        Assert.Contains("waiting on 2 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("untimed-one", r.Output, StringComparison.Ordinal);
        Assert.Contains("untimed-two", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-run", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_wait_with_expect_a_run_the_status_file_dates_before_the_time_does_not_fill_the_number_whatever_its_pointer_saysAsync()
    {
        // The status file's start is 3 hours ago; the pointer says 10 minutes ago. The status file comes first, so the
        // run is out, nothing fills --expect 1 and the wait is given up after the grace.
        _ = Timed("old-by-status", status: _old, pointer: _new, stamp: _new);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", T(), "--expect", "1", "--grace", "1m", "--interval", "10");

        Assert.Equal(8, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(60), Seconds(70));
        Assert.Contains("only 0 of 1 expected runs appeared", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_wait_the_pointers_time_counts_before_the_run_ids_and_the_run_ids_stamp_counts_when_there_is_no_otherAsync()
    {
        StatusLiveRig.Live viaPointer = Timed("via-pointer", status: null, pointer: _new, stamp: _old);
        StatusLiveRig.Live viaStamp = Timed("via-stamp", status: null, pointer: null, stamp: _new);
        _ = Timed("pointer-says-old", status: null, pointer: _old, stamp: _new);
        _ = Timed("stamp-says-old", status: null, pointer: null, stamp: _old);
        _rig.At(Seconds(10), () => _rig.End(viaPointer));
        _rig.At(Seconds(10), () => _rig.End(viaStamp));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", T(), "--expect", "2", "--grace", "1m");

        Assert.Equal(0, r.Code);
        Assert.Contains("waiting on 2 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("via-pointer", r.Output, StringComparison.Ordinal);
        Assert.Contains("via-stamp", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pointer-says-old", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stamp-says-old", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_expect_a_run_with_no_time_at_all_is_waited_on_too_and_never_silently_leftOutAsync()
    {
        StatusLiveRig.Live untimed = Timed("untimed", status: null, pointer: null, stamp: null);
        _rig.At(Seconds(30), () => _rig.End(untimed, "error"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", T());

        Assert.Equal(1, r.Code);
        Assert.Equal(Seconds(30), _rig.Elapsed);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shared_dir_row_inside_a_wait_with_started_after_is_a_unit_held_through_the_grace_then_exit_8Async()
    {
        _ = _rig.Idx.Add("twin", [StatusLiveRig.Key], startedAgo: _new, id: "20261122-115500-twin");
        _ = _rig.Idx.Add("twin", [StatusLiveRig.Key], startedAgo: _new, id: "20261122-115500-twin");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--started-after", T(), "--grace", "1m", "--interval", "10");

        Assert.Equal(8, r.Code);
        Assert.Contains("waiting on 2 runs matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Contains("shared-dir", r.Output, StringComparison.Ordinal);
    }
}
