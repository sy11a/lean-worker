namespace LeanWorker;

/// <summary>
/// What a stream line says that the live status shows besides usage: a tool call that started, a
/// tool call that finished (with its state), or an error or retry event. Each runtime reads its own
/// events into it (<see cref="IRuntime.Activity"/>).
/// </summary>
internal sealed record StreamActivity(StreamActivityKind Kind, string? Tool, string? CallId, string? State)
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Error = "error";

    /// <summary>
    /// An error or a retry event: nothing displayed changes but the time of the last event.
    /// </summary>
    public static StreamActivity Problem { get; } = new(StreamActivityKind.Problem, Tool: null, CallId: null, State: null);

    public static StreamActivity ToolStart(string tool, string? callId) => new(StreamActivityKind.ToolStart, tool, callId, Running);

    /// <summary>
    /// A finished tool call. <paramref name="tool"/> is null when the event names only the call
    /// (claude's tool_result): it then updates the last tool only when the call is that one.
    /// </summary>
    public static StreamActivity ToolEnd(string? tool, string? callId, string state) => new(StreamActivityKind.ToolEnd, tool, callId, state);
}
