// The process group of the worker: setsid gives the worker a session (and with it a process group)
// of its own, and the launcher signals that group after the worker exits.

using System.Globalization;

namespace LeanWorker;

/// <summary>
/// The process group of the worker: setsid makes the worker the leader of a group of its own, and
/// the members of that group are signalled after the worker exits — they are reparented to init
/// then and out of a tree kill's reach (issue #116).
/// </summary>
internal static class ProcessGroup
{
    /// <summary>
    /// setsid by its full path: a bare name would be looked up in the PATH, which the worker and
    /// the profile's PATH can change.
    /// </summary>
    internal const string SetsidPath = "/usr/bin/setsid";

    /// <summary>
    /// The note of a run whose worker could not get a process group of its own: whatever the
    /// worker leaves behind stays behind.
    /// </summary>
    internal const string UnavailableNote = "process-group: unavailable";

    /// <summary>
    /// The note of a run whose worker's group could not be read: whatever the worker leaves
    /// behind stays behind.
    /// </summary>
    internal const string UnknownNote = "process-group: unknown";

    internal const int Sigkill = 9;
    internal const int Sigterm = 15;

    /// <summary>
    /// The number of checks of a group that did not go on SIGTERM: fifty steps of a tenth of a
    /// second are five seconds.
    /// </summary>
    private const int WaitSteps = 50;

    /// <summary>
    /// The wait between the two checks of a group that did not go on SIGTERM.
    /// </summary>
    private static readonly TimeSpan _waitStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Whether the worker can get a process group of its own: Linux with setsid in /usr/bin.
    /// </summary>
    public static bool Available => !OperatingSystem.IsWindows() && File.Exists(SetsidPath);

    /// <summary>
    /// The group the worker leads: the id of the group it is in. It is read from /proc, so it is
    /// the group the worker really is in. Zero when it cannot be told.
    /// </summary>
    public static async Task<int> ResolveAsync(int pid)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ProcStat? stat = Stat(pid);
            if (stat is not null)
            {
                // The worker leads its own group: the group is the worker's own id.
                if (stat.Value.Pgrp == pid)
                {
                    return pid;
                }

                // setsid forked (it waits for the program in a child of its own): the group is the
                // one the child leads.
                int child = ChildGroup(pid);
                if (child > 0)
                {
                    return child;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// The end of the worker's group: the members that are still alive are counted, the group gets
    /// a SIGTERM, and the members that are still there after five seconds get a SIGKILL. The count
    /// is what the run records: the processes the worker left behind. -1 when the group is not
    /// known (or is the launcher's own group, which is never signalled).
    /// </summary>
    public static async Task<int> FinishAsync(int pgid)
    {
        // A group that cannot be told apart from the launcher's own is never signalled: a wrong id
        // would kill the launcher itself.
        if (pgid <= 1 || pgid == OwnGroup())
        {
            return -1;
        }

        int alive = Alive(pgid);
        if (alive is 0)
        {
            return alive;
        }

        Signal(pgid, Sigterm);
        for (int step = 0; step < WaitSteps && Alive(pgid) > 0; step++)
        {
            await Task.Delay(_waitStep, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        }

        if (Alive(pgid) > 0)
        {
            Signal(pgid, Sigkill);
        }

        return alive;
    }

    /// <summary>
    /// The processes of a group that are still alive: every process whose stat line names the
    /// group, except the zombies (they are gone; only their entry waits for the parent to reap
    /// them).
    /// </summary>
    public static int Alive(int pgid)
    {
        int n = 0;
        foreach (string dir in ProcDirs())
        {
            if (!TryPid(dir, out int pid))
            {
                continue;
            }

            ProcStat? stat = Stat(pid);
            if (stat is null || stat.Value.Pgrp != pgid || stat.Value.State is 'Z')
            {
                continue;
            }

            n++;
        }

        return n;
    }

    /// <summary>
    /// A signal to a whole group: the negative pid addresses the group, not one process.
    /// </summary>
    private static void Signal(int pgid, int sig)
    {
        try
        {
            _ = NativeMethods.Kill(-pgid, sig);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc: nothing can be signalled.
        }
    }

    /// <summary>
    /// The launcher's own process group: the one that is never signalled, whatever the worker
    /// claims. -1 when it cannot be read.
    /// </summary>
    private static int OwnGroup()
    {
        try
        {
            return NativeMethods.GetPgid(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    /// <summary>
    /// The group of the child of a process: the case where setsid forked. Zero when there is no
    /// such child (yet).
    /// </summary>
    private static int ChildGroup(int pid)
    {
        foreach (string dir in ProcDirs())
        {
            if (!TryPid(dir, out int child))
            {
                continue;
            }

            ProcStat? stat = Stat(child);
            if (stat is null || stat.Value.Ppid != pid)
            {
                continue;
            }

            return stat.Value.Pgrp;
        }

        return 0;
    }

    /// <summary>
    /// The fields of a process's stat line that matter here: its state, its parent, its process
    /// group and its session.
    /// </summary>
    private readonly record struct ProcStat(char State, int Ppid, int Pgrp, int Sid);

    /// <summary>
    /// The stat line of a process, read from /proc. Null when the process is gone or its line
    /// cannot be read.
    /// </summary>
    private static ProcStat? Stat(int pid)
    {
        try
        {
            string line = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            // The command name sits in parentheses and can hold spaces and parentheses of its own:
            // the fields after the last ')' are the ones that matter.
            int close = line.LastIndexOf(')');
            if (close < 0)
            {
                return null;
            }

            string[] fields = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0].Length is 0)
            {
                return null;
            }

            return new ProcStat(
                fields[0][0],
                int.Parse(fields[1], CultureInfo.InvariantCulture),
                int.Parse(fields[2], CultureInfo.InvariantCulture),
                int.Parse(fields[3], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The directories of /proc: one per process.
    /// </summary>
    private static IEnumerable<string> ProcDirs()
    {
        try
        {
            return Directory.EnumerateDirectories("/proc").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The process id a /proc directory names, or false when it names no process.
    /// </summary>
    private static bool TryPid(string dir, out int pid)
    {
        string name = Path.GetFileName(dir);
        return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }
}
