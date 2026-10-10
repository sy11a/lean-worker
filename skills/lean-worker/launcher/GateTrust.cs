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
// IOException or UnauthorizedAccessException records "unreadable: <ExceptionType>"; an open that hangs
// on a FIFO without a writer records "unreadable: timeout"; a file larger than the per-file byte cap
// records "unreadable: too large". The chain treats any "nonregular" or "unreadable" entry that appears
// after the worker as a trust violation.
//
// .NET 8 has no portable file-type check (UnixFileMode.TypeMask lands in .NET 9), so the guard on this
// runtime is layered:
//   - the FileAttributes pre-check below (catches directories, devices where reported, reparse points)
//   - FileStream.CanSeek on the opened handle (FIFOs, sockets, character devices are non-seekable)
//   - the 5-second open/read bound (catches a FIFO blocking in open with no writer)
//   - the 16 MiB byte cap (bounds a read off a misclassified device)
// Block devices may still look regular and seekable on this runtime; a normal user cannot open them
// (the open fails → unreadable → trust violation), and the size cap bounds a read. No P/Invoke.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace LeanWorker;

internal static class GateTrust
{
    /// <summary>
    /// One set of trusted files and their SHA-256 of the bytes. The sentinel "<c>-</c>" is recorded for
    /// every path that does not exist at hash time, "<c>nonregular</c>" for a path whose last target is
    /// not a regular file, and "<c>unreadable: &lt;reason&gt;</c>" when reading was bounded by the
    /// per-file time limit ("<c>timeout</c>"), the per-file byte cap ("<c>too large</c>"), or any I/O or
    /// permission failure ("<c>&lt;ExceptionType&gt;</c>"). A file the worker created between the two
    /// hashes shows up as a difference just like a content change; a nonregular/unreadable state
    /// appearing in the after-worker snapshot is itself a trust violation.
    /// </summary>
    internal sealed record Snapshot(Dictionary<string, string> Hashes);

    /// <summary>
    /// Reads each <paramref name="paths"/> entry and hashes its bytes. Absent files get the sentinel
    /// "<c>-</c>"; a symlink's final target is the key (so retargeting the link differs); a non-regular
    /// target records "<c>nonregular</c>"; any I/O or permission error records
    /// "<c>unreadable: &lt;ExceptionType&gt;</c>". Hashing never blocks or throws: a hang on a FIFO
    /// without a writer is bounded by a per-file timeout (the open + read run on the thread pool and the
    /// wait is capped at <see cref="_hashTimeout"/>), and an unbounded file is bounded by a per-file byte
    /// cap (<see cref="MaxHashBytes"/>).
    /// </summary>
    public static async Task<Snapshot> HashAsync(IEnumerable<string> paths)
    {
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            await HashOneAsync(hashes, path).ConfigureAwait(false);
        }

        return new Snapshot(hashes);
    }

    /// <summary>
    /// Time budget for opening a file and hashing its bytes. A FIFO without a writer parks the open
    /// call in the kernel until a writer appears, and a character device like <c>/dev/zero</c> never
    /// returns EOF. The trust check must fail closed rather than hang the chain, so the open + read
    /// run on the thread pool and the wait is capped at this value.
    /// </summary>
    private static readonly TimeSpan _hashTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Byte cap for a single file hash. Inputs larger than this record "<c>unreadable: too large</c>"
    /// rather than streaming the whole file into memory.
    /// </summary>
    private const long MaxHashBytes = 16L * 1024L * 1024L;

    /// <summary>
    /// Buffer used by every file hash. 80 KiB matches the typical kernel pipe / file-copy granularity
    /// and keeps the per-read overhead negligible.
    /// </summary>
    private const int HashBufferSize = 80 * 1024;

    private static async Task HashOneAsync(Dictionary<string, string> hashes, string path)
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

            // Attribute pre-check: directories, devices (where reported), and reparse points must not
            // be opened at all. .NET 8 has no portable file-type check (UnixFileMode.TypeMask lands in
            // .NET 9), so the layered guard is: this attribute probe (Directory / Device / ReparsePoint)
            // + FileStream.CanSeek on the opened handle (FIFOs, sockets, character devices are
            // non-seekable) + the 5-second bound in HashFileAsync (a FIFO blocking in open with no
            // writer) + the 16 MiB cap below (bounds a read off a misclassified device). Block devices
            // may still look regular and seekable on this runtime; a normal user cannot open them (the
            // open fails → unreadable → trust violation), and the size cap bounds a read. No P/Invoke.
            FileAttributes attrs = File.GetAttributes(target);
            if (attrs.HasFlag(FileAttributes.Directory)
                || attrs.HasFlag(FileAttributes.Device)
                || attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                hashes[key] = "nonregular";
                return;
            }

            hashes[key] = await HashFileAsync(target).ConfigureAwait(false);
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

    private static async Task<string> HashFileAsync(string target)
    {
        // The CTS defines the cancellation token handed to ReadAsync and to Task.Run; the WhenAny
        // below caps the same value as a belt-and-suspenders bound. Either signal produces the same
        // "unreadable: timeout" sentinel.
        using CancellationTokenSource cts = new(_hashTimeout, TimeProvider.System);
        CancellationToken token = cts.Token;

        Task<string> hashTask = Task.Run(async () => await HashCoreAsync(target, token).ConfigureAwait(false), token);

        try
        {
            Task winner = await Task.WhenAny(hashTask, Task.Delay(_hashTimeout, TimeProvider.System, token)).ConfigureAwait(false);
            if (winner == hashTask)
            {
                return await hashTask.ConfigureAwait(false);
            }

            return "unreadable: timeout";
        }
        catch (OperationCanceledException)
        {
            return "unreadable: timeout";
        }
    }

    private static async Task<string> HashCoreAsync(string target, CancellationToken token)
    {
        // .NET 8 cannot tell a FIFO / socket / character device from a regular file through
        // FileAttributes (UnixFileMode.TypeMask lands in .NET 9). The portable guard on this
        // runtime is FileStream.CanSeek on the same handle: the kernel reports pipes, sockets and
        // character devices as non-seekable, so the seek check plus the 5-second timeout (which
        // catches a FIFO with a writer that hangs the read) is the complete classification. A
        // directory throws on open; that exception is classified by Directory.Exists below.
        try
        {
            FileStream fs = new(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using ConfiguredAsyncDisposable fsDisposal = fs.ConfigureAwait(false);
            if (!fs.CanSeek)
            {
                return "nonregular";
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[HashBufferSize];
            long total = 0;
            while (true)
            {
                int n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (n is 0)
                {
                    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }

                if (total + n > MaxHashBytes)
                {
                    return "unreadable: too large";
                }

                hash.AppendData(buffer, 0, n);
                total += n;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "unreadable: timeout";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fs was disposed (using) on the way out; classify the directory here.
            return Directory.Exists(target) ? "nonregular" : $"unreadable: {ex.GetType().Name}";
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
