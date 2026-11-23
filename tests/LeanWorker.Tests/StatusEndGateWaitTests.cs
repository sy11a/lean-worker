using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status --wait` on a chain whose round 1 ended with <c>end.gate: "continue"</c> (docs/research/live-status/README.md, sections 4.2 and 5; the
/// follow-up review of #147, findings 1 and 2): while no later round of it is seen, the unit has not ended, so a clean
/// round 1 never gives exit 0 with the chain's next round unseen: 8 after --grace, 7 when --timeout is shorter, in
/// every scope (--here with a round 2 whose status.json is never written or unreadable, --run, an index scope whose
/// round 2 has no pointer). A read that misses round 2 and a later one that sees it judge the unit by round 2. With --any such a unit
/// neither ends the wait nor counts as a 0.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusEndGateWaitTests : IDisposable
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(2);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    public static TheoryData<string, string, int, bool> Unseen()
    {
        // The scope, how round 2 is hurt, the exit code with the default --timeout (8 is a 7 under a shorter one).
        (string Scope, string How, int Code)[] cases =
        [
            ("owner", "never", 8),
            ("owner", "unlisted", 8),
            ("owner", "unreadable", 8),
            ("owner", "missing", 1),
            ("here", "never", 8),
            ("here", "missing", 8),
            ("here", "unreadable", 8),
            ("run", "never", 8),
            ("run", "missing", 8),
            ("run", "unreadable", 8),
            ("run", "unlisted", 8),
        ];
        TheoryData<string, string, int, bool> data = [];
        foreach ((string scope, string how, int code) in cases)
        {
            foreach (bool shortTimeout in new[] { false, true })
            {
                data.Add(scope, how, code, shortTimeout);
            }
        }

        return data;
    }

    // The review's scenario: the wait starts with round 1 alone; at 10 s its gate says continue (its end is recorded with
    // the gate, its launcher lives on) and round 2 appears, hurt as <how> says ("never": it does not appear); at 40 s the
    // launcher ends.
    private StatusLiveRig.Live Chain(string how)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first, gateJson: StatusEndGateScenes.Continue);
            if (how is "never")
            {
                return;
            }

            StatusLiveRig.Live second = _rig.Add("g-later", startedAgo: TimeSpan.FromMinutes(7), chain: StatusEndGateScenes.FirstId, round: 2,
                id: StatusEndGateScenes.LaterId, launcher: first.Pid);
            if (how is "missing")
            {
                StatusLiveRig.Unwrite(second);
            }
            else if (how is "unreadable")
            {
                StatusLiveRig.Garble(second);
            }
            else if (how is "unlisted")
            {
                _rig.Unlist(second);
            }
        });
        _rig.At(Seconds(40), () => _rig.End(first, gateJson: StatusEndGateScenes.Continue));
        return first;
    }

    private string[] Scope(string scope, StatusLiveRig.Live first)
    {
        return scope switch
        {
            "owner" => ["--owner", StatusLiveRig.Key],
            "here" => ["--here", "--runs-root", _rig.Idx.Root],
            _ => ["--run", first.Dir],
        };
    }

    private async Task<StatusLiveRig.Result> WaitAsync(string[] scope, params string[] more)
    {
        List<string> args = [.. scope, "--wait", "--grace", "2m"];
        if (!more.Contains("--timeout", StringComparer.Ordinal))
        {
            args.AddRange(["--timeout", "30m"]);
        }

        args.AddRange(more);
        StatusLiveRig.Result result = await _rig.CallAsync([.. args]);
        return result;
    }

    [Theory]
    [MemberData(nameof(Unseen))]
    public async Task A_clean_round_whose_gate_went_on_never_gives_exit_0_while_its_next_round_is_not_seenAsync(string scope, string how, int code, bool shortTimeout)
    {
        StatusLiveRig.Live first = Chain(how);
        int expected = code is 8 && shortTimeout ? 7 : code;

        StatusLiveRig.Result r = shortTimeout
            ? await WaitAsync(Scope(scope, first), "--timeout", "100s")
            : await WaitAsync(Scope(scope, first));

        Assert.Null(r.Error);
        Assert.NotEqual(0, r.Code);
        Assert.Equal(expected, r.Code);
        Assert.EndsWith(StatusEndGateScenes.Exit(expected), r.Lines[^1], StringComparison.Ordinal);
        Assert.Empty(TerminalAssert.PlainProblems(r.Output));
        if (expected is 7)
        {
            Assert.Equal(Seconds(100), _rig.Elapsed);
        }
        else if (expected is 8)
        {
            Assert.InRange(_rig.Elapsed, Seconds(40) + _grace, Seconds(42) + _grace);
        }
        else
        {
            Assert.InRange(_rig.Elapsed, Seconds(40), Seconds(42));
        }
    }

    [Fact]
    public async Task The_row_of_a_round_whose_next_round_is_not_seen_is_listed_as_unknown_not_as_a_successAsync()
    {
        StatusLiveRig.Live first = Chain("never");

        StatusLiveRig.Result r = await WaitAsync(Scope("owner", first));

        Assert.Equal(8, r.Code);
        Assert.Contains(r.Lines, l => l.StartsWith("unknown", StringComparison.Ordinal) && l.Contains("g-first", StringComparison.Ordinal)
            && l.Contains(StatusLayout.AwaitingDetail, StringComparison.Ordinal));
        Assert.DoesNotContain(r.Lines, l => l.StartsWith("success", StringComparison.Ordinal));
        Assert.Contains("1 of 1 runs stayed unknown or unreadable", r.Output, StringComparison.Ordinal);
    }

    // The read race (review finding 2): a read sees round 1 ended and no round 2, a later read sees round 2.
    [Theory]
    [InlineData("owner", "success", 0, false)]
    [InlineData("owner", "error", 1, false)]
    [InlineData("here", "success", 0, false)]
    [InlineData("here", "error", 1, false)]
    [InlineData("owner", "success", 0, true)]
    [InlineData("owner", "error", 1, true)]
    public async Task A_read_that_sees_round_1_ended_and_none_of_round_2_and_a_later_one_that_does_judge_the_unit_by_round_2Async(string scope, string status, int code, bool any)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.At(Seconds(10), () => _rig.Record(first, gateJson: StatusEndGateScenes.Continue));
        _rig.At(Seconds(20), () => _rig.End(first, gateJson: StatusEndGateScenes.Continue));
        _rig.At(Seconds(30), () =>
        {
            StatusLiveRig.Live second = _rig.Add("g-first", startedAgo: TimeSpan.FromMinutes(7), chain: StatusEndGateScenes.FirstId, round: 2,
                id: StatusEndGateScenes.LaterId, launcher: first.Pid);
            _rig.Record(second, status);
        });

        StatusLiveRig.Result r = any
            ? await WaitAsync(Scope(scope, first), "--any")
            : await WaitAsync(Scope(scope, first));

        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(30), Seconds(32));
        Assert.EndsWith(StatusEndGateScenes.Exit(code), r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_any_a_unit_whose_next_round_is_not_seen_does_not_end_the_wait_and_does_not_count_as_0Async()
    {
        StatusLiveRig.Live first = Chain("never");
        StatusLiveRig.Live plain = _rig.Add("plain-run");
        _rig.At(Seconds(60), () => _rig.End(plain));

        StatusLiveRig.Result r = await WaitAsync(["--owner", StatusLiveRig.Key], "--any");

        // At 40 s round 1 is over and reads unknown; only the plain run's end returns the wait, and the 0 is the plain run's.
        Assert.Equal(0, r.Code);
        Assert.Equal(Seconds(60), _rig.Elapsed);
        Assert.Contains("1 of 2 runs ended success", r.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 7)]
    public async Task With_any_a_unit_alone_whose_next_round_is_not_seen_is_given_up_after_the_grace_or_times_outAsync(bool shortTimeout, int code)
    {
        StatusLiveRig.Live first = Chain("never");

        StatusLiveRig.Result r = shortTimeout
            ? await WaitAsync(Scope("owner", first), "--any", "--timeout", "100s")
            : await WaitAsync(Scope("owner", first), "--any");

        Assert.Equal(code, r.Code);
        if (shortTimeout)
        {
            Assert.Equal(Seconds(100), _rig.Elapsed);
        }
        else
        {
            Assert.InRange(_rig.Elapsed, Seconds(40) + _grace, Seconds(42) + _grace);
        }
    }
}
