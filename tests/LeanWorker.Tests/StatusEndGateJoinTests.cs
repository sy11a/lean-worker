using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The two joins of `status --wait` the follow-up review of #147 changed (docs/research/live-status/README.md, section 4.2): a row once joined
/// to a unit stays joined while the read lists it, judged by its new state and shown under the name it had even when its
/// status file is gone or hurt; and a row is joined by chain id only within the runs folder of the unit's first round
/// (run ids are unique only there), so a chain of the same name in another repository changes no verdict.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusEndGateJoinTests : IDisposable
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(2);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    [Theory]
    [InlineData("gone", 1, "gone")]
    [InlineData("garbled", 8, "unreadable")]
    public async Task A_row_joined_by_chain_id_stays_joined_when_its_status_file_goes_and_shows_under_the_name_it_hadAsync(string how, int code, string state)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        StatusLiveRig.Live? other = null;
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first);
            other = _rig.Add("split-round-2", startedAgo: TimeSpan.FromMinutes(7), chain: StatusEndGateScenes.FirstId, round: 2, id: StatusEndGateScenes.LaterId);
        });
        _rig.At(Seconds(20), () =>
        {
            if (how is "gone")
            {
                _rig.Vanish(other!);
            }
            else
            {
                StatusLiveRig.Garble(other!);
            }
        });
        _rig.At(Seconds(40), () => _rig.End(first));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "2m", "--timeout", "30m");

        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(40) + (code is 8 ? _grace : TimeSpan.Zero), Seconds(42) + (code is 8 ? _grace : TimeSpan.Zero));
        // Judged by its new state, listed under the name it had (not under its run id).
        Assert.Contains(r.Lines, l => l.StartsWith(state, StringComparison.Ordinal) && l.Contains("split-round-2", StringComparison.Ordinal));
        Assert.DoesNotContain(StatusEndGateScenes.LaterId, r.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task A_row_of_the_same_chain_id_joins_the_unit_in_its_runs_folder_and_not_in_anotherAsync(bool sameFolder, int code)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _ = StatusEndGateScenes.Ended(_rig, sameFolder ? _rig.Idx.Root : _rig.Idx.Outside(), "g-foreign", StatusEndGateScenes.FirstId);
        _rig.At(Seconds(20), () => _rig.End(first));

        StatusLiveRig.Result r = await _rig.CallAsync("--all", "--wait", "--grace", "2m", "--timeout", "30m");

        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(20), Seconds(22));
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        // A row that is joined is listed with the unit; one that is not takes no part in the wait or its listing.
        Assert.Equal(sameFolder, r.Output.Contains("g-foreign", StringComparison.Ordinal));
        Assert.EndsWith(StatusEndGateScenes.Exit(code), r.Lines[^1], StringComparison.Ordinal);
    }

}
