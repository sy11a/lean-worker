using System.Diagnostics;

namespace LeanWorker;

internal sealed record Prepared(
    string Executable,
    List<string> Args,
    Dictionary<string, string?> Env,
    string CommandText,
    string? ScratchDirectory,
    string? WorkingDirectory = null)
{
    /// <summary>
    /// The worker's own process group: the worker leads one (Linux, through setsid), so the
    /// processes it leaves behind can be signalled after it exits — they are reparented to init
    /// then and out of a tree kill's reach (issue #116). Without setsid there is no group of its
    /// own, and the run says so.
    /// </summary>
    public WorkerGroup Group { get; init; } = new();

    /// <summary>
    /// The processes of the worker's group that were still alive when the worker exited; -1 when
    /// there was no group of its own or its id could not be read.
    /// </summary>
    public int Leftovers => Group.Leftovers;

    /// <summary>
    /// The note of a run whose worker had no process group of its own (or whose group could not
    /// be read): null when the group worked.
    /// </summary>
    public string? GroupNote()
    {
        if (!Group.Available)
        {
            return ProcessGroup.UnavailableNote;
        }

        return Group.Leftovers < 0 ? ProcessGroup.UnknownNote : null;
    }

    /// <summary>
    /// Puts the worker into a process group of its own: setsid makes it a session (and group)
    /// leader, and --wait keeps the launcher's own exit code the worker's.
    /// </summary>
    public void ApplyGroup(ProcessStartInfo psi)
    {
        if (!Group.Available)
        {
            return;
        }

        psi.FileName = ProcessGroup.SetsidPath;
        psi.ArgumentList.Insert(0, Executable);
        psi.ArgumentList.Insert(0, "--wait");
    }

    /// <summary>
    /// Reads the group the worker leads, from the process it was started as.
    /// </summary>
    public async Task ResolveGroupAsync(int pid) =>
        await Group.ResolveAsync(pid).ConfigureAwait(false);

    /// <summary>
    /// Signals the group after the worker exited.
    /// </summary>
    public async Task FinishGroupAsync() =>
        await Group.FinishAsync().ConfigureAwait(false);
}
