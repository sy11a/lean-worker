using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// One run as the reader sees it: its directory, the derived state and liveness, the status file's
/// content (null for legacy and unreadable rows), the detail shown at the end of the line, when the
/// run started and (for an ended run) when it ended, whether it needs attention, and the quiet age
/// of a live run that has been silent too long.
/// </summary>
internal sealed record StatusRow(
    string Dir,
    string Name,
    string State,
    string Liveness,
    JsonObject? Status,
    string Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    bool Attention,
    TimeSpan? Quiet)
{
    /// <summary>
    /// The summary's status of a legacy row (for --json).
    /// </summary>
    public string? SummaryStatus { get; init; }

    public bool Live => Liveness is LeanWorker.Liveness.Alive;

    /// <summary>
    /// Whether the run has ended: its launcher is provably gone.
    /// </summary>
    public bool Ended => Liveness is LeanWorker.Liveness.Gone;
}
