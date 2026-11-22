namespace LeanWorker;

/// <summary>
/// One process probe: what was found and, when present, the stat line's fields. <see cref="Error"/>
/// says why a failed probe failed.
/// </summary>
internal readonly record struct ProcProbe(ProbeKind Kind, ProcStat Stat, string? Error)
{
    public static ProcProbe Absent { get; } = new(ProbeKind.Absent, Stat: default, Error: null);

    public static ProcProbe Failed(string error) => new(ProbeKind.Failed, Stat: default, error);

    public static ProcProbe Present(ProcStat stat) => new(ProbeKind.Present, stat, Error: null);
}
