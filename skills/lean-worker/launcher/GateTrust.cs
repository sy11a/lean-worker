// The gate runs in the working tree the worker has just changed, but the launcher treats some files as
// trusted inputs (the gate's command, the profile, the project notes, the price book, the run-root
// config, the .config/dotnet-tools.json at the work tree root, plus nuget.config/global.json at both the
// git root and the gate's working directory, and the resolved gate executable wherever it lives). A
// worker that edits them can swap the gate's report path or its command for the next round. The launcher
// hashes these files before the worker starts and again before the gate runs; a difference ends the chain
// with `error` instead of running the gate against tampered inputs.
//
// Hashing is conservative: only regular files are hashed. A symlink is resolved to its final target and
// the target path is recorded as the key (so a retargeted link shows up as a change). Non-regular targets
// (directories, FIFOs, sockets, character/block devices) record the sentinel "nonregular"; any
// IOException or UnauthorizedAccessException records "unreadable: <ExceptionType>". The chain treats
// any "nonregular" or "unreadable" entry that appears after the worker as a trust violation.

using System.Security.Cryptography;

namespace LeanWorker;

internal static class GateTrust
{
    /// <summary>
    /// One set of trusted files and their SHA-256 of the bytes. The sentinel "<c>-</c>" is recorded for
    /// every path that does not exist at hash time, "<c>nonregular</c>" for a path whose last target is
    /// not a regular file, and "<c>unreadable: &lt;ExceptionType&gt;</c>" when an I/O or permission
    /// failure prevented reading. A file the worker created between the two hashes shows up as a
    /// difference just like a content change; a nonregular/unreadable state appearing in the after-worker
    /// snapshot is itself a trust violation.
    /// </summary>
    internal sealed record Snapshot(Dictionary<string, string> Hashes);

    /// <summary>
    /// Reads each <paramref name="paths"/> entry and hashes its bytes. Absent files get the sentinel
    /// "<c>-</c>"; a symlink's final target is the key (so retargeting the link differs); a non-regular
    /// target records "<c>nonregular</c>"; any I/O or permission error records
    /// "<c>unreadable: &lt;ExceptionType&gt;</c>". Hashing never blocks or throws.
    /// </summary>
    public static Snapshot Hash(IEnumerable<string> paths)
    {
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            HashOne(hashes, path);
        }

