// The /proc probe of the live status: the stat line of a process, the boot id and a scan for the
// members of a process group. Every read tells three cases apart: the entry is there, it is
// provably absent, or the probe failed (a permission, an unexpected format). Only a provable
// absence may end a run; a failed probe never does.
//
// The root is a parameter so a test can hand it a directory of its own instead of the real /proc.

using System.Globalization;

namespace LeanWorker;

/// <summary>
/// Reads /proc (or the directory a test gives) for the live status.
/// </summary>
internal sealed class ProcFs(string root)
{
    /// <summary>
    /// The real /proc.
    /// </summary>
    public static ProcFs Host { get; } = new("/proc");

    public string Root { get; } = root;

    /// <summary>
    /// The stat line of a process: present, provably absent (no such entry), or failed.
    /// </summary>
    public ProcProbe Stat(int pid)
    {
        if (pid <= 0)
        {
            return ProcProbe.Failed("no pid recorded");
        }

        string path = Path.Combine(Root, pid.ToString(CultureInfo.InvariantCulture), "stat");
        try
        {
            string line = File.ReadAllText(path);
            return ProcStat.Parse(line) is { } stat
                ? ProcProbe.Present(stat)
                : ProcProbe.Failed($"{path}: unexpected format");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ProcProbe.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProcProbe.Failed($"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The boot id (/proc/sys/kernel/random/boot_id), trimmed; null when it cannot be read.
    /// </summary>
    public string? BootId()
    {
        try
        {
            string id = File.ReadAllText(Path.Combine(Root, "sys", "kernel", "random", "boot_id")).Trim();
            return id.Length > 0 ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether some live process (not a zombie) is in group <paramref name="pgid"/> and session
    /// <paramref name="sid"/>. True when one is found; false when the scan read every entry and
    /// found none; null when the scan could not tell (the listing or an entry could not be read).
    /// </summary>
    public bool? HasGroupMember(int pgid, int sid)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = [.. Directory.EnumerateDirectories(Root)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        bool unsure = false;
        foreach (string dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }

            ProcProbe probe = Stat(pid);
            if (probe.Kind is ProbeKind.Failed)
            {
                unsure = true;
            }
            else if (probe.Kind is ProbeKind.Present && probe.Stat.Pgrp == pgid && probe.Stat.Session == sid && !probe.Stat.Exited)
            {
                return true;
            }
        }

        return unsure ? null : false;
    }
}
