namespace LeanWorker;

/// <summary>
/// The states the status reader derives (never written to a file) besides the ledger's statuses.
/// </summary>
internal static class StatusStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string WrapUp = "wrap-up";
    public const string Gate = "gate";
    public const string Finishing = "finishing";
    public const string Ending = "ending";
    public const string Failed = "failed";
    public const string Orphaned = "orphaned";
    public const string Lost = "lost";
    public const string Unknown = "unknown";
    public const string Unreadable = "unreadable";
    public const string Legacy = "legacy";

    /// <summary>
    /// A run whose pointer names a run dir (or a status.json) that is no longer there.
    /// </summary>
    public const string Gone = "gone";

    /// <summary>
    /// A run whose pointer names the same run dir as another pointer: shown, never trusted.
    /// </summary>
    public const string SharedDir = "shared-dir";
}