        return new Snapshot(hashes);
    }

    private static void HashOne(Dictionary<string, string> hashes, string path)
    {
        string key = path;
        try
        {
            if (!File.Exists(path))
            {
                hashes[key] = "-";
                return;
            }

            string target = ResolveTarget(path, ref key);

            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hashes[key] = $"unreadable: {ex.GetType().Name}";
                return;
            }

            if (IsForbiddenFileType(attrs))
            {
                hashes[key] = "nonregular";
                return;
            }

            if (!IsRegularFile(target, attrs))
            {
                hashes[key] = "nonregular";
                return;
            }

            try
            {
                using FileStream fs = new(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                hashes[key] = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hashes[key] = $"unreadable: {ex.GetType().Name}";
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            // Fail closed: any I/O / platform / argument failure still records a sentinel rather than
            // crashing the gate chain. Non-I/O exceptions (e.g. NullReferenceException) propagate.
            hashes[key] = $"unreadable: {ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Resolves <paramref name="path"/> through any symlink chain to the final target. When the final
    /// target is reachable, the target path is returned and <paramref name="key"/> is updated so the
    /// snapshot records the target (a change in the link's target becomes a change in the key).
    /// </summary>
    private static string ResolveTarget(string path, ref string key)
    {
        FileSystemInfo? resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
        if (resolved is null)
        {
            return path;
        }

        key = resolved.FullName;
        return resolved.FullName;
    }

    private static bool IsForbiddenFileType(FileAttributes attrs)
    {
        return attrs.HasFlag(FileAttributes.Directory)
            || attrs.HasFlag(FileAttributes.Device)
            || attrs.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsRegularFile(string target, FileAttributes attrs)
    {
        if (OperatingSystem.IsWindows())
        {
            return attrs.HasFlag(FileAttributes.Normal)
                || attrs.HasFlag(FileAttributes.Archive)
                || attrs.HasFlag(FileAttributes.ReadOnly)
                || attrs.HasFlag(FileAttributes.Hidden);
        }

        // On Unix-like systems, FileAttributes alone does not distinguish a regular file from a FIFO or
        // socket (both surface as FileAttributes.Normal). File.GetUnixFileMode throws
        // PlatformNotSupportedException on Windows; on Unix-like it gives the stat mode, and an
        // inaccessible target throws IOException / UnauthorizedAccessException. We treat any such
        // failure as non-regular so the path records the sentinel rather than crashing.
        try
        {
            return File.GetUnixFileMode(target) is not UnixFileMode.None;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Paths whose state differs between the two snapshots, in ordinal order. A path counts as changed
    /// when it is in one snapshot but not the other, or when its hash differs.
    /// </summary>
    public static List<string> Changed(Snapshot before, Snapshot after)
    {
        return [.. before.Hashes.Keys.Union(after.Hashes.Keys, StringComparer.Ordinal)
            .Where(p => !before.Hashes.TryGetValue(p, out string? a) || !after.Hashes.TryGetValue(p, out string? b) || a != b)
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// The paths the gate chain treats as trusted inputs: every top-level file in <paramref name="runsRoot"/>
    /// other than <c>runs.jsonl</c>, <c>.config/dotnet-tools.json</c> at <paramref name="gitRoot"/>,
    /// <c>nuget.config</c>/<c>NuGet.Config</c>/<c>NuGet.config</c> and <c>global.json</c> at both
    /// <paramref name="gitRoot"/> and <paramref name="gateWorkingDirectory"/> (deduped), the resolved
    /// gate executable (argv[0]: <see cref="Launcher.FindOnPath"/> when the entry has no directory part,
    /// else resolved against <paramref name="gateWorkingDirectory"/> — wherever it lives, inside the repo
    /// or not), and every remaining argv entry that is an existing file path under
    /// <paramref name="gitRoot"/> (resolved against <paramref name="gateWorkingDirectory"/>).
    /// </summary>
    public static List<string> CollectPaths(string runsRoot, string gitRoot, string gateWorkingDirectory, IReadOnlyList<string> gateCommand)
    {
        List<string> paths = [];
        HashSet<string> dedupe = new(StringComparer.Ordinal);
        void Add(string p)
        {
            if (!dedupe.Add(p))
            {
                return;
            }

            paths.Add(p);
        }

        AddRunsRootFiles(runsRoot, p => Add(p));
        Add(Path.Combine(gitRoot, ".config", "dotnet-tools.json"));
        AddConfigFiles(p => Add(p), gitRoot);
        AddConfigFiles(p => Add(p), gateWorkingDirectory);
        AddResolvedExecutable(gateCommand, gateWorkingDirectory, p => Add(p));
        AddArgvEntries(gateCommand, gateWorkingDirectory, gitRoot, p => Add(p));
        return paths;
    }

    private static void AddRunsRootFiles(string runsRoot, Action<string> add)
    {
        if (!Directory.Exists(runsRoot))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(runsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(file) is "runs.jsonl")
            {
                continue;
            }

            add(file);
        }
    }

    private static void AddConfigFiles(Action<string> add, string dir)
    {
        add(Path.Combine(dir, "nuget.config"));
        add(Path.Combine(dir, "NuGet.Config"));
        add(Path.Combine(dir, "NuGet.config"));
        add(Path.Combine(dir, "global.json"));
    }

    private static void AddResolvedExecutable(IReadOnlyList<string> gateCommand, string gateWorkingDirectory, Action<string> add)
    {
        string exe = ResolveExecutable(gateCommand, gateWorkingDirectory);
        if (exe.Length is 0)
        {
            return;
        }

        add(exe);
    }

    private static void AddArgvEntries(IReadOnlyList<string> gateCommand, string gateWorkingDirectory, string gitRoot, Action<string> add)
    {
        for (int i = 1; i < gateCommand.Count; i++)
        {
            string entry = gateCommand[i];
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.IsPathRooted(entry)
                    ? Path.GetFullPath(entry)
                    : Path.GetFullPath(Path.Combine(gateWorkingDirectory, entry));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!File.Exists(full))
            {
                continue;
            }

            string rel = Path.GetRelativePath(gitRoot, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            {
                continue;
            }

            add(full);
        }
    }

    private static string ResolveExecutable(IReadOnlyList<string> gateCommand, string gateWorkingDirectory)
    {
        if (gateCommand.Count is 0)
        {
            return string.Empty;
        }

        string first = gateCommand[0];
        if (string.IsNullOrEmpty(first))
        {
            return string.Empty;
        }

        if (HasDirectorySeparator(first))
        {
            try
            {
                return Path.GetFullPath(Path.Combine(gateWorkingDirectory, first));
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        return Launcher.FindOnPath(first) ?? first;
    }

    private static bool HasDirectorySeparator(string s)
    {
        return s.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || s.Contains(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal);
    }
}