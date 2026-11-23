using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A gate chain is one unit for `status --wait` (docs/research/live-status/README.md, sections 4.1 and 4.2): its one
/// launcher runs round after round, so a round that ended while a later round is live does not end the wait; the chain
/// ends when its last round has, and a chain that ends stuck or in an error (exit code 4 or 5) counts as failed.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitChainTests : IDisposable
{
    private const string FirstId = "20261122-115000-chain-w1";
    private const string SecondId = "20261122-115300-chain-w1";

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    private static void Finished(JsonObject doc)
    {
        doc["phase"] = "done";
        doc["end"] = StatusDocs.End("success", StatusDocs.Now - TimeSpan.FromMinutes(3));
    }

    // Round 1 of a chain, with its launcher alive; ended when asked (its gate said continue).
    private StatusLiveRig.Live Round1(bool ended) =>
        _rig.Add("chain-w1", startedAgo: TimeSpan.FromMinutes(10), chain: FirstId, id: FirstId, tweak: ended ? Finished : null);

    // Round 2, run by the same launcher.
    private StatusLiveRig.Live Round2(StatusLiveRig.Live first) =>
        _rig.Add("chain-w1", startedAgo: TimeSpan.FromMinutes(7), chain: FirstId, round: 2, id: SecondId, launcher: first.Pid);

    [Fact]
    public async Task A_round_that_ended_while_a_later_round_is_live_does_not_end_the_waitAsync()
    {
        _ = Round2(Round1(ended: true));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--timeout", "3m");

        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(3), _rig.Elapsed);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Equal(4, r.Lines.Length);
        Assert.StartsWith("running", r.Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chain_ends_when_its_last_round_has_endedAsync()
    {
        StatusLiveRig.Live second = Round2(Round1(ended: true));
        _rig.At(Seconds(30), () => _rig.End(second));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(30), Seconds(32));
        Assert.Equal(4, r.Lines.Length);
        Assert.StartsWith("success", r.Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_that_ends_and_a_later_round_that_starts_while_the_wait_runs_are_one_unitAsync()
    {
        StatusLiveRig.Live first = Round1(ended: false);
        StatusLiveRig.Live? second = null;
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first);
            second = Round2(first);
        });
        _rig.At(Seconds(40), () => _rig.End(second!));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(40), _rig.Elapsed);
        Assert.Contains("waiting on 1 run matched", r.Lines[0], StringComparison.Ordinal);
        Assert.Equal(4, r.Lines.Length);
    }

    [Fact]
    public async Task A_round_that_recorded_its_end_while_its_launcher_lives_is_not_ended_before_the_next_round_startsAsync()
    {
        StatusLiveRig.Live first = Round1(ended: false);
        _rig.At(Seconds(10), () => _rig.Record(first));
        _rig.At(Seconds(60), () => _rig.End(first));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(60), _rig.Elapsed);
    }

    [Theory]
    [InlineData("error", null, 1)]
    [InlineData("success", 4, 1)]
    [InlineData("success", 5, 1)]
    [InlineData("success", 0, 0)]
    [InlineData("wrapped-up", null, 3)]
    public async Task A_chain_ends_as_its_last_round_endedAsync(string status, int? exitCode, int code)
    {
        StatusLiveRig.Live second = Round2(Round1(ended: true));
        _rig.At(Seconds(30), () => _rig.End(second, status, exitCode));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(code, r.Code);
        Assert.Equal(4, r.Lines.Length);
    }

    [Fact]
    public async Task With_any_an_ended_round_of_a_chain_that_is_still_live_does_not_return_the_waitAsync()
    {
        _ = Round2(Round1(ended: true));
        _ = _rig.Add("plain-run");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any", "--timeout", "2m");

        Assert.Equal(7, r.Code);
        Assert.Equal(TimeSpan.FromMinutes(2), _rig.Elapsed);
    }

    [Fact]
    public async Task With_any_a_plain_run_that_ends_returns_the_wait_while_the_chain_is_still_liveAsync()
    {
        _ = Round2(Round1(ended: true));
        StatusLiveRig.Live plain = _rig.Add("plain-run");
        _rig.At(Seconds(20), () => _rig.End(plain));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(20), _rig.Elapsed);
        Assert.Contains("1 live, 1 ended", r.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_any_a_chain_that_has_ended_returns_the_waitAsync()
    {
        StatusLiveRig.Live second = Round2(Round1(ended: true));
        _ = _rig.Add("plain-run");
        _rig.At(Seconds(20), () => _rig.End(second));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--any");

        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(20), _rig.Elapsed);
    }
}
