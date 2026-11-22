namespace LeanWorker;

/// <summary>
/// The kinds of <see cref="StreamActivity"/>: a tool call started, a tool call finished, an error or retry event.
/// </summary>
internal enum StreamActivityKind
{
    ToolStart,
    ToolEnd,
    Problem,
}
