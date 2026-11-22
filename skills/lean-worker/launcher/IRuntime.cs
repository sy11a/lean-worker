using System.Text.Json.Nodes;

namespace LeanWorker;

internal interface IRuntime
{
    string Name { get; }
    Prepared Prepare(RunSpec s);

    /// <summary>
    /// Reads one stream line; returns the API call's usage if the line carries one.
    /// </summary>
    Usage? Parse(JsonObject line, Outcome o);

    /// <summary>
    /// Settles the outcome after the process exits.
    /// </summary>
    void Finish(Outcome o, int exitCode);

    /// <summary>
    /// Whether a stream line is kept in stream.jsonl (token-by-token deltas are not).
    /// </summary>
    bool Record(string line) => true;

    /// <summary>
    /// What a stream line says for the live status besides usage (a tool call started or finished,
    /// an error or retry event); null when it says nothing of that kind.
    /// </summary>
    StreamActivity? Activity(JsonObject line) => null;

    /// <summary>
    /// The runtime's own turn count while the run goes on; null while the runtime has none yet.
    /// </summary>
    long? LiveTurns(Outcome o) => o.Turns;
}