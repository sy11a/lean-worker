using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A status problem never touches the run: driven as the launcher drives it (the runtime reads a
/// stream line, the Meter prices it, then the writer sees it), no public method of the writer throws
/// for a hostile stream line, a runtime that throws, a token sum that overflows, a message with a lone
/// surrogate or a run dir that is unwritable or gone; a failure leaves exactly one note, later lines
/// are still processed and the file, when it is written, still parses.
/// </summary>
public sealed class RunStatusBestEffortTests : IDisposable
{
    private const string GoodOpencodeTool = """{"type":"tool_use","part":{"tool":"good","callID":"c-good","state":{"status":"completed"}}}""";

    private const string GoodClaudeTool = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_good","name":"good","input":{}}]}}""";

    private readonly List<IDisposable> _rigs = [];

    public void Dispose()
    {
        foreach (IDisposable rig in _rigs)
        {
            rig.Dispose();
        }
    }

    private StatusRig Rig(IRuntime runtime)
    {
        StatusRig rig = new(runtime);
        _rigs.Add(rig);
        return rig;
    }

    /// <summary>
    /// A runtime whose status reads throw (an exception whose message carries text that must not reach the note).
    /// </summary>
    private sealed class ThrowingRuntime(bool activity, bool turns) : IRuntime
    {
        public const string Secret = "SECRET-FROM-THE-STREAM";

        public string Name => "throwing";

        public Prepared Prepare(RunSpec s) => throw new NotSupportedException();

        public Usage? Parse(JsonObject line, Outcome o) => null;

        public void Finish(Outcome o, int exitCode)
        {
        }

        public StreamActivity? Activity(JsonObject line) => activity ? throw new InvalidOperationException(Secret) : null;

        public long? LiveTurns(Outcome o) => turns ? throw new InvalidOperationException(Secret) : null;
    }

    private static JsonObject Line(string json) => JsonNode.Parse(json)!.AsObject();

    private static List<string> Notes(RunStatus status)
    {
        List<string> notes = [];
        status.AddNoteTo(notes);
        status.AddNoteTo(notes);
        return notes;
    }

    public static TheoryData<string> OpencodeHostileLines
    {
        get
        {
            return
            [
                """{"type":"tool_use","part":{"tool":"Ba\ud800sh","callID":"c1","state":{"status":"completed"}}}""",
                """{"type":"tool_use","part":{"tool":"bash","callID":"\udc00","state":{"status":"completed"}}}""",
                """{"type":"tool_use","part":{"tool":"bash","callID":"c1","state":{"status":"\ud800"}}}""",
                """{"type":"tool_use","part":{"tool":"\ud83d","callID":"\ude00","state":{"status":"\udfff"}}}""",
                """{"type":"tool_use","part":[1,2]}""",
                """{"type":"tool_use","part":null}""",
                """{"type":"tool_use"}""",
                """{"type":"tool_use","part":{"tool":5,"callID":{},"state":[1]}}""",
                """{"type":"tool_use","part":{"tool":["a"],"callID":7,"state":{"status":5}}}""",
                """{"type":"tool_use","part":{"tool":"bash","callID":"c","state":"completed"}}""",
                """{"type":5}""",
                """{"type":["tool_use"],"part":{}}""",
                """{"type":"error","error":"\ud800"}""",
                """{"type":"step_finish","part":{"tokens":"x","cost":[1]}}""",
            ];
        }
    }

    public static TheoryData<string> ClaudeHostileLines
    {
        get
        {
            return
            [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Ba\ud800sh","input":{}}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"\udc00","name":"Bash","input":{}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"\ud800","is_error":true}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":"\ud800"}]}}""",
                """{"type":"assistant","message":[1,2,3]}""",
                """{"type":"assistant","message":{"content":{"a":1}}}""",
                """{"type":"assistant","message":{"content":"text"}}""",
                """{"type":"assistant","message":{"content":[null,5,"s",[],{"type":5},{"type":"tool_use","name":[1],"id":{}}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","name":{"a":1},"id":[1]}]}}""",
                """{"type":"system","subtype":["api_retry"]}""",
                """{"type":"system","subtype":"\ud800"}""",
            ];
        }
    }

    private static void AssertSurvived(StatusRig rig, string goodLine)
    {
        // The writer is still alive: the next good line is processed and the file parses.
        rig.Status.OnLine(rig.Runtime, Line(goodLine), usage: false);

        JsonObject live = rig.Live();
        Assert.Equal("good", live["last_tool"]!["name"]!.GetValue<string>());
        Assert.True(Notes(rig.Status).Count <= 1);
    }

    [Theory]
    [MemberData(nameof(OpencodeHostileLines))]
    public void A_hostile_opencode_line_never_throws_and_later_lines_are_still_processed(string line)
    {
        StatusRig rig = Rig(new OpencodeRuntime());

        Exception? thrown = Record.Exception(() =>
        {
            rig.Status.OnLine(rig.Runtime, Line(line), usage: false);
            rig.Status.OnLine(rig.Runtime, Line(line), usage: true);
        });

        Assert.Null(thrown);
        AssertSurvived(rig, GoodOpencodeTool);
    }

    [Theory]
    [MemberData(nameof(ClaudeHostileLines))]
    public void A_hostile_claude_line_never_throws_and_later_lines_are_still_processed(string line)
    {
        StatusRig rig = Rig(new ClaudeRuntime());

        Exception? thrown = Record.Exception(() =>
        {
            rig.Status.OnLine(rig.Runtime, Line(line), usage: false);
            rig.Status.OnLine(rig.Runtime, Line(line), usage: true);
        });

        Assert.Null(thrown);
        AssertSurvived(rig, GoodClaudeTool);
    }

    [Theory]
    [InlineData("""{"type":"tool_use","part":{"tool":"Ba\ud800sh","callID":"c1","state":{"status":"completed"}}}""")]
    [InlineData("""{"type":"tool_use","part":{"tool":"bash","callID":"\udc00","state":{"status":"completed"}}}""")]
    [InlineData("""{"type":"tool_use","part":{"tool":"bash","callID":"c1","state":{"status":"\ud800"}}}""")]
    public void A_lone_surrogate_in_a_tool_name_id_or_state_leaves_at_most_one_note_and_no_stream_text_in_it(string line)
    {
        StatusRig rig = Rig(new OpencodeRuntime());

        for (int i = 0; i < 5; i++)
        {
            rig.Status.OnLine(rig.Runtime, Line(line), usage: false);
        }

        List<string> notes = Notes(rig.Status);
        Assert.True(notes.Count <= 1);
        Assert.All(notes, n => Assert.DoesNotContain(n, c => char.IsSurrogate(c)));
        Assert.All(notes, n => Assert.DoesNotContain("ud800", n, StringComparison.Ordinal));
        string text = File.ReadAllText(Path.Combine(rig.RunDir, RunStatus.FileName));
        Assert.DoesNotContain(text, c => char.IsSurrogate(c));
        _ = JsonNode.Parse(text);
    }

    [Fact]
    public void A_runtime_whose_activity_throws_never_reaches_the_caller_and_leaves_exactly_one_note()
    {
        StatusRig rig = Rig(new ThrowingRuntime(activity: true, turns: false));

        Exception? thrown = Record.Exception(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                rig.Status.OnLine(rig.Runtime, Line("""{"type":"x"}"""), usage: i % 2 is 0);
            }
        });

        Assert.Null(thrown);
        string note = Assert.Single(Notes(rig.Status));
        Assert.DoesNotContain(ThrowingRuntime.Secret, note, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", note, StringComparison.Ordinal);
        Assert.Equal(note, rig.Status.Note);
    }

    [Fact]
    public void After_a_runtime_that_threw_the_next_lines_are_processed_and_the_file_parses()
    {
        StatusRig rig = Rig(new ThrowingRuntime(activity: true, turns: false));
        rig.Status.OnLine(rig.Runtime, Line("""{"type":"x"}"""), usage: false);
        long seq = rig.Status.Seq;

        rig.Status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: false);

        Assert.Equal(seq + 1, rig.Status.Seq);
        Assert.Equal("Bash", rig.Live()["last_tool"]!["name"]!.GetValue<string>());
        _ = Assert.Single(Notes(rig.Status));
    }

    [Fact]
    public void A_runtime_whose_live_turns_throws_never_reaches_the_caller_and_leaves_exactly_one_note()
    {
        StatusRig rig = Rig(new ThrowingRuntime(activity: false, turns: true));

        Exception? thrown = Record.Exception(() =>
        {
            rig.Status.Phase(RunPhase.Worker);
            rig.Status.WorkerStarted(4000, 4000);
            rig.Status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: true);
            rig.Status.OnLine(activity: null, usage: true);
            rig.Status.GateStarted(1);
            rig.Status.Finishing();
            rig.Status.Finish("success", 0);
            rig.Status.Fail("boom", summaryStatus: null);
        });

        Assert.Null(thrown);
        string note = Assert.Single(Notes(rig.Status));
        Assert.DoesNotContain(ThrowingRuntime.Secret, note, StringComparison.Ordinal);
        string path = Path.Combine(rig.RunDir, RunStatus.FileName);
        Assert.True(!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not null);
    }

    [Fact]
    public void Token_counts_whose_sum_overflows_never_reach_the_caller_and_leave_exactly_one_note()
    {
        StatusRig rig = Rig(new OpencodeRuntime());
        _ = rig.Meter.Add(new Usage("a", "m", long.MaxValue, 0, 0, 0, 0, 0));
        _ = rig.Meter.Add(new Usage("b", "m", long.MaxValue, 0, 0, 0, 0, 0));

        Exception? thrown = Record.Exception(() =>
        {
            rig.Status.OnLine(activity: null, usage: true);
            rig.Status.OnLine(StreamActivity.ToolStart("Bash", "c1"), usage: true);
            rig.Status.Phase(RunPhase.Gate, 1);
            rig.Status.Finishing();
            rig.Status.Finish("success", 0);
            rig.Status.Fail("boom", "success");
        });

        Assert.Null(thrown);
        string note = Assert.Single(Notes(rig.Status));
        Assert.DoesNotContain("SECRET", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_dir_that_vanishes_leaves_exactly_one_note_and_the_writer_recovers_when_it_is_back()
    {
        StatusRig rig = Rig(new OpencodeRuntime());
        Directory.Delete(rig.RunDir, recursive: true);

        Exception? thrown = Record.Exception(() =>
        {
            rig.Status.Phase(RunPhase.Worker);
            rig.Status.OnLine(rig.Runtime, Line(GoodOpencodeTool), usage: false);
            rig.Status.OnLine(StreamActivity.Problem, usage: false);
            rig.Status.Finishing();
        });
        _ = Directory.CreateDirectory(rig.RunDir);
        rig.Status.OnLine(rig.Runtime, Line(GoodOpencodeTool.Replace("good", "later", StringComparison.Ordinal)), usage: false);

        Assert.Null(thrown);
        _ = Assert.Single(Notes(rig.Status));
        Assert.Equal("later", rig.Live()["last_tool"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void A_directory_in_the_place_of_status_json_leaves_exactly_one_note_and_never_throws()
    {
        StatusRig rig = Rig(new OpencodeRuntime());
        string path = Path.Combine(rig.RunDir, RunStatus.FileName);
        File.Delete(path);
        _ = Directory.CreateDirectory(path);

        Exception? thrown = Record.Exception(() =>
        {
            rig.Status.OnLine(rig.Runtime, Line(GoodOpencodeTool), usage: false);
            rig.Status.OnLine(StreamActivity.Problem, usage: false);
            rig.Status.Finish("success", 0);
            rig.Status.Fail("boom", summaryStatus: null);
        });

        Assert.Null(thrown);
        _ = Assert.Single(Notes(rig.Status));
        Assert.True(Directory.Exists(path));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(rig.RunDir), e => e != path);
    }

    [Fact]
    public void Fail_with_lone_surrogates_in_its_text_never_throws_and_the_file_still_parses()
    {
        StatusRig rig = Rig(new OpencodeRuntime());

        Exception? thrown = Record.Exception(() => rig.Status.Fail("boom \ud800 and \udc00 end\ud83d", "succ\udc00ess"));

        Assert.Null(thrown);
        JsonObject status = rig.Disk();
        Assert.Equal("failed", status["phase"]!.GetValue<string>());
        JsonObject end = status["end"]!.AsObject();
        Assert.Equal("launch-failed", end["status"]!.GetValue<string>());
        string error = end["error"]!.GetValue<string>();
        Assert.StartsWith("boom ", error, StringComparison.Ordinal);
        Assert.Contains(" end", error, StringComparison.Ordinal);
        Assert.DoesNotContain(error, c => char.IsSurrogate(c));
        Assert.DoesNotContain(end["summary_status"]!.GetValue<string>(), c => char.IsSurrogate(c));
        Assert.Empty(Notes(rig.Status));
    }

    [Fact]
    public void Fail_with_a_run_dir_that_is_gone_never_throws_and_leaves_one_note()
    {
        StatusRig rig = Rig(new OpencodeRuntime());
        Directory.Delete(rig.RunDir, recursive: true);

        Exception? thrown = Record.Exception(() => rig.Status.Fail("boom \ud800", "success"));

        Assert.Null(thrown);
        _ = Assert.Single(Notes(rig.Status));
    }

    private static string OpencodeTool(string? status)
    {
        string state = status is null ? string.Empty : ",\"state\":{\"status\":\"" + status + "\"}";
        return "{\"type\":\"tool_use\",\"part\":{\"tool\":\"bash\",\"callID\":\"c1\"" + state + "}}";
    }

    private static JsonNode? StoredState(StatusRig rig, string? status)
    {
        rig.Status.OnLine(rig.Runtime, Line(OpencodeTool(status)), usage: false);
        return rig.Live()["last_tool"]!["state"];
    }

    [Theory]
    [InlineData("pending", "running")]
    [InlineData("running", "running")]
    [InlineData("completed", "completed")]
    [InlineData("error", "error")]
    public void An_opencode_tool_state_is_stored_as_running_completed_or_error(string given, string stored)
    {
        StatusRig rig = Rig(new OpencodeRuntime());

        Assert.Equal(stored, StoredState(rig, given)!.GetValue<string>());
    }

    [Theory]
    [InlineData("weird")]
    [InlineData("RUNNING")]
    [InlineData("Completed")]
    [InlineData("completed ")]
    [InlineData("")]
    [InlineData("\\u001b[2Jcompleted")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void An_unknown_opencode_tool_state_is_never_stored_as_given(string given)
    {
        StatusRig rig = Rig(new OpencodeRuntime());

        JsonNode? stored = StoredState(rig, given);

        Assert.True(stored is null || stored.GetValue<string>() is "running" or "completed" or "error", stored?.ToJsonString());
        string raw = File.ReadAllText(Path.Combine(rig.RunDir, RunStatus.FileName));
        Assert.DoesNotContain("weird", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_event_without_a_state_is_stored_as_one_of_the_three_or_as_null()
    {
        StatusRig rig = Rig(new OpencodeRuntime());

        JsonNode? stored = StoredState(rig, status: null);

        Assert.True(stored is null || stored.GetValue<string>() is "running" or "completed" or "error", stored?.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("completed")]
    [InlineData("error")]
    [InlineData("weird")]
    [InlineData("")]
    [InlineData("RUNNING")]
    [InlineData("\u001b[2J")]
    public void The_state_a_stream_activity_is_stored_with_is_running_completed_error_or_null_never_the_given_text(string? given)
    {
        string? stored = RunStatus.ToolState(given);

        Assert.True(stored is null or "running" or "completed" or "error", stored);
        Assert.True(given is null or "running" or "completed" or "error" || !string.Equals(given, stored, StringComparison.Ordinal));
    }

    [Fact]
    public void A_tool_start_from_claude_is_running_and_a_failed_result_is_error()
    {
        StatusRig rig = Rig(new ClaudeRuntime());

        rig.Status.OnLine(rig.Runtime, Line(GoodClaudeTool), usage: false);
        string started = rig.Live()["last_tool"]!["state"]!.GetValue<string>();
        rig.Status.OnLine(rig.Runtime, Line("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_good","is_error":true}]}}"""), usage: false);
        string ended = rig.Live()["last_tool"]!["state"]!.GetValue<string>();

        Assert.Equal("running", started);
        Assert.Equal("error", ended);
    }
}
