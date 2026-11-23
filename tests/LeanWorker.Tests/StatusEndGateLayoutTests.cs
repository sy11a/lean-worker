using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A round that ended with <c>end.gate: "continue"</c> is not an end until its next round is seen (docs/research/live-status/README.md,
/// section 4.2; the follow-up review of #147, finding 1), in what every reader shows: the one-shot listing, <c>--json</c>
/// and <c>--watch</c> (a frame and the lines of a redirected watch). With no later round in the read, the row is
/// <c>unknown</c> with the detail the code gives; a later round seen by launcher, or by chain id in the same runs folder, with a
/// higher round or no round at all, leaves it as it is; a round whose launcher is still alive is never marked.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusEndGateLayoutTests : IDisposable
{
    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    public static TheoryData<string, string, bool> Cases()
    {
        // later: a second row of the chain ("none": there is none); marked: whether round 1's row reads unknown.
        (string Later, bool Marked)[] scenes =
        [
            ("none", true),
            ("launcher:1", true),
            ("launcher:0", true),
            ("chain:1", true),
            ("other-folder", true),
            ("launcher:2", false),
            ("launcher:none", false),
            ("chain:2", false),
            ("chain:none", false),
        ];
        TheoryData<string, string, bool> data = [];
        foreach (string form in new[] { "plain", "json", "frame", "lines" })
        {
            foreach ((string later, bool marked) in scenes)
            {
                data.Add(form, later, marked);
            }
        }

        return data;
    }

    // Round 1 ended (its launcher gone) with the gate going on; <later> is the second row, if any.
    private void Arrange(string later)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        if (later is "other-folder")
        {
            _ = StatusEndGateScenes.Ended(_rig, _rig.Idx.Outside(), "g-later", StatusEndGateScenes.FirstId, "success");
        }
        else if (later is not "none")
        {
            StatusLiveRig.Live second = StatusEndGateScenes.Later(_rig, first, later);
            _rig.Record(second);
        }

        _rig.End(first, gateJson: StatusEndGateScenes.Continue);
    }

    // The state and the detail the form shows for the row named g-first.
    private async Task<(string State, string Detail, string Output)> ShowAsync(string form)
    {
        if (form is "json")
        {
            StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--json");
            Assert.Empty(TerminalAssert.JsonProblems(r.Output));
            JsonObject row = StatusIndexRig.JsonRows(r.Output).Single(o => o["run"]?["name"]?.GetValue<string>() is "g-first");
            return (row["state"]!.GetValue<string>(), row["detail"]!.GetValue<string>(), r.Output);
        }

        if (form is "plain")
        {
            StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key);
            string line = r.Lines.Single(l => l.Contains("g-first", StringComparison.Ordinal));
            return (line.Split(' ')[0], line, r.Output);
        }

        if (form is "frame")
        {
            _rig.Terminal.Width = 400;
            _rig.At(TimeSpan.FromSeconds(3), () => _rig.Terminal.Press('q'));
            StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--watch");
            string frame = r.Output.Split("\u001b[?2026h\u001b[H")[^1];
            string line = frame.Split("\r\n").Single(l => l.Contains("g-first", StringComparison.Ordinal));
            return (line.Contains("unknown", StringComparison.Ordinal) ? "unknown" : "other", line, r.Output);
        }

        _rig.Terminal.OutputRedirected = true;
        _rig.At(TimeSpan.FromSeconds(3), () => _rig.CancelHost());
        StatusLiveRig.Result w = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--watch");
        string watched = w.Lines.Single(l => l.Contains("g-first", StringComparison.Ordinal));
        return (watched.Split(' ')[2], watched, w.Output);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_round_whose_gate_went_on_reads_unknown_only_while_no_later_round_of_it_is_seenAsync(string form, string later, bool marked)
    {
        Arrange(later);

        (string state, string detail, string output) = await ShowAsync(form);

        if (form is "plain" or "lines")
        {
            Assert.Empty(TerminalAssert.PlainProblems(output.Replace("\r", string.Empty, StringComparison.Ordinal)));
        }

        if (marked)
        {
            Assert.Equal("unknown", state);
            // The frame's last column shows the tool, not the detail; every other form shows the detail.
            if (form is not "frame")
            {
                Assert.Contains(StatusLayout.AwaitingDetail, detail, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Equal(form is "frame" ? "other" : "success", state);
            Assert.DoesNotContain(StatusLayout.AwaitingDetail, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("json")]
    [InlineData("lines")]
    public async Task A_round_whose_launcher_is_still_alive_is_not_marked_and_is_once_the_launcher_is_goneAsync(string form)
    {
        StatusLiveRig.Live first = StatusEndGateScenes.First(_rig);
        _rig.Record(first, gateJson: StatusEndGateScenes.Continue);
        if (form is "lines")
        {
            _rig.Terminal.OutputRedirected = true;
            _rig.At(TimeSpan.FromSeconds(10), () => _rig.End(first, gateJson: StatusEndGateScenes.Continue));
            _rig.At(TimeSpan.FromSeconds(20), () => _rig.CancelHost());
            StatusLiveRig.Result w = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--watch");

            // The launcher's end is recorded, it runs the next round: the row is ending, then the launcher is gone and it reads unknown.
            Assert.Equal(2, w.Lines.Length);
            Assert.Contains(" g-first ending ", w.Lines[0], StringComparison.Ordinal);
            Assert.Contains(" g-first ending -> unknown (" + StatusLayout.AwaitingDetail + ")", w.Lines[1], StringComparison.Ordinal);
            return;
        }

        string[] args = form is "json" ? ["--owner", StatusLiveRig.Key, "--json"] : ["--owner", StatusLiveRig.Key];
        StatusLiveRig.Result alive = await _rig.CallAsync(args);
        _rig.End(first, gateJson: StatusEndGateScenes.Continue);
        StatusLiveRig.Result gone = await _rig.CallAsync(args);

        // The JSON writer escapes the apostrophe of the detail.
        Assert.DoesNotContain(StatusLayout.AwaitingDetail, alive.Output.Replace("\\u0027", "'", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("ending", alive.Output, StringComparison.Ordinal);
        Assert.Contains(StatusLayout.AwaitingDetail, gone.Output.Replace("\\u0027", "'", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
