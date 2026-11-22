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

    /// <summary>
    /// The pointer file this row was read through (its name in the index); null for a scan or --run.
    /// </summary>
    public string? Pointer { get; init; }

    /// <summary>
    /// The run's first owner key (status.json, else the pointer), already checked; null when unowned.
    /// </summary>
    public string? OwnerKey { get; init; }

    /// <summary>
    /// The run id of the run this one was launched inside (status.json, else the pointer), already
    /// checked; null for a top-level run.
    /// </summary>
    public string? ParentRun { get; init; }

    /// <summary>
    /// The gate chain this round belongs to (for --json): its id, this round, the rounds read and
    /// whether this is the latest; null outside a chain view.
    /// </summary>
    public JsonObject? Chain { get; init; }

    /// <summary>
    /// The nesting level of the row in the listing: 0 for a top-level run.
    /// </summary>
    public int Depth { get; init; }

    public bool Live => Liveness is LeanWorker.Liveness.Alive;

    /// <summary>
    /// Whether the run has ended: its launcher is provably gone.
    /// </summary>
    public bool Ended => Liveness is LeanWorker.Liveness.Gone;

    /// <summary>
    /// The run id: the run dir's name.
    /// </summary>
    public string RunId => Path.GetFileName(Path.TrimEndingDirectorySeparator(Dir));
}
