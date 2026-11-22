namespace LeanWorker;

/// <summary>
/// What a /proc probe found: the entry, a provable absence, or a failure to tell.
/// </summary>
internal enum ProbeKind
{
    Present,
    Absent,
    Failed,
}
