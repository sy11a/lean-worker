using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// What `status --wait` prints and what it touches (docs/research/live-status/README.md, sections 4.2 and 5): the first
/// line, nothing per poll, the listing and the closing line; output that stays clean for hostile files; the first read
/// reaps and no later one does; no run dir is written to; a cancelled wait ends without an exception.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitOutputTests : IDisposable
{
    private static readonly string _hostile = StatusHostileText.Value;
    private static readonly TimeSpan _week = TimeSpan.FromDays(7);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    private static void AssertClean(StatusLiveRig.Result r)
    {
        Assert.DoesNotContain('\u001b', r.Output);
        Assert.DoesNotContain('\a', r.Output);
        Assert.Empty(TerminalAssert.PlainProblems(r.Output.Replace("\r", string.Empty, StringComparison.Ordinal)));
        Assert.Equal(string.Empty, r.Stderr);
    }

    private static void Poison(string vector, JsonObject d)
    {
        if (vector is "name")
        {
            d["run"]!["name"] = _hostile;
        }
        else if (vector is "tool")
        {
            d["live"]!["last_tool"] = StatusDocs.Tool(_hostile, "running", StatusDocs.Now);
        }
        else if (vector is "model" or "runtime")
        {
            d["spec"]![vector] = _hostile;
        }
    }

    [Fact]
    public async Task The_first_line_says_how_many_runs_were_taken_and_when_and_that_later_runs_are_not_includedAsync()
    {
        StatusLiveRig.Live a = _rig.Add("one");
        StatusLiveRig.Live b = _rig.Add("two");
        _rig.At(Seconds(10), () => _rig.End(a));
        _rig.At(Seconds(10), () => _rig.End(b));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal("lean-worker status: waiting on 2 runs matched at 12:00:00Z; runs launched later are not included", r.Lines[0]);
    }

    [Fact]
    public async Task The_first_line_says_run_for_one_runAsync()
    {
        StatusLiveRig.Live a = _rig.Add("one");
        _rig.At(Seconds(10), () => _rig.End(a));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal("lean-worker status: waiting on 1 run matched at 12:00:00Z; runs launched later are not included", r.Lines[0]);
    }

    [Fact]
    public async Task Nothing_is_printed_per_poll_only_the_first_line_the_listing_and_the_closing_lineAsync()
    {
        StatusLiveRig.Live a = _rig.Add("one");
        StatusLiveRig.Live b = _rig.Add("two");
        _rig.At(Seconds(60), () => _rig.End(a));
        _rig.At(Seconds(120), () => _rig.End(b, "error"));
        string[] seen = [];
        _rig.At(Seconds(50), () => seen = StatusIndexRig.Lines(_rig.Out.ToString()));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.True(_rig.Pauses.Count > 50);
        _ = Assert.Single(seen);
        Assert.Equal(5, r.Lines.Length);
        Assert.StartsWith("lean-worker status: waiting on 2 runs", r.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("lean-worker status: owner k:1,", r.Lines[1], StringComparison.Ordinal);
        Assert.Contains("0 live, 2 ended, 1 needs attention", r.Lines[1], StringComparison.Ordinal);
        Assert.Equal("lean-worker status: wait over: 1 of 2 runs ended not success (exit 1)", r.Lines[4]);
    }

    [Theory]
    [InlineData("success", 0, "1 of 1 runs ended success")]
    [InlineData("wrapped-up", 3, "1 of 1 runs ended, 1 wrapped up near the budget")]
    [InlineData("error", 1, "1 of 1 runs ended not success")]
    public async Task The_closing_line_says_how_the_runs_ended_and_repeats_the_exit_codeAsync(string how, int code, string outcome)
    {
        StatusLiveRig.Live a = _rig.Add("one");
        _rig.At(Seconds(10), () => StatusWaitScenarios.Apply(_rig, a, how));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal("lean-worker status: wait over: " + outcome + " (exit " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", r.Lines[^1]);
        Assert.Equal(code, r.Code);
    }

    [Fact]
    public async Task The_listing_is_the_one_shot_listing_of_the_runs_waited_on_in_display_orderAsync()
    {
        StatusLiveRig.Live fine = _rig.Add("fine-run", startedAgo: TimeSpan.FromMinutes(9));
        StatusLiveRig.Live bad = _rig.Add("bad-run", startedAgo: TimeSpan.FromMinutes(8));
        _rig.At(Seconds(10), () => _rig.End(fine));
        _rig.At(Seconds(10), () => _rig.End(bad, "error"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.StartsWith("error", r.Lines[2], StringComparison.Ordinal);
        Assert.Contains("bad-run", r.Lines[2], StringComparison.Ordinal);
        Assert.StartsWith("success", r.Lines[3], StringComparison.Ordinal);
        Assert.Contains("fine-run", r.Lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_closing_line_of_a_wait_that_gave_up_says_why_and_names_the_graceAsync()
    {
        _rig.Blur(_rig.Add("unsure"));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "90s");

        Assert.Equal(8, r.Code);
        Assert.Equal("lean-worker status: wait over: 1 of 1 runs stayed unknown or unreadable past the grace of 1m30s (exit 8)", r.Lines[^1]);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("tool")]
    [InlineData("model")]
    [InlineData("runtime")]
    public async Task Hostile_content_of_a_run_that_is_still_live_when_the_wait_ends_never_reaches_stdout_or_stderrAsync(string vector)
    {
        _ = _rig.Add("hostile-live", tweak: d => Poison(vector, d));
        _ = _rig.Add("healthy-live");

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--timeout", "5s");

        AssertClean(r);
        Assert.Equal(7, r.Code);
        Assert.Equal(5, r.Lines.Length);
        Assert.Contains("healthy-live", r.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("tool")]
    [InlineData("model")]
    [InlineData("runtime")]
    [InlineData("run-dir")]
    [InlineData("failure-message")]
    public async Task Hostile_content_of_a_run_that_ended_stays_in_its_one_line_and_clean_in_stdout_and_stderrAsync(string vector)
    {
        string? id = vector is "run-dir" ? "20261122-115500-" + _hostile : null;
        StatusLiveRig.Live run = _rig.Add("hostile-run", id: id, tweak: d => Poison(vector, d));
        _rig.At(Seconds(10), () =>
        {
            if (vector is "failure-message")
            {
                _rig.End(run, "launch-failed", error: _hostile);
            }
            else
            {
                _rig.End(run, "error");
            }
        });

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        AssertClean(r);
        Assert.Equal(1, r.Code);
        Assert.Equal(4, r.Lines.Length);
        Assert.StartsWith("lean-worker status: waiting on 1 run", r.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("lean-worker status: owner k:1,", r.Lines[1], StringComparison.Ordinal);
        Assert.StartsWith("lean-worker status: wait over:", r.Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_read_reaps_like_the_one_shot_call_and_no_later_read_doesAsync()
    {
        StatusIndexRig.Added old1 = _rig.Idx.Add("old-1", [StatusLiveRig.Key], endedAgo: _week + TimeSpan.FromHours(1), startedAgo: _week + TimeSpan.FromHours(2));
        StatusLiveRig.Live live = _rig.Add("live-one");
        StatusIndexRig.Added? old2 = null;
        _rig.At(Seconds(10), () => old2 = _rig.Idx.Add("old-2", [StatusLiveRig.Key], endedAgo: _week + TimeSpan.FromHours(1), startedAgo: _week + TimeSpan.FromHours(2)));
        _rig.At(Seconds(30), () => _rig.End(live));

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(0, r.Code);
        Assert.False(File.Exists(Path.Combine(_rig.Idx.Folder, old1.Pointer)), "the first read reaps a pointer that is provably old");
        Assert.True(File.Exists(Path.Combine(_rig.Idx.Folder, old2!.Pointer)), "a later read never reaps");
        Assert.True(Directory.Exists(old1.Dir), "a run dir is never deleted");
    }

    [Fact]
    public async Task A_wait_never_writes_into_or_deletes_from_a_run_dir_or_the_indexAsync()
    {
        StatusLiveRig.Live lost = _rig.Add("will-be-lost");
        StatusLiveRig.Live slow = _rig.Add("slow-one");
        _ = _rig.AddEnded("ended-before", startedAgo: TimeSpan.FromMinutes(20));
        _rig.At(Seconds(20), () => _rig.Lose(lost));
        _rig.At(Seconds(40), () => _rig.Idx.Proc.Remove(slow.Pid));
        string[] before = StatusIndexRig.Snapshot(_rig.Idx.Root, _rig.Idx.Folder);

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Equal(1, r.Code);
        Assert.Equal(before, StatusIndexRig.Snapshot(_rig.Idx.Root, _rig.Idx.Folder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_wait_ends_promptly_without_an_exception_and_never_as_a_successAsync(bool cancelBeforeTheCall)
    {
        _ = _rig.Add("never-ends");
        if (cancelBeforeTheCall)
        {
            _rig.CancelHost();
        }
        else
        {
            _rig.At(Seconds(10), () => _rig.CancelHost());
        }

        StatusLiveRig.Result r = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--wait");

        Assert.Null(r.Error);
        Assert.NotEqual(0, r.Code);
        Assert.True(_rig.Elapsed < Seconds(30));
    }
}
