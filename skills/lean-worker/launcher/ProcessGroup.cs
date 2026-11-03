// The process group of the worker: setsid gives the worker a session (and with it a process group)
// of its own, and the launcher signals that group after the worker exits.
//
// Nothing here calls into libc: the group a process is in is read from /proc, and the signal to a
// group is sent by the kill program, by its full path.

using System.Diagnostics;
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

    /// <summary>
    /// The note of a run whose group still had members after the SIGKILL: the kill cannot be
    /// taken back, so what survived it stays behind.
    /// </summary>
    public static string SurvivedNote(int survivors) =>
        "process-group: " + survivors.ToString(CultureInfo.InvariantCulture) + " processes survived SIGKILL";

    /// <summary>
    /// The signals the group gets: first the one that asks its members to stop, then the one that
    /// leaves nothing behind.
    /// </summary>
    private const string Sigterm = "TERM";

    private const string Sigkill = "KILL";

    /// <summary>
    /// The kill program by its full paths, in the order they are tried: a bare name would be
    /// looked up in the PATH, which the worker and the profile's PATH can change.
    /// </summary>
    private static readonly string[] _killPaths = ["/usr/bin/kill", "/bin/kill"];

    /// <summary>
    /// The number of checks of a group that did not go on SIGTERM: fifty steps of a tenth of a
    /// second are five seconds.
    /// </summary>
    private const int WaitSteps = 50;

    /// <summary>
    /// The number of checks of a group whose members did not die on the SIGKILL: ten steps of a
    /// tenth of a second are one second. A killed process is a zombie at once, so the wait is
    /// short: a process that cannot die must not hold the launcher.
    /// </summary>
    private const int SurvivorSteps = 10;

    /// <summary>
    /// The wait between the two checks of a group that did not go on SIGTERM.
    /// </summary>
    private static readonly TimeSpan _waitStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long the launcher waits for the kill program before it stops waiting for it.
    /// </summary>
    private const int KillTimeoutMs = 10_000;

    /// <summary>
    /// Whether the worker can get a process group of its own: Linux with /proc, setsid in /usr/bin
    /// and a kill program to signal the group with. Without the kill program the group could not
    /// be emptied, so there is no group to speak of.
    /// </summary>
    public static bool Available =>
        !OperatingSystem.IsWindows() && Directory.Exists("/proc") && File.Exists(SetsidPath) && KillPath() is not null;

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
                // The worker leads its own group: the group is the worker's own id. Nothing else
                // is looked at: a group the worker made for itself is not the launcher's to
                // signal, and a guess would kill a process the worker chose.
                if (stat.Value.Pgrp == pid)
                {
                    return pid;
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
    ///
    /// A SIGKILL is final, but a killed process is still there until the kernel has taken it: the
    /// launcher waits for the killed members to die before it returns, so the members that are
    /// still there afterwards (read with <see cref="Alive"/>) are the ones the kill could not end.
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

        await SignalAsync(pgid, Sigterm).ConfigureAwait(false);
        for (int step = 0; step < WaitSteps && Alive(pgid) > 0; step++)
        {
            await Task.Delay(_waitStep, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        }

        if (Alive(pgid) > 0)
        {
            await SignalAsync(pgid, Sigkill).ConfigureAwait(false);
            // The killed members need a moment to die. The wait is short (a second): a process
            // that cannot die must not hold the launcher.
            for (int step = 0; step < SurvivorSteps && Alive(pgid) > 0; step++)
            {
                await Task.Delay(_waitStep, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
            }
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
    /// A signal to a whole group: the negative pid addresses the group, not one process. The kill
    /// program sends the signal — by its full path, with the group as its argument and without a
    /// shell around it.
    /// </summary>
    private static async Task SignalAsync(int pgid, string signal)
    {
        string? kill = KillPath();
        if (kill is null)
        {
            // No kill program: nothing can be signalled.
            return;
        }

        ProcessStartInfo psi = new(kill) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(signal);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-" + pgid.ToString(CultureInfo.InvariantCulture));

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return;
            }

            // The kill program says what it could not do on its error stream: both streams are
            // read to their end, so the launcher waits for the program and not for a pipe.
            Task<string> output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> error = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using CancellationTokenSource cts = new(KillTimeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The kill program did not finish in time: it is killed and the launcher stops
                // waiting for it.
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return;
            }

            _ = await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The kill program could not be run: nothing can be signalled.
        }
    }

    /// <summary>
    /// The kill program by its full path: /usr/bin/kill, else /bin/kill. Null when there is none.
    /// </summary>
    private static string? KillPath()
    {
        foreach (string path in _killPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// The launcher's own process group: the one that is never signalled, whatever the worker
    /// claims. -1 when it cannot be read.
    /// </summary>
    private static int OwnGroup() => GroupOf(Environment.ProcessId);

    /// <summary>
    /// The group a process is in: the pgrp of its stat line in /proc — the fifth field of the
    /// line, counted after the closing parenthesis of the command name (the name may hold spaces
    /// and parentheses of its own). -1 when it cannot be told: the process is gone or its line
    /// cannot be read.
    /// </summary>
    public static int GroupOf(int pid)
    {
        ProcStat? stat = Stat(pid);
        return stat?.Pgrp ?? -1;
    }

    /// <summary>
    /// The fields of a process's stat line that matter here: its state and its process group.
    /// </summary>
    private readonly record struct ProcStat(char State, int Pgrp);

    /// <summary>
    /// The stat line of a process, read from /proc: its state (the third field of the line) and its
    /// process group (the fifth) — counted after the command name, which sits in parentheses and
    /// may hold spaces and parentheses of its own. Null when the process is gone or its line
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
            if (fields.Length < 3 || fields[0].Length is 0)
            {
                return null;
            }

            return new ProcStat(
                fields[0][0],
                int.Parse(fields[2], CultureInfo.InvariantCulture));
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
