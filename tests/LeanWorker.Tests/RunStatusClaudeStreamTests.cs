using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The status file driven with the lines of a claude stream: the counters, the last tool (name and
/// state) and the time of the last event. Event shapes are those of ClaudeRuntime's own parser and of
/// the recorded streams under .lean-worker/runs.
/// </summary>
public sealed class RunStatusClaudeStreamTests : IDisposable
{
    private const string Init = """{"type":"system","subtype":"init","session_id":"s1","tools":["Bash"],"model":"m"}""";

    private const string MessageStart = """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"m","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0}}}}""";

    private const string TextDelta = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}}""";

    private const string AssistantToolUse = """{"type":"assistant","message":{"id":"m1","model":"m","usage":{"input_tokens":1000,"output_tokens":3,"cache_read_input_tokens":9200,"cache_creation_input_tokens":0},"content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"ls"}}]}}""";

    private const string ToolResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"ok","is_error":false}]}}""";

    private const string MessageDelta = """{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":50}}}""";

    private const string Result = """{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":2,"permission_denials":[],"usage":{"output_tokens":50,"output_tokens_details":{"thinking_tokens":7}}}""";

    private readonly StatusRig _rig = new(new ClaudeRuntime());

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void A_whole_stream_moves_calls_tokens_cost_tool_and_turns_as_the_design_says()
    {
        _rig.Feed(Init);
        Assert.Equal(1, _rig.Status.Seq);

        _rig.Feed(MessageStart);
        JsonObject live = _rig.Live();
        Assert.Equal(2, _rig.Status.Seq);
        Assert.Equal(1, live["api_calls"]!.GetValue<int>());
        Assert.Equal(1000, live["tokens"]!["input"]!.GetValue<long>());
        Assert.Equal(9200, live["tokens"]!["cache_read"]!.GetValue<long>());
        Assert.Equal(1.92m, live["cost_usd"]!.GetValue<decimal>());
        Assert.Null(live["turns"]);
        Assert.Null(live["last_tool"]);

        _rig.Feed(TextDelta);
        Assert.Equal(2, _rig.Status.Seq);

        _rig.Feed(AssistantToolUse);
        live = _rig.Live();
        Assert.Equal(3, _rig.Status.Seq);
        Assert.Equal(1, live["api_calls"]!.GetValue<int>());
        Assert.Equal("Bash", live["last_tool"]!["name"]!.GetValue<string>());
        Assert.Equal("running", live["last_tool"]!["state"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:00.000Z", live["last_tool"]!["at"]!.GetValue<string>());

        _rig.Clock.Advance(TimeSpan.FromSeconds(4));
        _rig.Feed(ToolResult);
        live = _rig.Live();
        Assert.Equal(4, _rig.Status.Seq);
        Assert.Equal("Bash", live["last_tool"]!["name"]!.GetValue<string>());
        Assert.Equal("completed", live["last_tool"]!["state"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:04.000Z", live["last_tool"]!["at"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:04.000Z", live["last_event_at"]!.GetValue<string>());

        _rig.Feed(MessageDelta);
        live = _rig.Live();
        Assert.Equal(5, _rig.Status.Seq);
        Assert.Equal(1, live["api_calls"]!.GetValue<int>());
        Assert.Equal(50, live["tokens"]!["output"]!.GetValue<long>());
        Assert.Equal(2.02m, live["cost_usd"]!.GetValue<decimal>());
        Assert.Null(live["turns"]);
    }

    [Fact]
    public void Turns_exist_only_from_the_result_line_and_the_result_line_itself_writes_nothing()
    {
        _rig.Feed(MessageStart);
        _rig.Feed(MessageDelta);
        long seq = _rig.Status.Seq;

        _rig.Feed(Result);

        Assert.Equal(seq, _rig.Status.Seq);
        Assert.Equal(2, _rig.Status.Snapshot()["live"]!["turns"]!.GetValue<long>());
        Assert.Equal(7, _rig.Status.Snapshot()["live"]!["tokens"]!["thinking"]!.GetValue<long>());
        _rig.Status.Finish("success", 0);
        Assert.Equal(2, _rig.Live()["turns"]!.GetValue<long>());
    }

    [Fact]
    public void One_call_reported_several_times_counts_once_and_a_new_message_id_is_a_second_call()
    {
        _rig.Feed(MessageStart);
        _rig.Feed(AssistantToolUse);
        _rig.Feed(MessageDelta);
        _rig.Feed(MessageStart);
        Assert.Equal(1, _rig.Live()["api_calls"]!.GetValue<int>());

        _rig.Feed(MessageStart.Replace("\"m1\"", "\"m2\"", StringComparison.Ordinal));

        Assert.Equal(2, _rig.Live()["api_calls"]!.GetValue<int>());
    }

    [Fact]
    public void A_failed_tool_result_marks_the_tool_error()
    {
        _rig.Feed(AssistantToolUse);

        _rig.Feed(ToolResult.Replace("\"is_error\":false", "\"is_error\":true", StringComparison.Ordinal));

        Assert.Equal("error", _rig.Live()["last_tool"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_tool_result_of_another_call_leaves_the_last_tool_as_it_was()
    {
        _rig.Feed(AssistantToolUse);
        _rig.Clock.Advance(TimeSpan.FromSeconds(9));

        _rig.Feed(ToolResult.Replace("toolu_01", "toolu_99", StringComparison.Ordinal));

        JsonObject tool = _rig.Live()["last_tool"]!.AsObject();
        Assert.Equal("running", tool["state"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:00.000Z", tool["at"]!.GetValue<string>());
    }

    [Fact]
    public void Of_several_tool_calls_in_one_message_the_last_named_is_the_last_tool()
    {
        string two = AssistantToolUse.Replace(
            "\"input\":{\"command\":\"ls\"}}]",
            "\"input\":{}},{\"type\":\"tool_use\",\"id\":\"toolu_02\",\"name\":\"Read\",\"input\":{}}]",
            StringComparison.Ordinal);

        _rig.Feed(two);

        Assert.Equal("Read", _rig.Live()["last_tool"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void An_api_retry_event_writes_and_moves_the_time_of_the_last_event_only()
    {
        _rig.Feed(AssistantToolUse);
        long seq = _rig.Status.Seq;
        _rig.Clock.Advance(TimeSpan.FromSeconds(12));

        _rig.Feed("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":5,"retry_delay_ms":1000,"error_status":529,"error":"overloaded"}""");

        JsonObject live = _rig.Live();
        Assert.Equal(seq + 1, _rig.Status.Seq);
        Assert.Equal("2026-11-22T12:00:12.000Z", live["last_event_at"]!.GetValue<string>());
        Assert.Equal("2026-11-22T12:00:00.000Z", live["last_tool"]!["at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"status","status":"requesting"}""")]
    [InlineData("""{"type":"system","subtype":"thinking_tokens"}""")]
    [InlineData("""{"type":"assistant","message":{"id":"m1","content":[{"type":"thinking","thinking":"..."}]}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"a plain prompt"}}""")]
    public void Other_events_that_show_nothing_new_cause_no_write(string line)
    {
        long seq = _rig.Status.Seq;

        _rig.Feed(line);

        Assert.Equal(seq, _rig.Status.Seq);
    }

    [Fact]
    public void The_wrap_up_flag_is_written_on_the_line_that_crosses_the_share()
    {
        using StatusRig rig = new(new ClaudeRuntime(), budget: 4m, wrapUpAt: 0.5m);
        rig.Feed(MessageStart);
        Assert.False(rig.Live()["wrapped_up"]!.GetValue<bool>());

        rig.Feed(MessageDelta);

        Assert.True(rig.Live()["wrapped_up"]!.GetValue<bool>());
        Assert.Equal(2.02m, rig.Live()["cost_usd"]!.GetValue<decimal>());
    }
}
