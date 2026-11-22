using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The status file driven with the lines of an opencode stream: one API call and one turn per
/// step_finish, the last tool from tool_use events (opencode emits them on completion or error), an
/// error event as a write that changes nothing displayed but the time of the last event.
/// </summary>
public sealed class RunStatusOpencodeStreamTests : IDisposable
{
    private const string StepStart = """{"type":"step_start","sessionID":"ses_1","part":{"id":"s1","type":"step-start"}}""";

    private const string Text = """{"type":"text","sessionID":"ses_1","part":{"messageID":"a","text":"thinking aloud"}}""";

    private const string ToolDone = """{"type":"tool_use","sessionID":"ses_1","part":{"id":"p9","callID":"call_1","tool":"bash","state":{"status":"completed","input":{"command":"ls"}}}}""";

    private const string StepFinish = """{"type":"step_finish","sessionID":"ses_1","part":{"id":"p1","type":"step-finish","reason":"tool-calls","tokens":{"input":721,"output":2343,"reasoning":445,"cache":{"write":0,"read":149184}},"cost":0}}""";

    private const string Error = """{"type":"error","sessionID":"ses_1","error":{"name":"APIError","data":{"message":"overloaded"}}}""";

    private readonly StatusRig _rig = new(new OpencodeRuntime());

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void A_whole_stream_moves_calls_turns_tokens_cost_and_the_last_finished_tool()
    {
        _rig.Feed(StepStart);
        _rig.Feed(Text);
        Assert.Equal(1, _rig.Status.Seq);

        _rig.Clock.Advance(TimeSpan.FromSeconds(3));
        _rig.Feed(ToolDone);
        JsonObject live = _rig.Live();
        Assert.Equal(2, _rig.Status.Seq);
        Assert.Equal("bash", live["last_tool"]!["name"]!.GetValue<string>());
        Assert.Equal("completed", live["last_tool"]!["state"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:03.000Z", live["last_tool"]!["at"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:03.000Z", live["last_event_at"]!.GetValue<string>());
        Assert.Equal(0, live["api_calls"]!.GetValue<int>());

        _rig.Feed(StepFinish);
        live = _rig.Live();
        Assert.Equal(3, _rig.Status.Seq);
        Assert.Equal(1, live["api_calls"]!.GetValue<int>());
        Assert.Equal(1, live["turns"]!.GetValue<long>());
        Assert.Equal(721, live["tokens"]!["input"]!.GetValue<long>());
        Assert.Equal(0, live["tokens"]!["cache_write"]!.GetValue<long>());
        Assert.Equal(149184, live["tokens"]!["cache_read"]!.GetValue<long>());
        Assert.Equal(2343 + 445, live["tokens"]!["output"]!.GetValue<long>());
        Assert.Equal(445, live["tokens"]!["thinking"]!.GetValue<long>());
        Assert.Equal(21.2154m, live["cost_usd"]!.GetValue<decimal>());
    }

    [Fact]
    public void Each_step_finish_with_a_new_id_is_one_more_call_and_one_more_turn()
    {
        _rig.Feed(StepFinish);

        _rig.Feed(StepFinish.Replace("\"p1\"", "\"p2\"", StringComparison.Ordinal));

        JsonObject live = _rig.Live();
        Assert.Equal(2, live["api_calls"]!.GetValue<int>());
        Assert.Equal(2, live["turns"]!.GetValue<long>());
    }

    [Fact]
    public void A_failed_tool_call_is_the_last_tool_with_state_error()
    {
        _rig.Feed(ToolDone);

        _rig.Feed(ToolDone.Replace("\"completed\"", "\"error\"", StringComparison.Ordinal).Replace("\"tool\":\"bash\"", "\"tool\":\"edit\"", StringComparison.Ordinal));

        JsonObject tool = _rig.Live()["last_tool"]!.AsObject();
        Assert.Equal("edit", tool["name"]!.GetValue<string>());
        Assert.Equal("error", tool["state"]!.GetValue<string>());
    }

    [Fact]
    public void An_error_event_writes_and_moves_the_time_of_the_last_event_only()
    {
        _rig.Feed(ToolDone);
        long seq = _rig.Status.Seq;
        _rig.Clock.Advance(TimeSpan.FromSeconds(20));

        _rig.Feed(Error);

        JsonObject live = _rig.Live();
        Assert.Equal(seq + 1, _rig.Status.Seq);
        Assert.Equal("2026-11-22T12:00:20.000Z", live["last_event_at"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:00.000Z", live["last_tool"]!["at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(StepStart)]
    [InlineData(Text)]
    public void Events_that_show_nothing_new_cause_no_write(string line)
    {
        long seq = _rig.Status.Seq;

        _rig.Feed(line);

        Assert.Equal(seq, _rig.Status.Seq);
    }

}
