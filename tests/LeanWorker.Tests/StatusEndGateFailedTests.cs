using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// An end keeps its own verdict whatever <c>end.gate</c> says unless it reads as a success (state <c>success</c>, exit code 0
/// or none): only then does <c>"continue"</c> make the round await its next round (issue #152; docs/research/live-status/README.md,
/// section 4.2, "A chain round that went on"). A failed end gives 1 and a wrap-up gives 3 at once, a <c>success</c> end with a
/// non-zero exit code (a shape no honest launcher writes) is a failure and gives 1 at once, never a 0; the same for a joined
/// round, in every scope, under --any, and in what the listing and --json show (the failure, not <c>unknown</c>).
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusEndGateFailedTests : IDisposable
{
    private const int Omitted = -1;

    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(2);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    // The status of the end, its exit code (Omitted: none), the exit code of a wait over a lone run with it, whether the wait holds
    // the end for --grace (the end reads as a success the gate marks); every end carries "gate": "continue".
    public static TheoryData<string, int, int, bool> Ends()
    {
        TheoryData<string, int, int, bool> data = [];
        foreach ((string status, int exit, int code, bool held) in Cases())
        {
            data.Add(status, exit, code, held);
        }

        return data;
    }

    public static TheoryData<string, string, int, int, bool> EndsByScope()
    {
        TheoryData<string, string, int, int, bool> data = [];
        foreach (string scope in new[] { "owner", "here", "run", "all" })
        {
            foreach ((string status, int exit, int code, bool held) in Cases())
            {
                data.Add(scope, status, exit, code, held);
            }
        }

        return data;
    }

    private static IEnumerable<(string Status, int Exit, int Code, bool Held)> Cases()
    {
        return
        [
            ("error", 1, 1, false),
            ("timeout", 1, 1, false),
            ("wrapped-up", 3, 3, false),
            ("success", 5, 1, false),
            ("success", 1, 1, false),
            ("success", 0, 8, true),
            ("success", Omitted, 8, true),
        ];
    }

    private static string[] Scope(string scope, StatusLiveRig rig, StatusLiveRig.Live run)
    {
        return scope switch
        {
            "owner" => ["--owner", StatusLiveRig.Key],
            "here" => ["--here", "--runs-root", rig.Idx.Root],
            "run" => ["--run", run.Dir],
            _ => ["--all"],
        };
    }

    private void End(StatusLiveRig.Live run, string status, int exit)
    {
        if (exit is Omitted)
        {
            _rig.End(run, status, omitExitCode: true, gateJson: StatusEndGateScenes.Continue);
        }
        else
        {
            _rig.End(run, status, exit, gateJson: StatusEndGateScenes.Continue);
        }
    }

    private async Task<StatusLiveRig.Result> WaitAsync(string[] scope, bool any)
    {
        List<string> args = [.. scope, "--wait", "--grace", "2m", "--timeout", "30m"];
        if (any)
        {
            args.Add("--any");
        }

        StatusLiveRig.Result r = await _rig.CallAsync([.. args]);
        return r;
    }

    private void AssertEnded(StatusLiveRig.Result r, int code, bool held)
    {
        TimeSpan hold = held ? _grace : TimeSpan.Zero;
        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.EndsWith(StatusEndGateScenes.Exit(code), r.Lines[^1], StringComparison.Ordinal);
        Assert.InRange(_rig.Elapsed, Seconds(10) + hold, Seconds(12) + hold);
        Assert.Empty(TerminalAssert.PlainProblems(r.Output));
    }

    [Theory]
    [MemberData(nameof(EndsByScope))]
    public async Task A_round_that_ends_with_a_forged_continue_keeps_its_own_verdict_unless_it_reads_as_a_success_in_every_scopeAsync(string scope, string status, int exit, int code, bool held)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.At(Seconds(10), () => End(first, status, exit));

        StatusLiveRig.Result r = await WaitAsync(Scope(scope, _rig, first), any: false);

        AssertEnded(r, code, held);
        if (held)
        {
            Assert.Contains(r.Lines, l => l.StartsWith("unknown", StringComparison.Ordinal) && l.Contains("g-first", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(r.Lines, l => l.StartsWith("unknown", StringComparison.Ordinal));
            Assert.DoesNotContain(StatusLayout.AwaitingDetail, r.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Ends))]
    public async Task With_any_a_forged_continue_on_a_failed_or_wrapped_up_end_ends_the_wait_at_once_and_on_a_clean_end_holds_itAsync(string status, int exit, int code, bool held)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.At(Seconds(10), () => End(first, status, exit));

        StatusLiveRig.Result r = await WaitAsync(["--owner", StatusLiveRig.Key], any: true);

        AssertEnded(r, code, held);
    }

    [Fact]
    public async Task A_clean_end_with_a_forged_continue_and_no_later_round_gives_exit_7_when_the_timeout_comes_firstAsync()
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.At(Seconds(10), () => End(first, "success", 0));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "2m", "--timeout", "90s");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(90), _rig.Elapsed);
    }

    // A joined round: another launcher's row of the chain (the chain id of round 1, a launcher of its own), ended at 10 s with
    // the gate going on while round 1 runs until 40 s. A joined row can only make the unit stricter: every end that is not a
    // success gives 1 at once (a wrap-up too: the unit is a failure when any row joined to it is not a success), and a clean one
    // that went on is held for --grace and gives 8.
    [Theory]
    [InlineData("owner", "error", 1, 1, false)]
    [InlineData("owner", "timeout", 1, 1, false)]
    [InlineData("owner", "wrapped-up", 3, 1, false)]
    [InlineData("owner", "success", 5, 1, false)]
    [InlineData("owner", "success", 0, 8, true)]
    [InlineData("owner", "success", Omitted, 8, true)]
    [InlineData("all", "error", 1, 1, false)]
    [InlineData("all", "wrapped-up", 3, 1, false)]
    [InlineData("all", "success", 5, 1, false)]
    [InlineData("all", "success", 0, 8, true)]
    [InlineData("here", "error", 1, 1, false)]
    [InlineData("here", "success", 5, 1, false)]
    [InlineData("here", "success", 0, 8, true)]
    public async Task A_joined_round_that_ends_with_a_forged_continue_keeps_its_own_verdict_unless_it_reads_as_a_successAsync(string scope, string status, int exit, int code, bool held)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.At(Seconds(10), () =>
        {
            _rig.Record(first);
            StatusLiveRig.Live joined = _rig.Add("g-joined", startedAgo: TimeSpan.FromMinutes(7), chain: StatusEndGateScenes.FirstId, round: 2, id: StatusEndGateScenes.LaterId);
            End(joined, status, exit);
        });
        _rig.At(Seconds(40), () => _rig.End(first));

        StatusLiveRig.Result r = await WaitAsync(Scope(scope, _rig, first), any: false);

        TimeSpan hold = held ? _grace : TimeSpan.Zero;
        Assert.Null(r.Error);
        Assert.Equal(code, r.Code);
        Assert.EndsWith(StatusEndGateScenes.Exit(code), r.Lines[^1], StringComparison.Ordinal);
        Assert.InRange(_rig.Elapsed, Seconds(40) + hold, Seconds(42) + hold);
        Assert.Empty(TerminalAssert.PlainProblems(r.Output));
        // The joined row is listed under its name as what it is: the end it records, or unknown while the gate's next round is unseen.
        Assert.Contains(r.Lines, l => l.StartsWith(held ? "unknown" : status, StringComparison.Ordinal) && l.Contains("g-joined", StringComparison.Ordinal));
    }

    // The failed end is shown as the failure it is: the state of the end it records, never unknown, the field or not.
    [Theory]
    [InlineData("plain", "error", 1)]
    [InlineData("plain", "timeout", 1)]
    [InlineData("plain", "wrapped-up", 3)]
    [InlineData("plain", "success", 5)]
    [InlineData("json", "error", 1)]
    [InlineData("json", "timeout", 1)]
    [InlineData("json", "wrapped-up", 3)]
    [InlineData("json", "success", 5)]
    public async Task The_listing_and_json_show_a_failed_or_wrapped_up_end_with_a_forged_continue_as_what_it_is_not_as_unknownAsync(string form, string status, int exit)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        End(first, status, exit);

        if (form is "json")
        {
            StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--json");
            Assert.Empty(TerminalAssert.JsonProblems(r.Output));
            JsonObject row = StatusIndexRig.JsonRows(r.Output).Single(o => o["run"]?["name"]?.GetValue<string>() is "g-first");
            Assert.Equal(status, row["state"]!.GetValue<string>());
            Assert.DoesNotContain(StatusLayout.AwaitingDetail, row["detail"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(StatusEndGateScenes.Continue.Trim('"'), row["end"]!["gate"]!.GetValue<string>());
        }
        else
        {
            StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key);
            string line = r.Lines.Single(l => l.Contains("g-first", StringComparison.Ordinal));
            Assert.Equal(status, line.Split(' ')[0]);
            Assert.DoesNotContain(StatusLayout.AwaitingDetail, r.Output, StringComparison.Ordinal);
            Assert.Empty(TerminalAssert.PlainProblems(r.Output));
        }
    }
}
