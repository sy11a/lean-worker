using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Hostile content in `status --watch` (docs/research/live-status/README.md, section 4.3): a run name, a tool name, a model, a
/// runtime, a state, a run dir, an owner key or a scope with escape sequences, CSI and OSC sequences, C1 controls, bidi
/// controls, line breaks or a bell character. A frame carries only the escape sequences the watcher itself emits, the
/// line mode none and no bell. Each scenario runs twice, once with the hostile value and once with a harmless one: the
/// output must have as many escape and bell characters (those the watcher emits) and as many lines.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class StatusWatchHostileTests
{
    private const string Benign = "benign-value";

    private static string Hostile => StatusHostileText.Value;

    public static TheoryData<string> Vectors => ["name", "model", "runtime", "profile", "tool", "state", "run-dir", "owner"];

    [GeneratedRegex(@"\u001b\[(?:\?1049h|\?25l|\?2026h|\?2026l|H|K|J|0m|1m|31m|32m|33m|\?25h|\?1049l)", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Allowed();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ ", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Stamp();

    private static int Count(string text, char c) => text.Count(x => x == c);

    // The vectors "state", "run-dir" and "owner" are poisoned by the caller, not in the status document.
    private static void Poison(string vector, string value, JsonObject d)
    {
        if (vector is "name")
        {
            d["run"]!["name"] = value;
        }
        else if (vector is "tool")
        {
            d["live"]!["last_tool"] = StatusDocs.Tool(value, "running", StatusDocs.Now);
        }
        else if (vector is "model" or "runtime" or "profile")
        {
            d["spec"]![vector] = value;
        }
    }

    // One watch over a hostile run and a healthy one: the hostile run ends in an error five seconds in, and the watch stops after ten.
    private static async Task<StatusLiveRig.Result> WatchAsync(string vector, string value, bool redirected, bool bell)
    {
        using StatusLiveRig rig = new();
        rig.Terminal.OutputRedirected = redirected;
        rig.Terminal.Width = 200;
        string? id = vector is "run-dir" ? "20261122-115500-" + value : null;
        StatusLiveRig.Live run = rig.Add("hostile-run", id: id, tweak: d => Poison(vector, value, d));
        _ = rig.Add("healthy-run");
        if (vector is "owner")
        {
            run.Run.PointerDoc["owner_keys"] = new JsonArray(value, StatusLiveRig.Key);
            _ = rig.Idx.WritePointer(run.Run.Pointer, run.Run.PointerDoc.ToJsonString());
        }

        rig.At(TimeSpan.FromSeconds(5), () => rig.End(run, vector is "state" ? "error " + value : "error"));
        rig.At(TimeSpan.FromSeconds(10), () =>
        {
            if (redirected)
            {
                rig.CancelHost();
            }
            else
            {
                rig.Terminal.Press('q');
            }
        });
        string[] args = ["--all", "--watch", .. bell ? new[] { "--bell" } : []];

        StatusLiveRig.Result result = await rig.CallAsync(args);

        Assert.Null(result.Error);
        Assert.Equal(0, result.Code);
        return result;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task On_a_terminal_a_hostile_value_adds_no_escape_sequence_and_no_bell_to_the_framesAsync(string vector)
    {
        StatusLiveRig.Result hostile = await WatchAsync(vector, Hostile, redirected: false, bell: true);
        StatusLiveRig.Result benign = await WatchAsync(vector, Benign, redirected: false, bell: true);

        Assert.Equal(Count(benign.Output, '\u001b'), Count(hostile.Output, '\u001b'));
        Assert.Equal(1, Count(benign.Output, '\a'));
        Assert.Equal(1, Count(hostile.Output, '\a'));
        string stripped = Allowed().Replace(hostile.Output, string.Empty);
        Assert.DoesNotContain('\u001b', stripped);
        Assert.Empty(TerminalAssert.PlainProblems(stripped.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\a", string.Empty, StringComparison.Ordinal)));
        Assert.Equal(benign.Output.Split("\r\n").Length, hostile.Output.Split("\r\n").Length);
        Assert.Equal(string.Empty, hostile.Stderr);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task On_a_terminal_every_frame_has_its_lines_and_none_is_wider_than_the_windowAsync(string vector)
    {
        StatusLiveRig.Result hostile = await WatchAsync(vector, Hostile, redirected: false, bell: false);

        string[] frames = hostile.Output.Split("\u001b[?2026h\u001b[H")[1..];
        Assert.NotEmpty(frames);
        foreach (string frame in frames)
        {
            string[] lines = frame.Split("\u001b[?2026l")[0].Split("\r\n");
            Assert.Equal(4, lines.Length);
            Assert.All(lines, l => Assert.True(StatusTableRows.Columns(Allowed().Replace(l, string.Empty)) <= 200));
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task In_line_mode_a_hostile_value_adds_no_escape_no_bell_and_no_line_even_with_bellAsync(string vector)
    {
        StatusLiveRig.Result hostile = await WatchAsync(vector, Hostile, redirected: true, bell: true);
        StatusLiveRig.Result benign = await WatchAsync(vector, Benign, redirected: true, bell: true);

        Assert.DoesNotContain('\u001b', hostile.Output);
        Assert.DoesNotContain('\a', hostile.Output);
        Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(hostile.Output), b => b is 0x1b or 0x07);
        Assert.Empty(TerminalAssert.PlainProblems(hostile.Output));
        Assert.Equal(3, hostile.Lines.Length);
        Assert.Equal(benign.Lines.Length, hostile.Lines.Length);
        Assert.All(hostile.Lines, l => Assert.Matches(Stamp(), l));
        Assert.Equal(string.Empty, hostile.Stderr);
    }

    [Theory]
    [InlineData(54)]
    [InlineData(80)]
    [InlineData(121)]
    [InlineData(200)]
    public void A_frame_built_from_hostile_rows_has_as_many_escapes_as_one_built_from_harmless_ones(int width)
    {
        string[] hostile = Frame(Hostile, width);
        string[] benign = Frame(Benign, width);

        Assert.Equal(string.Concat(benign).Count(c => c is '\u001b'), string.Concat(hostile).Count(c => c is '\u001b'));
        Assert.Equal(benign.Length, hostile.Length);
        Assert.DoesNotContain('\u001b', Allowed().Replace(string.Concat(hostile), string.Empty));
        Assert.Empty(TerminalAssert.PlainProblems(string.Join('\n', hostile.Select(l => Allowed().Replace(l, string.Empty)))));
        Assert.All(hostile, l => Assert.True(StatusTableRows.Columns(l) <= width));
    }

    private static string[] Frame(string value, int width)
    {
        StatusRow running = StatusTableRows.Running(value, d =>
        {
            d["spec"]!["model"] = value;
            d["spec"]!["runtime"] = value;
            d["spec"]!["profile"] = value;
            d["live"]!["last_tool"] = StatusDocs.Tool(value, "running", StatusDocs.Now);
        }) with
        {
            OwnerKey = value,
            State = value,
        };
        StatusRow ended = StatusTableRows.Ended(value, value);
        StatusRow noStatus = new("/runs/" + value, value, value, Liveness.Unknown, Status: null, Detail: value, StartedAt: null, EndedAt: null, Attention: true, Quiet: null);
        StatusTable.FrameData data = new(value, [running, ended, noStatus], new HashSet<StatusRow>(ReferenceEqualityComparer.Instance) { ended }, Color: true, OwnerColumn: true);

        return [.. StatusTable.Frame(data, StatusDocs.Now, width, 40)];
    }
}
