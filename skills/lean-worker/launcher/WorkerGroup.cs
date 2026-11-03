// The worker's own process group: the worker leads one (Linux, through setsid), so the processes
// it leaves behind can be signalled after it exits. They are reparented to init the moment the
// worker is gone, so a tree kill cannot reach them (issue #116).

namespace LeanWorker;

/// <summary>
/// The process group of the worker: the group it leads and the members of it that were still
/// alive when the worker exited.
/// </summary>
internal sealed class WorkerGroup
{
    /// <summary>
    /// Whether the worker could get a group of its own: Linux with setsid. Without it the worker
    /// runs in the launcher's own group, which is never signalled.
    /// </summary>
    public bool Available { get; init; } = ProcessGroup.Available;

    /// <summary>
    /// The id of the group the worker leads. Zero while it is unknown: the group could not be read.
    /// </summary>
    public int GroupId { get; private set; }

    /// <summary>
    /// The members of the group that were still alive when the worker exited: the processes the
    /// worker left behind. -1 while the group was never established.
    /// </summary>
    public int Leftovers { get; private set; } = -1;

    /// <summary>
    /// The members of the group that were still there after the SIGKILL: zero when none were.
    /// </summary>
    public int Survivors { get; private set; }

    /// <summary>
    /// Reads the group the worker leads from the process it was started as.
    /// </summary>
    public async Task ResolveAsync(int pid)
    {
        if (!Available)
        {
            return;
        }

        GroupId = await ProcessGroup.ResolveAsync(pid).ConfigureAwait(false);
    }

    /// <summary>
    /// The end of the group: the members that are still alive are counted, the group is signalled,
    /// and the ones that survive five seconds are killed.
    /// </summary>
    public async Task FinishAsync()
    {
        if (!Available)
        {
            return;
        }

        Leftovers = await ProcessGroup.FinishAsync(GroupId).ConfigureAwait(false);
        // The members that are still there after the kill: FinishAsync waits for the killed ones to
        // die, so what is left is what the SIGKILL could not end. A group that was never signalled
        // (the launcher's own, or one whose id could not be read) has no survivors to report.
        Survivors = Leftovers > 0 ? ProcessGroup.Alive(GroupId) : 0;
    }
}
