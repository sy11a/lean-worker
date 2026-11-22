namespace LeanWorker;

/// <summary>
/// The verdict over a run that ended without an end record or a summary: orphaned, lost or unknown,
/// with the detail the row shows.
/// </summary>
internal readonly record struct GroupVerdict(string State, string Detail);
