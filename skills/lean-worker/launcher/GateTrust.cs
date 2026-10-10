// The gate runs in the working tree the worker has just changed, but the launcher treats some files as
// trusted inputs (the gate's command, the profile, the project notes, the price book, the run-root
// config, the .config/dotnet-tools.json at the work tree root, plus dotnet-tools.json/
// global.json/nuget.config searched from the gate's working directory up to the git root, and the
// resolved gate executable wherever it lives). A worker that edits them can swap the gate's report
// path or its command for the next round. The launcher hashes these files before the worker starts,
// again before the gate runs, and a third time after the gate exits; any difference from the
// before-worker snapshot ends the chain with `error` instead of running the gate against tampered
// inputs.
//
// Snapshot keys are the trusted paths the operator configured; the value always carries the resolved
// final target before a bar, then the SHA-256 (for regular file bytes) or a sentinel for everything
// else. So retargeting a symlink shows up as a value change (the "target" half moves; the key stays),
// and the chain's change test detects it through the union of keys plus identical-keyed value
// comparisons. Non-regular targets (directories, FIFOs, sockets, character/block devices, final-target
// paths under /dev/, /proc/ or /sys/) record "<target>|nonregular"; a symlink whose target does not
// exist records "unreadable: dangling link" under the link path; a read that returned fewer bytes
// than the length reported at open time records "<target>|unreadable: length mismatch"; any
// IOException or UnauthorizedAccessException records "<target>|unreadable: <ExceptionType>"; an open
// that hangs on a FIFO without a writer records "<target>|unreadable: timeout"; a file larger than
// the per-file byte cap records "<target>|unreadable: too large". The chain treats any
// "nonregular" or "unreadable" entry that appears after the worker as a trust violation.
//
// .NET 8 has no portable file-type check (UnixFileMode.TypeMask lands in .NET 9), so the guard on this
// runtime is layered:
//   - the LinkTarget check at the top of HashOneAsync (a path whose LinkTarget is non-null and whose
//     target does not exist as a file or directory is a dangling link → "unreadable: dangling link"
//     under the link path)
//   - the final-target refusal for /dev/, /proc/, /sys/ (kernel surfaces whose contents are generated
//     on read; /dev/null passes every other guard and would otherwise hash the empty SHA-256)
//   - the FileAttributes pre-check below (catches directories, devices where reported, reparse points)
//   - FileStream.CanSeek on the opened handle (FIFOs, sockets, character devices are non-seekable)
//   - the bytes-read vs. fs.Length check at the end of HashCoreAsync (a mismatch reports
//     "unreadable: length mismatch" and refuses to hash)
//   - the 5-second open/read bound (catches a FIFO blocking in open with no writer)
//   - the 16 MiB byte cap (bounds a read off a misclassified device)
// Block devices may still look regular and seekable on this runtime; a normal user cannot open them
// (the open fails → unreadable → trust violation), and the size cap bounds a read. No P/Invoke.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal static class GateTrust
{
    /// <summary>
    /// One set of trusted files and their SHA-256 of the bytes. Snapshot keys are always the trusted
    /// path the operator configured (a symlink is recorded under its own path, not its target).
    /// Values have the shape final-target bar hash-or-sentinel: the SHA-256 (lower hex) for a
    /// regular file's bytes; <c>nonregular</c> when the final target is a directory, device, reparse
    /// point, or path under <c>/dev/</c>, <c>/proc/</c>, <c>/sys/</c>; <c>unreadable: reason</c>
    /// when reading was bounded by the per-file time limit (<c>timeout</c>), the per-file byte cap
    /// (<c>too large</c>), a length mismatch reported at open time (<c>length mismatch</c>), the
    /// link pointed at a missing target (<c>dangling link</c>, recorded under the link path), or
    /// any I/O or permission failure (the exception type). The sentinel <c>-</c> (no value after
    /// the bar) marks a path that does not exist at hash time and is not a symlink. A file the
    /// worker created between the two hashes shows up as a new key just like a content change; a
    /// nonregular/unreadable state appearing in the after-worker snapshot is itself a trust
    /// violation.
    /// </summary>
    internal sealed record Snapshot(Dictionary<string, string> Hashes);

    /// <summary>
    /// Reads each <paramref name="paths"/> entry and hashes its bytes. Entries that exist as a
    /// non-link record <c>"&lt;path&gt;|&lt;sha-or-sentinel&gt;"</c> (their own path is the final
    /// target). Entries that are symlinks resolve to their final target; the target path appears after
    /// the bar so a retargeted link shows up as a value change. A symlink whose target is unreachable
    /// records <c>"unreadable: dangling link"</c> under the link path. Hashing never blocks or throws:
    /// a hang on a FIFO without a writer is bounded by a per-file timeout (the open + read run on the
    /// thread pool and the wait is capped at <see cref="_hashTimeout"/>), and an unbounded file is
    /// bounded by a per-file byte cap (<see cref="MaxHashBytes"/>).
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
        // The snapshot key is always the trusted path the operator configured. The value places the
        // resolved final target before a bar so retargeting a link becomes a value change (the key
        // stays the same; the value's "target" half differs).
        string key = path;
        try
        {
            string? linkTarget = TryReadLinkTarget(path);
            string target = ResolveLinkTarget(path, linkTarget);

            // Dangling link: the path is a symlink whose target does not exist as a file or a
            // directory. Record the sentinel under the link path so the error message names a
            // path the operator actually configured.
            if (linkTarget is not null && !PathExists(target))
            {
                hashes[key] = "unreadable: dangling link";
                return;
            }

            // Absent path: nothing at the path (and not a link whose target would resolve). The
            // sentinel is "-" so the trust check can ignore absent entries rather than treating
            // every missing config file as a kernel failure.
            if (linkTarget is null && !PathExists(target))
            {
                hashes[key] = "-";
                return;
            }

            if (IsPseudoFileSystem(target))
            {
                hashes[key] = $"{target}|nonregular";
                return;
            }

            if (HasNonRegularAttributes(target))
            {
                hashes[key] = $"{target}|nonregular";
                return;
            }

            hashes[key] = $"{target}|{await HashFileAsync(target).ConfigureAwait(false)}";
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

    private static string? TryReadLinkTarget(string path)
    {
        try
        {
            // FileSystemInfo.LinkTarget returns the link's ReadLink target whenever the path is
            // itself a symlink (including dangling links), and null otherwise.
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            // Best-effort ReadLink: classify the path with whatever we can still see below.
            return null;
        }
    }

    private static string ResolveLinkTarget(string path, string? linkTarget)
    {
        // FileSystemInfo.LinkTarget returns the raw ReadLink result, so /usr/bin/sh -> bash reports
        // "bash" rather than "/usr/bin/bash"; checking PathExists("bash") against the caller's cwd
        // would then misclassify the link as dangling. Resolve a relative target against the link's
        // directory (POSIX); an absolute target is the final target as-is; no link means the path
        // itself is the target.
        if (linkTarget is null)
        {
            return path;
        }

        if (Path.IsPathRooted(linkTarget))
        {
            return linkTarget;
        }

        string? parent = Path.GetDirectoryName(path);
        return Path.GetFullPath(Path.Combine(parent ?? string.Empty, linkTarget));
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsPseudoFileSystem(string target)
    {
        return target.StartsWith("/dev/", StringComparison.Ordinal)
            || target.StartsWith("/proc/", StringComparison.Ordinal)
            || target.StartsWith("/sys/", StringComparison.Ordinal);
    }

    private static bool HasNonRegularAttributes(string target)
    {
        FileAttributes attrs = File.GetAttributes(target);
        return attrs.HasFlag(FileAttributes.Directory)
            || attrs.HasFlag(FileAttributes.Device)
            || attrs.HasFlag(FileAttributes.ReparsePoint);
    }

    private static async Task<string> HashFileAsync(string target)
    {
        // The CTS defines the cancellation token handed to ReadAsync and to Task.Run; the WhenAny
        // below caps the same value as a belt-and-suspenders bound. Either signal produces the same
        // "unreadable: timeout" sentinel.
        using CancellationTokenSource cts = new(_hashTimeout, TimeProvider.System);
        CancellationToken token = cts.Token;

        // The read runs on the thread pool, otherwise the FileStream ctor can park the caller's thread
        // on a FIFO with no writer and the WhenAny timer never gets a chance to win. The wrapper
        // moves the open off the call site and returns a Task<string> that can be raced with the
        // timer; Task.Factory.StartNew takes a TaskScheduler (CA2008/VSTHRD105) and an Unwrap so the
        // returned task represents the inner async method's completion.
        Task<string> hashTask = HashOffPoolAsync(target, token);

        // Observe (and discard) any fault on the abandoned hashTask. When the WhenAny below picks the
        // timer, the open/read thread is left running until its own read or open completes; that thread
        // is blocked on a FIFO without a writer (no kernel-level means to cancel the open) and is
        // acceptable for a short-lived launcher. The continuation is what prevents that fault from
        // becoming an unobserved task exception that the runtime would surface.
        _ = hashTask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        Task<string> timer = HashTimeoutAsync(_hashTimeout, TimeProvider.System, token);
        Task<string> winner = await Task.WhenAny(hashTask, timer).ConfigureAwait(false);
        if (winner == hashTask)
        {
            return await hashTask.ConfigureAwait(false);
        }

        return "unreadable: timeout";
    }

    private static async Task<string> HashTimeoutAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken token)
    {
        await Task.Delay(timeout, timeProvider, token).ConfigureAwait(false);
        return "unreadable: timeout";
    }

    private static async Task<string> HashOffPoolAsync(string target, CancellationToken token)
    {
        // StartNew with Func<object, Task<...>> returns Task<Task<...>> and takes a TaskScheduler
        // (satisfies CA2008/VSTHRD105). Unwrap exposes the inner Task<string>; awaiting it gives
        // the inner result, the async method's signature wraps that back into Task<string> for the
        // caller's WhenAny with a timer.
        Task<Task<string>> wrapped = Task.Factory.StartNew(async _ => await HashCoreAsync(target, token).ConfigureAwait(false), state: null, token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
        return await wrapped.Unwrap().ConfigureAwait(false);
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

            // Capture the length right after opening. Some kernel-managed entries report a Length
            // that does not match the bytes the read actually returns (the file is being modified
            // while we read it, or Length is a hint and the kernel returns less). A mismatch means
            // the hash would not reflect the file's actual contents at this moment, so refuse to
            // classify it as a regular-file read.
            long expectedLength = fs.Length;

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[HashBufferSize];
            long total = 0;
            while (true)
            {
                int n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (n is 0)
                {
                    if (total != expectedLength)
                    {
                        return "unreadable: length mismatch";
                    }

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
    /// Paths whose state differs between the two snapshots, in ordinal order. A path counts as changed
    /// when it is in one snapshot but not the other, or when its value differs (a retargeted
    /// symlink shows up as the same key with a different "target" half in its value).
    /// </summary>
    public static List<string> Changed(Snapshot before, Snapshot after)
    {
        return [.. before.Hashes.Keys.Union(after.Hashes.Keys, StringComparer.Ordinal)
            .Where(p => !before.Hashes.TryGetValue(p, out string? a) || !after.Hashes.TryGetValue(p, out string? b) || a != b)
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// The paths the gate chain treats as trusted inputs: every file under <paramref name="runsRoot"/>
    /// (recursively, except the launcher-owned subtrees <c>runs/</c>, <c>inbox/</c>, <c>system/</c> and
    /// the ledger <c>runs.jsonl</c>), <c>dotnet-tools.json</c>, <c>.config/dotnet-tools.json</c>,
    /// <c>global.json</c> and the three <c>NuGet.config</c>/<c>NuGet.Config</c>/<c>nuget.config</c>
    /// casings at every directory from <paramref name="gateWorkingDirectory"/> up to
    /// <paramref name="gitRoot"/> (deduped), the resolved gate executable (argv[0]: PATH when the
    /// entry has no directory part, else resolved against <paramref name="gateWorkingDirectory"/> —
    /// wherever it lives, inside the repo or not), every remaining argv entry that is an existing
    /// file path under <paramref name="gitRoot"/> (resolved against <paramref name="gateWorkingDirectory"/>),
    /// any <paramref name="extraTrust"/> paths or globs the operator pinned (literal entries
    /// included even when absent, glob matches limited to files that exist before the worker runs),
    /// and the price book file at <paramref name="extraPricesFile"/> when it lies outside
    /// <paramref name="runsRoot"/>.
    /// </summary>
    public static List<string> CollectPaths(string runsRoot, string gitRoot, string gateWorkingDirectory, IReadOnlyList<string> gateCommand,
        IReadOnlyList<string>? extraTrust = null, string? extraPricesFile = null)
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
        AddConfigFileWalk(p => Add(p), gateWorkingDirectory, gitRoot);
        AddResolvedExecutable(gateCommand, gateWorkingDirectory, p => Add(p));
        AddArgvEntries(gateCommand, gateWorkingDirectory, gitRoot, p => Add(p));
        AddExtraTrust(extraTrust, gitRoot, gateWorkingDirectory, p => Add(p));
        AddPricesFile(extraPricesFile, runsRoot, p => Add(p));
        return paths;
    }

    private static void AddRunsRootFiles(string runsRoot, Action<string> add)
    {
        if (!Directory.Exists(runsRoot))
        {
            return;
        }

        HashSet<string> skipDirs = new(StringComparer.Ordinal);
        foreach (string sub in new[] { "runs", "inbox", "system" })
        {
            skipDirs.Add(Path.Combine(runsRoot, sub));
        }

        string ledger = Path.Combine(runsRoot, "runs.jsonl");

        // Walk recursively; the launcher's own bookkeeping lives under runs/, inbox/ and system/ and
        // is never a gate input, and runs.jsonl is the ledger (one record per run, append-only).
        foreach (string file in Directory.EnumerateFiles(runsRoot, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, ledger, StringComparison.Ordinal))
            {
                continue;
            }

            string? parent = Path.GetDirectoryName(file);
            while (parent is not null)
            {
                if (skipDirs.Contains(parent))
                {
                    break;
                }

                if (string.Equals(parent, runsRoot, StringComparison.Ordinal))
                {
                    parent = null;
                    break;
                }

                parent = Path.GetDirectoryName(parent);
            }

            if (parent is not null && skipDirs.Contains(parent))
            {
                continue;
            }

            add(file);
        }
    }

    private static void AddConfigFileWalk(Action<string> add, string fromDir, string toDir)
    {
        // Walk from the gate's working directory up to and including the git root. The dotnet local-
        // tool lookup reads `dotnet-tools.json` and `.config/dotnet-tools.json` at every level; same
        // story for `global.json` and the NuGet config casings. The walk ends at the git root so a
        // /home/<user> ancestor does not contribute candidate paths.
        string? current = fromDir;
        while (current is not null)
        {
            AddConfigFiles(add, current);
            if (string.Equals(current, toDir, StringComparison.Ordinal))
            {
                break;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Reached the filesystem root before reaching the git root; emit one last set so the
                // top-level git root config files are still trusted.
                if (!string.Equals(current, toDir, StringComparison.Ordinal))
                {
                    AddConfigFiles(add, toDir);
                }

                break;
            }

            current = parent;
        }
    }

    private static void AddConfigFiles(Action<string> add, string dir)
    {
        add(Path.Combine(dir, "nuget.config"));
        add(Path.Combine(dir, "NuGet.Config"));
        add(Path.Combine(dir, "NuGet.config"));
        add(Path.Combine(dir, "global.json"));
        add(Path.Combine(dir, "dotnet-tools.json"));
        add(Path.Combine(dir, ".config", "dotnet-tools.json"));
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

    /// <summary>
    /// Resolves the operator's <c>gate.trust</c> entries against the git root (or the gate working
    /// directory when there is no git root) and adds them to the trust set. Literal entries (no glob
    /// characters) are added as-is, even when the file does not exist; glob matches are limited to
    /// files that exist before the worker runs (WriteScope.InScope-style matching).
    /// </summary>
    private static void AddExtraTrust(IReadOnlyList<string>? entries, string gitRoot, string gateWorkingDirectory, Action<string> add)
    {
        if (entries is null || entries.Count is 0)
        {
            return;
        }

        // Trust entries are repo-relative; the git root takes precedence when it is usable, the gate
        // working directory is the fallback so a non-git launch can still pin files.
        string anchor = Directory.Exists(gitRoot) ? gitRoot : gateWorkingDirectory;

        foreach (string entry in entries)
        {
            bool hasGlob = entry.IndexOfAny(['*', '?']) >= 0;
            if (!hasGlob)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(anchor, entry));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                add(full);
                continue;
            }

            // Glob: enumerate every file under <anchor> and add the ones the glob matches that exist
            // before the worker runs (Absence here is not a violation on its own; the trust check
            // also catches "present and later gone").
            Regex matcher;
            try
            {
                matcher = WriteScope.BuildGlobRegex(entry);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!Directory.Exists(anchor))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(anchor, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(anchor, file).Replace('\\', '/');
                if (matcher.IsMatch(rel))
                {
                    add(file);
                }
            }
        }
    }

    private static void AddPricesFile(string? pricesFile, string runsRoot, Action<string> add)
    {
        if (string.IsNullOrEmpty(pricesFile))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(pricesFile);
        }
        catch (ArgumentException)
        {
            return;
        }

        // An explicit --prices / profile "prices" path inside the runs root is already covered by
        // AddRunsRootFiles; only add it here when it lives somewhere the recursive walk misses.
        string runsRootFull;
        try
        {
            runsRootFull = Path.GetFullPath(runsRoot);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (full.StartsWith(runsRootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(full, runsRootFull, StringComparison.Ordinal))
        {
            return;
        }

        add(full);
    }
}
