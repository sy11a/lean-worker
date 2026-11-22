namespace LeanWorker;

/// <summary>
/// The launcher's liveness as the status reader proved it: alive, provably gone, or not to be told.
/// </summary>
internal static class Liveness
{
    public const string Alive = "alive";
    public const string Gone = "gone";
    public const string Unknown = "unknown";
}
