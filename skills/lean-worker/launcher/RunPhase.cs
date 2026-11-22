namespace LeanWorker;

/// <summary>
/// The phases status.json records: the run dir exists (starting), the worker runs (worker), the
/// gate runs (gate, with the round), the launcher decides the worktree and writes the summary
/// (finishing), the run is recorded (done), or the launcher failed with an exception (failed).
/// </summary>
internal static class RunPhase
{
    public const string Starting = "starting";
    public const string Worker = "worker";
    public const string Gate = "gate";
    public const string Finishing = "finishing";
    public const string Done = "done";
    public const string Failed = "failed";
}
