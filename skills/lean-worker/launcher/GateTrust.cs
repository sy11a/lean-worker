// The gate runs in the working tree the worker has just changed, but the launcher treats some files as
// trusted inputs (the gate's command, the profile, the project notes, the price book, the run-root
// config, the .config/dotnet-tools.json at the work tree root, plus dotnet-tools.json/
// global.json/nuget.config searched from the gate's working directory up to the git root, the
// resolved gate executable wherever it lives, and every existing argv file entry, wherever it lives).
// A worker that edits them can swap the gate's report
// path or its command for the next round. The launcher hashes these files before the worker starts,
// again before the gate runs, and a third time after the gate exits; any difference from the
// before-worker snapshot ends the chain with `error` instead of running the gate against tampered
// inputs.
//
// The gate's PATH: commands a gate script runs by name resolve only through the frozen absolute
// PATH — built once in round 1 from the launcher's PATH by keeping only the absolute entries
// (deduped, order kept; relative entries such as "." or node_modules/.bin resolve inside the
// worker's own tree and are dropped; a PATH with no absolute entry left is a launch error, since
// an empty PATH would resolve bare names in the gate's current directory), frozen on the gate
// spec, and handed to the gate as PATH in every round. A PATH directory at or under the git root,
// the working directory or the runs root is refused before the worker (exit 2) — the worker could
// write the gate's own PATH there — and so is a symlink entry whose final target lies there: the
// worker could rewrite what the link resolves to while the PATH directory's listing stays intact.
// Every such "inside the working tree" comparison runs on canonical paths (Canonical, with
// realpath(3) semantics): a PATH entry through a symlinked directory, a git root reached through a
// symlinked home, or a final target behind a link in a parent directory compares against the files
// the paths really name — and a link on the way to a PATH directory or to a final target is refused
// in its own right when the link path itself sits in the tree, since the worker can retarget it no
// matter where it points today.
// Each directory on that PATH is trusted as listed: at round 1 the launcher records the entries
// directly in each directory (no recursion, no hashing) — name, length, last write time (UTC),
// whether the entry is a symlink and, for a symlink, its link target plus the chain's final
// target as a full path with that target's length and last write time (`dangling` when the
// target does not exist) — and the pre-gate and after-gate checks list them again. An added or
// removed name, a changed length, mtime, link target or final target (with the target's own
// length and mtime), and any change of a directory's own state (missing ↔ readable ↔ unreadable,
// even when the listings are empty) is a trust violation. Named,
// accepted gaps: the content of a same-named entry is not hashed, so a worker that rewrites it and
// restores the mtime (`touch -d`) while keeping the length escapes this check (put such a tool in
// gate.trust or call it by an absolute path in a trusted script), and nothing under a subdirectory
// of a PATH directory is listed directly (only that subdirectory's own listing metadata notices).
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
// that hangs on a FIFO without a writer records "<target>|unreadable: timeout". The chain treats any
// "nonregular" or "unreadable" entry that appears after the worker as a trust violation.
//
// .NET 8 has no portable file-type check (UnixFileMode.TypeMask lands in .NET 9), so the guard on this
// runtime is layered:
//   - the ResolveLinkTarget check at the top of HashOneAsync (a path whose final link target does not
//     exist as a file or directory is a dangling link → "unreadable: dangling link" under the link path)
//   - the final-target refusal for /dev/, /proc/, /sys/ (kernel surfaces whose contents are generated
//     on read; /dev/null passes every other guard and would otherwise hash the empty SHA-256)
//   - the FileAttributes pre-check below (catches directories, devices where reported, reparse points)
//   - FileStream.CanSeek on the opened handle (FIFOs, sockets, character devices are non-seekable)
//   - the bytes-read vs. fs.Length check at the end of HashCoreAsync (a mismatch reports
//     "unreadable: length mismatch" and refuses to hash)
//   - the 30-second open/read bound (catches a FIFO blocking in open with no writer, and bounds a
//     large regular file's hash)
// Block devices may still look regular and seekable on this runtime; a normal user cannot open them
// (the open fails → unreadable → trust violation). No P/Invoke.

using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal static class GateTrust
{
    /// <summary>
    /// How two trust paths are compared: ordinal on Linux, case-insensitive on macOS and Windows,
    /// whose filesystems answer to both casings of the same file. Every comparison of two trust
    /// paths — roots against candidates, snapshot keys, listing names, declared outputs — goes
    /// through this one place, so the rule cannot drift between checks.
    /// </summary>
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// <see cref="PathComparison"/> as a comparer, for the dictionaries and sets keyed by trust
    /// paths and listing names.
    /// </summary>
    internal static readonly StringComparer PathComparer = StringComparer.FromComparison(PathComparison);

    /// <summary>
    /// One set of trusted files and their SHA-256 of the bytes. Snapshot keys are always the trusted
    /// path the operator configured (a symlink is recorded under its own path, not its target).
    /// Values have the shape final-target bar hash-or-sentinel: the SHA-256 (lower hex) for a
    /// regular file's bytes; <c>nonregular</c> when the final target is a directory, device, reparse
    /// point, or path under <c>/dev/</c>, <c>/proc/</c>, <c>/sys/</c>; <c>unreadable: reason</c>
    /// when reading was bounded by the per-file time limit (<c>timeout</c>), a length mismatch
    /// reported at open time (<c>length mismatch</c>), the link pointed at a missing target
    /// (<c>dangling link</c>, recorded under the link path), or any I/O or permission failure
    /// (the exception type). The sentinel <c>-</c> (no value after
    /// the bar) marks a path that does not exist at hash time and is not a symlink. A file the
    /// worker created between the two hashes shows up as a new key just like a content change; a
    /// nonregular/unreadable state appearing in the after-worker snapshot is itself a trust
    /// violation.
    /// </summary>
    internal sealed record Snapshot(Dictionary<string, string> Hashes);

    /// <summary>
    /// Where a trusted path came from. A path can be in the trusted set for more than one reason (an
    /// argv entry that is also a gate.trust literal); <see cref="CollectChainPaths"/> and
    /// <see cref="CollectVolatilePaths"/> record every source when the caller hands them a dictionary.
    /// The sources feed the report-path rule (<see cref="ReportPathViolation"/>): a path the gate names
    /// as its report must not be trusted from any source — argv entry, resolved executable, gate.trust
    /// literal or glob match, prices file, config walk or runs-root walk — unless the operator declared
    /// it a gate output or it lies under the current run directory. Those two exemptions are the
    /// caller's context (the frozen spec and the run directory), so the caller checks them and only a
    /// still-trusted path reaches this rule.
    /// </summary>
    internal enum TrustSource
    {
        /// <summary>
        /// An existing file path named in the gate's argv (after argv[0]), wherever it lives — inside
        /// the git root or not, exactly like argv[0]: a worker that can write the gate's script can
        /// change what the gate runs, no matter where the script sits. No argv entry is ever excluded
        /// from a trust comparison by default: the old runnable-position heuristic missed wrappers
        /// such as <c>env VAR=1 sh gate.sh</c>, so a gate that writes a file named in its argv needs
        /// that file declared in <c>gate.outputs</c> (or the run ends in a trust violation when the
        /// report path is checked).
        /// </summary>
        ArgvEntry,
        /// <summary>
        /// The resolved gate executable (argv[0]) — resolved once in round 1, to the absolute path
        /// the spec carries as <c>ResolvedExecutable</c> and the gate runner starts.
        /// </summary>
        Executable,
        /// <summary>
        /// A gate.trust literal path or glob match.
        /// </summary>
        TrustEntry,
        /// <summary>
        /// The explicit --prices / profile "prices" file.
        /// </summary>
        PricesFile,
        /// <summary>
        /// A dotnet-tools.json / global.json / NuGet config candidate from the config walk.
        /// </summary>
        ConfigWalk,
        /// <summary>
        /// A file from the recursive runs-root walk.
        /// </summary>
        RunsRootWalk,
    }

    /// <summary>
    /// Reads each <paramref name="paths"/> entry and hashes its bytes. Entries that exist as a
    /// non-link record <c>"&lt;path&gt;|&lt;sha-or-sentinel&gt;"</c> (their own path is the final
    /// target). Entries that are symlinks resolve to their final target; the target path appears after
    /// the bar so a retargeted link shows up as a value change. A symlink whose target is unreachable
    /// records <c>"unreadable: dangling link"</c> under the link path. Hashing never blocks or throws:
    /// a hang on a FIFO without a writer is bounded by a per-file timeout (the open + read run on the
    /// thread pool and the wait is capped at <see cref="_hashTimeout"/>).
    /// </summary>
    public static async Task<Snapshot> HashAsync(IEnumerable<string> paths)
    {
        Dictionary<string, string> hashes = new(PathComparer);
        foreach (string path in paths)
        {
            await HashOneAsync(hashes, path).ConfigureAwait(false);
        }

        return new Snapshot(hashes);
    }

    /// <summary>
    /// Time budget for opening a file and hashing its bytes. A FIFO without a writer parks the open
    /// call in the kernel until a writer appears, and a character device like <c>/dev/zero</c> never
    /// returns EOF; a large regular file (a single-binary gate executable can run to hundreds of MB)
    /// simply needs the time to stream through the hash. The trust check must fail closed rather
    /// than hang the chain, so the open + read run on the thread pool and the wait is capped at this
    /// value — generous enough for a real executable of that size, still short enough to fail closed.
    /// Two side effects are accepted: a timed-out open leaves its thread-pool thread blocked for
    /// good (the kernel offers no way to cancel an open), and hashing is sequential
    /// (<c>HashAsync</c> / <c>FirstDiffAsync</c> take one path at a time), so N hung entries cost
    /// N × 30 s before the snapshot fails closed — bounded by how many entries the operator
    /// configured, and still failing closed in the end.
    /// </summary>
    private static readonly TimeSpan _hashTimeout = TimeSpan.FromSeconds(30);

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
            (string? sentinel, string target) = ResolveFinal(path);
            if (sentinel is not null)
            {
                hashes[key] = sentinel;
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

    /// <summary>
    /// Resolves <paramref name="path"/> through its symlink chain. Returns a sentinel when the entry
    /// must not be hashed (a dangling link, or an absent path — <c>-</c>), or null with the final
    /// target path when hashing can proceed.
    /// </summary>
    private static (string? Sentinel, string Target) ResolveFinal(string path)
    {
        // ReadLink first (a dangling link still reports its target), then File.ResolveLinkTarget,
        // which follows the whole chain (a link to a link to a file resolves to the file), so a
        // chained link like /usr/bin/java → /etc/alternatives/java → a JVM is never classified via
        // an intermediate link. Null means the path is not a link.
        string? linkTarget = new FileInfo(path).LinkTarget;
        FileSystemInfo? final;
        try
        {
            final = File.ResolveLinkTarget(path, returnFinalTarget: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing at the path: ResolveLinkTarget reports a missing path as an exception rather
            // than null. A path that is itself a link is a dangling link; otherwise the entry is
            // absent and the sentinel "-" tells the trust check to ignore it.
            return (linkTarget is not null ? "unreadable: dangling link" : "-", path);
        }

        string target = final?.FullName ?? path;

        // Dangling link: the path is a symlink (or a chain of symlinks) whose final target does not
        // exist as a file or a directory. Recorded under the link path so the error message names a
        // path the operator actually configured.
        if (final is not null && !PathExists(target))
        {
            return ("unreadable: dangling link", target);
        }

        // Absent path (and not a link whose target would resolve): the sentinel "-" lets the trust
        // check ignore absent entries instead of treating every missing config file as a failure.
        if (final is null && !PathExists(target))
        {
            return ("-", target);
        }

        return (null, target);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsPseudoFileSystem(string target)
    {
        // Path prefixes, so PathComparison — on a case-insensitive filesystem /DEV/ names /dev/.
        return target.StartsWith("/dev/", PathComparison)
            || target.StartsWith("/proc/", PathComparison)
            || target.StartsWith("/sys/", PathComparison);
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
        // character devices as non-seekable, so the seek check plus the 30-second timeout (which
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
    /// Paths that are in one of the two sets but not the other, in ordinal order: a file that appeared
    /// in, or vanished from, the trusted set since <paramref name="before"/> was taken.
    /// <paramref name="exclusions"/> (the report paths the gate named) is applied to both sides:
    /// excluded paths are dropped from <paramref name="paths"/> and from the snapshot's keys before
    /// comparing, so a gate that rewrites its own report file is not reported as having removed it.
    /// Comparing the path sets needs no hashing (the walk only names paths), so callers run it before
    /// <see cref="FirstDiffAsync"/> and report new or missing files without paying a per-file timeout
    /// for entries that never have to be hashed.
    /// </summary>
    public static List<string> SetDifferences(Snapshot before, IReadOnlyList<string> paths, IReadOnlySet<string>? exclusions = null)
    {
        HashSet<string> now = new(paths, PathComparer);
        IEnumerable<string> added = paths.Where(p => exclusions?.Contains(p) is not true && !before.Hashes.ContainsKey(p));
        IEnumerable<string> removed = before.Hashes.Keys.Where(p => exclusions?.Contains(p) is not true && !now.Contains(p));
        return [.. added.Concat(removed).Distinct(PathComparer).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The trust violation for the gate's report path, or null when writing the report is legitimate.
    /// The launcher used to guess which argv entries were pure gate outputs (argv[1], execute bits) so
    /// a gate could rewrite its own report file without tripping the trust check; the guess cannot see
    /// through a wrapper such as <c>env VAR=1 sh gate.sh</c>, where the report is neither argv[0] nor
    /// argv[1] and carries no execute bit, so the heuristic is dropped. Writing the report is
    /// legitimate exactly when the path lies under the current run directory
    /// (<paramref name="runDirectory"/>, the launcher's own bookkeeping), is a declared gate output
    /// (<paramref name="declaredOutputs"/>, the profile's <c>gate.outputs</c> resolved the same way as
    /// the trust entries), or is not in the trusted set at all. Any remaining trusted source (an argv
    /// entry, the resolved executable, a gate.trust literal/glob, the prices file, the config walk or
    /// the runs-root walk) makes the report path a trusted input that the gate has just announced it
    /// writes over; the returned message ends the chain with <c>error</c> while keeping the gate's
    /// real exit code, count and duration.
    /// </summary>
    public static string? ReportPathViolation(string? reportPath, IReadOnlyDictionary<string, HashSet<TrustSource>> sources,
        IReadOnlySet<string>? declaredOutputs = null, string? runDirectory = null)
    {
        if (string.IsNullOrEmpty(reportPath))
        {
            return null;
        }

        // The report path arrives resolved to an absolute path (Gate resolves it against the working
        // directory); the run directory may still be relative to it. Both sides are canonicalised so
        // the exemption matches a report path that reaches the run directory through a symlink — the
        // resolution's expansion bound failing leaves the exemption unverified and the rule applies.
        if (runDirectory is { Length: > 0 }
            && Canonical(reportPath) is { Resolved: true } reportCanonical
            && Canonical(runDirectory) is { Resolved: true } runDirCanonical
            && IsAtOrUnder(reportCanonical.Path, runDirCanonical.Path))
        {
            return null;
        }

        if (declaredOutputs?.Contains(reportPath) is true)
        {
            return null;
        }

        return sources.ContainsKey(reportPath) ? $"the gate's report path is a trusted input: {reportPath}" : null;
    }

    /// <summary>
    /// One checked path: its value in the new snapshot and whether that value is a bad state
    /// (nonregular or unreadable — the chain treats these as trust violations).
    /// </summary>
    internal sealed record TrustDiff(string Path, string Value, bool IsBad);

    /// <summary>
    /// Hashes <paramref name="paths"/> one at a time against <paramref name="before"/> and stops at
    /// the first entry whose value differs from the snapshot's or that sits in a bad state, so a
    /// planted FIFO or a tampered file ends the check without hashing the rest (a FIFO without a
    /// writer costs a timeout per hashed entry). Returns null when every entry matches its snapshot
    /// value and none is bad. Callers compare the path sets first (<see cref="SetDifferences"/>), so
    /// every path here has a snapshot value; a path without one is reported as changed.
    /// </summary>
    public static async Task<TrustDiff?> FirstDiffAsync(Snapshot before, IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            Dictionary<string, string> hashes = new(PathComparer);
            await HashOneAsync(hashes, path).ConfigureAwait(false);
            string value = hashes[path];
            if (!before.Hashes.TryGetValue(path, out string? expected) || value != expected)
            {
                return new TrustDiff(path, value, IsBadValue(value));
            }

            if (IsBadValue(value))
            {
                return new TrustDiff(path, value, IsBad: true);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a snapshot value is a bad state: any nonregular or unreadable sentinel. Absent paths
    /// (the sentinel <c>-</c>) are not bad on their own: a config file that did not exist before the
    /// worker and still does not exist is not a tampering signal.
    /// </summary>
    public static bool IsBadValue(string value)
    {
        if (value is "-")
        {
            return false;
        }

        // Values are "<target>|<sha-or-sentinel>", but "unreadable: dangling link" omits the
        // target (the target is unreachable, so it is recorded under the link's own path).
        if (value is "unreadable: dangling link")
        {
            return true;
        }

        int sep = value.IndexOf('|', StringComparison.Ordinal);
        string right = sep >= 0 ? value[(sep + 1)..] : value;
        return right is "nonregular" || right.StartsWith("unreadable", StringComparison.Ordinal);
    }

    /// <summary>
    /// One entry directly in a gate PATH directory: its name, its length (0 for a directory — a
    /// subdirectory's own size changes whenever entries are added under it, which is not a change to
    /// the PATH directory's listing), its last write time in UTC, whether it is a symlink, and for a
    /// symlink the link target it carries plus the symlink chain's final target as a full path, that
    /// target's length (0 for a directory, same rule as <c>Length</c>) and its last write time in
    /// UTC. <c>FinalTarget</c> records <c>dangling</c> when the final target does not exist, or
    /// <c>unreadable: &lt;exception type&gt;</c> when resolving it failed (then the length and mtime
    /// are null); it is null when the entry is not a symlink.
    /// </summary>
    internal sealed record PathEntry(string Name, long Length, DateTimeOffset LastWriteTimeUtc, bool IsSymlink, string? LinkTarget, string? FinalTarget, long? FinalTargetLength, DateTimeOffset? FinalTargetLastWriteTimeUtc);

    /// <summary>
    /// One listing of a gate PATH directory. A readable listing carries every direct entry's
    /// <see cref="PathEntry"/> (the GateTrust header's rule); a missing (<c>missing</c>) or unreadable
    /// (<c>unreadable: &lt;exception type&gt;</c>) directory records the reason — which the comparison
    /// treats as a state of its own (the GateTrust header's rule), not as an empty directory.
    /// </summary>
    internal sealed record PathNameListing(bool Readable, string? Reason, IReadOnlyDictionary<string, PathEntry> Entries);

    /// <summary>
    /// Round 1's listings for the gate PATH's directories, keyed by the frozen absolute directory
    /// (the GateTrust header's rule: the listing baseline every later listing is compared against).
    /// </summary>
    internal sealed record PathNamesSnapshot(IReadOnlyDictionary<string, PathNameListing> Directories);

    /// <summary>
    /// The directories the gate's PATH is built from: the launcher's PATH split on the path
    /// separator, keeping only the absolute entries (each trimmed of quotes and normalised with
    /// <see cref="Path.GetFullPath(string)"/>, the same treatment <see cref="Launcher.FindOnPath"/>
    /// applies), deduped, order kept. Relative entries (<c>.</c>, <c>node_modules/.bin</c>) resolve
    /// inside the worker's own tree and are dropped — the GateTrust header's rule.
    /// </summary>
    public static IReadOnlyList<string> GatePathDirectories()
    {
        List<string> dirs = [];
        HashSet<string> dedupe = new(PathComparer);
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = entry.Trim('"');
            // Fully qualified, not merely rooted: on Windows `\foo` and `C:foo` are rooted but still
            // relative to the current drive or directory, and a relative entry would resolve inside
            // the worker's tree.
            if (!Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(dir);
            }
            catch (ArgumentException)
            {
                // A PATH entry that cannot form a valid path is skipped, like a non-existent one.
                continue;
            }

            if (dedupe.Add(full))
            {
                dirs.Add(full);
            }
        }

        return dirs;
    }

    /// <summary>
    /// The gate's PATH: <see cref="GatePathDirectories"/> joined with the path separator. When every
    /// PATH entry is relative nothing is left to join — and an empty PATH is not "no PATH": both
    /// glibc <c>execvp</c> and bash read an empty PATH element as the current directory, so the gate
    /// would resolve bare command names inside the worker's own tree (the very hole the absolute-only
    /// rule closes). This throws a launch error instead of returning an empty string. Frozen on the
    /// gate spec in round 1 and split back by <see cref="SplitGatePath"/>.
    /// </summary>
    public static string BuildGatePath()
    {
        IReadOnlyList<string> dirs = GatePathDirectories();
        if (dirs.Count is 0)
        {
            throw new LaunchException("gate PATH has no absolute entries");
        }

        return string.Join(Path.PathSeparator, dirs);
    }

    /// <summary>
    /// The frozen PATH string split back into its directories — the exact list
    /// <see cref="BuildGatePath"/> was built from.
    /// </summary>
    public static IReadOnlyList<string> SplitGatePath(string gatePath) =>
        [.. gatePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Lists the entries directly in each directory — one
    /// <see cref="FileSystemEnumerable{T}"/> per directory over <see cref="_pathListOptions"/> (no
    /// recursion, no hashing; the GateTrust header's rule). A missing or unreadable directory is
    /// recorded as such (<see cref="PathNameListing"/>), never silently as an empty directory.
    /// </summary>
    public static PathNamesSnapshot SnapshotPathNames(IReadOnlyList<string> directories)
    {
        Dictionary<string, PathNameListing> listings = new(PathComparer);
        foreach (string dir in directories)
        {
            listings[dir] = ListPathDirectory(dir);
        }

        return new PathNamesSnapshot(listings);
    }

    private static PathNameListing ListPathDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return new PathNameListing(Readable: false, Reason: "missing", Entries: _emptyEntries);
        }

        try
        {
            Dictionary<string, PathEntry> entries = new(PathComparer);
            // One enumeration; the transform reads the FileSystemEntry the walk already holds (name,
            // length, last write time, link flag), resolves a symlink's target with one call, and
            // follows the chain to the final target with one more. _pathListOptions, not _walkOptions:
            // an unreadable directory must throw here — on Unix IgnoreInaccessible also silences the
            // root's own open failure, which would record a directory the worker made unreadable as
            // "readable, no entries".
            foreach (PathEntry entry in new FileSystemEnumerable<PathEntry>(
                dir,
                static (ref FileSystemEntry e) =>
                {
                    bool isSymlink = e.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    string? finalTarget = null;
                    long? finalTargetLength = null;
                    DateTimeOffset? finalTargetLastWriteTimeUtc = null;
                    if (isSymlink)
                    {
                        (finalTarget, finalTargetLength, finalTargetLastWriteTimeUtc) = ResolveEntryFinalTarget(e.ToFileSystemInfo().FullName);
                    }

                    return new PathEntry(
                        Name: e.FileName.ToString(),
                        Length: e.IsDirectory ? 0 : e.Length,
                        LastWriteTimeUtc: e.LastWriteTimeUtc,
                        IsSymlink: isSymlink,
                        LinkTarget: isSymlink ? e.ToFileSystemInfo().LinkTarget : null,
                        FinalTarget: finalTarget,
                        FinalTargetLength: finalTargetLength,
                        FinalTargetLastWriteTimeUtc: finalTargetLastWriteTimeUtc);
                },
                _pathListOptions))
            {
                entries[entry.Name] = entry;
            }

            return new PathNameListing(Readable: true, Reason: null, Entries: entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PathNameListing(Readable: false, Reason: $"unreadable: {ex.GetType().Name}", Entries: _emptyEntries);
        }
    }

    /// <summary>
    /// Resolves a gate PATH entry's symlink chain to its final target and reads that target's length
    /// and last write time (UTC) — the three values a symlinked <see cref="PathEntry"/> records, so
    /// a retargeted link and a target rewritten in place are both listing changes. The target comes
    /// back as a full path; a link whose final target does not exist records <c>dangling</c>; a
    /// resolution that fails records <c>unreadable: &lt;exception type&gt;</c>. The target's length
    /// follows the listing's own rule: 0 for a directory, whose size changes whenever entries are
    /// added under it (which is not a change to what the link resolves to).
    /// </summary>
    private static (string FinalTarget, long? FinalTargetLength, DateTimeOffset? FinalTargetLastWriteTimeUtc) ResolveEntryFinalTarget(string path)
    {
        try
        {
            // Follows the whole chain (a link to a link to a file resolves to the file) and wraps the
            // final target path even when nothing is there, so a dangling link is detected by the
            // existence check rather than by an exception.
            FileSystemInfo? final = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (final is null || !PathExists(final.FullName))
            {
                return ("dangling", null, null);
            }

            string full = Path.GetFullPath(final.FullName);
            return Directory.Exists(full)
                ? (full, 0, Directory.GetLastWriteTimeUtc(full))
                : (full, new FileInfo(full).Length, File.GetLastWriteTimeUtc(full));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return ($"unreadable: {ex.GetType().Name}", null, null);
        }
    }

    private static readonly IReadOnlyDictionary<string, PathEntry> _emptyEntries = new Dictionary<string, PathEntry>(PathComparer);

    /// <summary>
    /// The gate PATH listing violations right now, against round 1's frozen listing: a name added to
    /// or removed from a readable directory, a name whose length, last write time, link flag, link
    /// target or final target (with the target's own length and mtime) changed, a readable directory
    /// that turned missing or unreadable, and a directory that was missing or unreadable at round 1
    /// and is readable now (the baseline never saw its entries, so each one is an addition, and the
    /// state change itself is reported even when it is empty — the check must not depend on how
    /// listing errors are classified). Entry changes are named as <c>&lt;dir&gt;/&lt;name&gt;</c>
    /// with what changed, a directory state change as <c>&lt;dir&gt; is now &lt;reason&gt;</c>, in
    /// the frozen directory order; the caller prefixes them like the other trust messages. One
    /// enumeration per directory, no hashing.
    /// </summary>
    public static IReadOnlyList<string> PathNameViolations(PathNamesSnapshot before, IReadOnlyList<string> directories)
    {
        List<string> violations = [];
        foreach (string dir in directories)
        {
            PathNameListing was = before.Directories.TryGetValue(dir, out PathNameListing? listing)
                ? listing
                : new PathNameListing(Readable: false, Reason: "missing", Entries: _emptyEntries);
            PathNameListing now = ListPathDirectory(dir);
            if (!now.Readable)
            {
                if (was.Readable)
                {
                    violations.Add($"{dir} is now {now.Reason}");
                }
                else if (!string.Equals(was.Reason, now.Reason, StringComparison.Ordinal))
                {
                    // Neither listing could read anything, but the directory's state still changed
                    // (missing ↔ unreadable, or a different unreadable reason): a change of state is
                    // a violation even when the listing is empty.
                    violations.Add($"{dir} was {was.Reason}, is now {now.Reason}");
                }

                continue;
            }

            if (!was.Readable)
            {
                // Readable now, invisible to the baseline: the state change (even for an empty
                // directory) plus every entry as an addition.
                violations.Add($"{dir} was {was.Reason}, is now readable");
                foreach (string name in now.Entries.Keys.Order(StringComparer.Ordinal))
                {
                    violations.Add(Path.Combine(dir, name));
                }

                continue;
            }

            EntryViolations(dir, was.Entries, now.Entries, violations);
        }

        return violations;
    }

    /// <summary>
    /// The entry-level violations for one readable directory against its readable baseline: added
    /// names, removed names, and surviving names whose recorded <see cref="PathEntry"/> differs.
    /// </summary>
    private static void EntryViolations(string dir, IReadOnlyDictionary<string, PathEntry> was, IReadOnlyDictionary<string, PathEntry> now, List<string> violations)
    {
        foreach (string name in now.Keys.Where(n => !was.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            violations.Add(Path.Combine(dir, name));
        }

        foreach (string name in was.Keys.Where(n => !now.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            violations.Add(Path.Combine(dir, name));
        }

        foreach (string name in was.Keys.Where(n => now.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            if (EntryChange(was[name], now[name]) is { } change)
            {
                violations.Add($"{Path.Combine(dir, name)} {change}");
            }
        }
    }

    /// <summary>
    /// What changed between round 1's record of a surviving entry and now, or null when nothing did:
    /// the link flag (a plain file swapped for a symlink or back), a symlink's link target, its
    /// final target (including <c>dangling</c> ↔ a real target), that target's length or its last
    /// write time, and the entry's own length or last write time.
    /// </summary>
    private static string? EntryChange(PathEntry was, PathEntry now)
    {
        List<string> changes = [];
        if (was.IsSymlink != now.IsSymlink)
        {
            changes.Add(was.IsSymlink ? "stopped being a symlink" : "became a symlink");
        }
        else if (was.IsSymlink)
        {
            if (!string.Equals(was.LinkTarget, now.LinkTarget, PathComparison))
            {
                changes.Add($"changed its link target from '{was.LinkTarget}' to '{now.LinkTarget}'");
            }

            if (!string.Equals(was.FinalTarget, now.FinalTarget, PathComparison))
            {
                changes.Add($"changed its final target from '{was.FinalTarget}' to '{now.FinalTarget}'");
            }

            if (was.FinalTargetLength != now.FinalTargetLength)
            {
                changes.Add(string.Create(CultureInfo.InvariantCulture, $"changed its target's length from {was.FinalTargetLength} to {now.FinalTargetLength}"));
            }

            if (was.FinalTargetLastWriteTimeUtc != now.FinalTargetLastWriteTimeUtc)
            {
                changes.Add(string.Create(CultureInfo.InvariantCulture, $"changed its target's mtime from {was.FinalTargetLastWriteTimeUtc:o} to {now.FinalTargetLastWriteTimeUtc:o}"));
            }
        }

        if (was.Length != now.Length)
        {
            changes.Add($"changed length from {was.Length} to {now.Length}");
        }

        if (was.LastWriteTimeUtc != now.LastWriteTimeUtc)
        {
            changes.Add($"changed mtime from {was.LastWriteTimeUtc:o} to {now.LastWriteTimeUtc:o}");
        }

        return changes.Count is 0 ? null : string.Join(", ", changes);
    }

    /// <summary>
    /// The part of the trust set that is fixed for a whole gate chain: the resolved gate executable,
    /// the existing argv file entries, and the <c>gate.trust</c> entries (literal paths and glob
    /// matches). The launcher resolves this list once, before round 1's worker runs, and reuses it
    /// for every later hash, so glob matches and argv entries never pick up files the worker or the
    /// gate created later. The gate executable is handed in via <paramref name="resolvedExecutable"/>
    /// — round 1's single resolution, the exact string the spec freezes as <c>ResolvedExecutable</c>
    /// and the gate runner starts — so the hashed path and the started path can never diverge;
    /// <paramref name="gateCommand"/> is only the fallback for callers that have no resolved path.
    /// The glob walk skips the git root's top-level <c>.git</c> directory and
    /// the runs root; a glob that matches no file adds a warning to <paramref name="warnings"/>
    /// when that list is given. When <paramref name="sources"/> is given, every add records its
    /// <see cref="TrustSource"/> in it — even for a path the per-collector dedupe skips, since the
    /// report-path rule must see every source a path is trusted from.
    /// </summary>
    public static List<string> CollectChainPaths(string runsRoot, string gitRoot, string gateWorkingDirectory, IReadOnlyList<string> gateCommand,
        IReadOnlyList<string>? extraTrust = null, string? extraPricesFile = null, List<string>? warnings = null,
        Dictionary<string, HashSet<TrustSource>>? sources = null, string? resolvedExecutable = null)
    {
        List<string> paths = [];
        HashSet<string> dedupe = new(PathComparer);
        void Add(string p)
        {
            if (!dedupe.Add(p))
            {
                return;
            }

            paths.Add(p);
        }

        // Source recording is deliberately independent of the dedupe above: a path named twice (an
        // argv entry that is also a gate.trust literal) is trusted from both sources, and the
        // report-path rule needs to see that.
        void AddTracked(string p, TrustSource source)
        {
            Add(p);
            if (sources is null)
            {
                return;
            }

            if (!sources.TryGetValue(p, out HashSet<TrustSource>? set))
            {
                set = [];
                sources[p] = set;
            }

            set.Add(source);
        }

        AddResolvedExecutable(resolvedExecutable, gateCommand, gateWorkingDirectory, p => AddTracked(p, TrustSource.Executable));
        AddArgvEntries(gateCommand, gateWorkingDirectory, p => AddTracked(p, TrustSource.ArgvEntry));
        AddExtraTrust(extraTrust, gitRoot, gateWorkingDirectory, runsRoot, p => AddTracked(p, TrustSource.TrustEntry), warnings);
        AddPricesFile(extraPricesFile, p => AddTracked(p, TrustSource.PricesFile));
        return paths;
    }

    /// <summary>
    /// The part of the trust set the launcher re-collects before every hash: the runs-root files and
    /// the config-file walk (working directory up to the git root). These are meant to catch new
    /// files, so they are walked again each time — unlike the fixed chain paths
    /// (<see cref="CollectChainPaths"/>), whose glob matches and argv entries would otherwise pick up
    /// files the worker or the gate created. When <paramref name="sources"/> is given, every add
    /// records its <see cref="TrustSource"/> in it (see <see cref="CollectChainPaths"/>).
    /// </summary>
    public static List<string> CollectVolatilePaths(string runsRoot, string gateWorkingDirectory, string gitRoot,
        Dictionary<string, HashSet<TrustSource>>? sources = null)
    {
        List<string> paths = [];
        HashSet<string> dedupe = new(PathComparer);
        void Add(string p)
        {
            if (!dedupe.Add(p))
            {
                return;
            }

            paths.Add(p);
        }

        void AddTracked(string p, TrustSource source)
        {
            Add(p);
            if (sources is null)
            {
                return;
            }

            if (!sources.TryGetValue(p, out HashSet<TrustSource>? set))
            {
                set = [];
                sources[p] = set;
            }

            set.Add(source);
        }

        AddRunsRootFiles(runsRoot, p => AddTracked(p, TrustSource.RunsRootWalk));
        AddConfigFileWalk(p => AddTracked(p, TrustSource.ConfigWalk), gateWorkingDirectory, gitRoot);
        return paths;
    }

    /// <summary>
    /// Options for the per-directory listing the trust set builds: no recursion (<see cref="WalkFiles"/>
    /// recurses by hand so subtrees can be skipped by name or because they are symlinks), skip
    /// directories the walk cannot read (a missing entry drops out of the walk and the path-set
    /// comparison reports it, so this fails closed), and skip no attributes: a symlinked file stays
    /// in the walk (it is hashed through <see cref="ResolveFinal"/>, which records the final target
    /// in the value), while a symlinked directory is never descended into (<see cref="WalkFiles"/>
    /// checks <see cref="FileSystemInfo.LinkTarget"/>, so a link cannot pull a tree outside the
    /// walked root into the trust set).
    /// </summary>
    private static readonly EnumerationOptions _walkOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
    };

    /// <summary>
    /// Options for the gate PATH directory listings (<see cref="ListPathDirectory"/>): no recursion,
    /// nothing skipped — neither inaccessible directories (an unreadable directory must reach the
    /// listing's <c>unreadable:</c> branch; on Unix <see cref="EnumerationOptions.IgnoreInaccessible"/>
    /// also silences the root's own open failure and would record it as a readable empty directory)
    /// nor attributes (no entry name drops out of the listing). Deliberately not
    /// <see cref="_walkOptions"/>, whose skips are what the trust walk wants.
    /// </summary>
    private static readonly EnumerationOptions _pathListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.None,
    };

    /// <summary>
    /// Enumerates every file under <paramref name="root"/> without throwing: unreadable directories
    /// are skipped (<see cref="EnumerationOptions.IgnoreInaccessible"/>), a directory that vanishes
    /// mid-walk ends just that directory's listing, a symlinked directory is never descended into
    /// (its <see cref="FileSystemInfo.LinkTarget"/> names the outside tree it would pull in),
    /// symlinked files are enumerated (they are hashed through <see cref="ResolveFinal"/>), and
    /// <paramref name="skipDirectory"/> prunes subtrees by name.
    /// </summary>
    private static IEnumerable<string> WalkFiles(string root, Func<string, bool>? skipDirectory)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            List<string> files = [];
            List<string> subDirs = [];
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*", _walkOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The directory vanished mid-walk; nothing under it can be hashed anyway.
            }

            try
            {
                subDirs.AddRange(Directory.EnumerateDirectories(dir, "*", _walkOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            foreach (string file in files)
            {
                yield return file;
            }

            foreach (string sub in subDirs)
            {
                // A symlinked directory is never followed (it would pull a tree outside the walked
                // root into the trust set); symlinked files are yielded above and hashed through
                // ResolveFinal.
                if (new DirectoryInfo(sub).LinkTarget is not null)
                {
                    continue;
                }

                if (skipDirectory?.Invoke(sub) is not true)
                {
                    pending.Push(sub);
                }
            }
        }
    }

    /// <summary>
    /// The bound on symlink expansions in one resolution (<see cref="Canonical"/>), where
    /// realpath(3) gives up with ELOOP: 40 expansions cover any real path and stop a link loop from
    /// walking forever.
    /// </summary>
    private const int MaxLinkExpansions = 40;

    /// <summary>
    /// A path resolved with realpath(3) semantics by <see cref="Canonical"/>: the absolute path with
    /// every symlink component expanded, plus <see cref="Links"/>, every link path the resolution
    /// expanded, in expansion order — a link on the way can be refused even when the resolved path
    /// itself is harmless, because the worker can retarget a link it can write no matter where the
    /// resolution lands today.
    /// </summary>
    internal sealed record CanonicalPath(string Path, IReadOnlyList<string> Links)
    {
        /// <summary>
        /// Null when the resolution completed; otherwise the sentinel for why it stopped —
        /// <c>unreadable: too many links</c> at the expansion bound, or the exception type when the
        /// path or a link target could not be made absolute. <see cref="Path"/> then holds the
        /// resolution reached so far, and callers that must not guess treat it as unresolved.
        /// </summary>
        public string? Unreadable { get; init; }

        /// <summary>
        /// Whether the resolution completed.
        /// </summary>
        public bool Resolved => Unreadable is null;
    }

    /// <summary>
    /// The canonical form of <paramref name="path"/> with realpath(3) semantics, in pure managed
    /// code: the path is made absolute without a lexical "." / ".." collapse, then walked over a
    /// stack of resolved components and a queue of pending raw components. A component that is a
    /// symlink (its <see cref="FileSystemInfo.LinkTarget"/>) queues its raw target — split into
    /// components without normalising — in front of the pending walk: a relative target resolves
    /// against the stack already walked, an absolute target clears the stack and restarts at the
    /// target's own root (a different drive or UNC share resets the walk to that root), and a
    /// target rooted on a drive whose current directory the walk cannot know ("C:foo") leaves the
    /// resolution <c>unreadable</c>. "." is dropped and ".." pops the resolved stack (never above
    /// the root) when they are processed — after any link expansions queued ahead of them — so
    /// <c>link/..</c> climbs out of the link target, not the link. A non-existent tail is appended
    /// as-is, lexically normalised. Expansion stops at <see cref="MaxLinkExpansions"/> with
    /// <c>unreadable: too many links</c>. No P/Invoke: one <see cref="FileSystemInfo.LinkTarget"/>
    /// read per component.
    /// </summary>
    internal static CanonicalPath Canonical(string path)
    {
        // Make the path absolute without a lexical "." / ".." collapse: those must resolve against
        // what the components really are, so "/link/.." climbs out of the link's target. Only forms
        // that are not fully qualified go through GetFullPath (they need the current directory or
        // drive to become absolute, and GetFullPath collapses their ".." lexically — the walk still
        // resolves whatever symlink components remain).
        try
        {
            return Canonicalize(Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return new CanonicalPath(path, []) { Unreadable = $"unreadable: {ex.GetType().Name}" };
        }
    }

    /// <summary>
    /// The component walk over an already absolute path (<see cref="Canonical"/>'s body): a stack
    /// of resolved, link-free components and a queue of pending raw components. A link expansion
    /// queues its target's raw components in front of the tail that followed the link, so they are
    /// processed — and any ".." in them resolved against real directories — before the tail.
    /// </summary>
    private static CanonicalPath Canonicalize(string absolute)
    {
        string root = Path.GetPathRoot(absolute) ?? string.Empty;
        List<string> pending = ComponentsToWalk(absolute[root.Length..]);
        List<string> resolved = [];
        List<string> links = [];
        while (pending.Count > 0)
        {
            // The queue's head is the next component to walk.
            string name = Pop(pending);
            if (name is ".")
            {
                continue;
            }

            if (name is "..")
            {
                // Everything on the stack holds resolved, link-free names, so popping is the
                // physical parent; the filesystem root clamps, like realpath. A ".." that follows
                // a link is processed after the target's components (they were queued in front of
                // it), so it climbs out of the link target, not the link.
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            string candidate = Compose(root, [.. resolved, name]);
            string? target = ReadLinkTarget(candidate);
            if (string.IsNullOrEmpty(target))
            {
                // Not a link — including a component that does not exist: from here on the tail is
                // appended as-is.
                resolved.Add(name);
                continue;
            }

            if (links.Count >= MaxLinkExpansions)
            {
                return new CanonicalPath(Compose(root, resolved), links) { Unreadable = "unreadable: too many links" };
            }

            links.Add(candidate);
            if (PushLinkTarget(pending, resolved, ref root, target) is { } unreadable)
            {
                return new CanonicalPath(Compose(root, resolved), links) { Unreadable = unreadable };
            }
        }

        return new CanonicalPath(Compose(root, resolved), links);
    }

    /// <summary>
    /// Pops the front of the walk (the queue's head).
    /// </summary>
    private static string Pop(List<string> pending)
    {
        string name = pending[0];
        pending.RemoveAt(0);
        return name;
    }

    /// <summary>
    /// The components of one path segment still to walk, in walk order.
    /// </summary>
    private static List<string> ComponentsToWalk(string segment)
    {
        return
        [
            .. segment.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries),
        ];
    }

    /// <summary>
    /// Queues a link target's raw components at the front of the walk without normalising them:
    /// its "." and ".." resolve against real directories when they are processed, and its own
    /// components are resolved like any other — a nested link expands where the walk meets it. A
    /// relative target resolves against the stack already walked; a rooted target restarts the
    /// walk at its own root, so a different drive or UNC share resets the walk to that root. A
    /// target rooted on a drive without a usable root ("C:foo") bases on that drive's own current
    /// directory, which this walk cannot reconstruct: the unreadable sentinel is returned so
    /// callers fail closed rather than guess. Returns the unreadable sentinel, or null when the
    /// target was queued.
    /// </summary>
    private static string? PushLinkTarget(List<string> pending, List<string> resolved, ref string root, string target)
    {
        try
        {
            if (Path.IsPathRooted(target) && !Path.IsPathFullyQualified(target))
            {
                // Rooted without a volume ("\foo", "C:foo"; Windows only): resolving would drop the volume.
                return $"unreadable: {nameof(ArgumentException)}";
            }

            string? targetRoot = Path.GetPathRoot(target);
            if (targetRoot is { Length: > 0 } && target.StartsWith(targetRoot, PathComparison))
            {
                // A root that ends in a separator names its own tree ("/", "C:\", "\\s\c\"): the
                // walk restarts there. A bare drive ("C:") does not: that target is relative to
                // the drive's current directory, which nothing in the walk can know.
                if (!targetRoot.EndsWith(Path.DirectorySeparatorChar) && !targetRoot.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    return $"unreadable: {nameof(ArgumentException)}";
                }

                resolved.Clear();
                root = targetRoot;
                pending.InsertRange(0, ComponentsToWalk(target[targetRoot.Length..]));
                return null;
            }

            if (Path.IsPathRooted(target))
            {
                // Rooted on the walked root itself ("\foo"): restart from the root, keep the root.
                resolved.Clear();
                pending.InsertRange(0, ComponentsToWalk(target.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                return null;
            }

            // Relative: the target's components resolve against the walked prefix.
            pending.InsertRange(0, ComponentsToWalk(target));
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return $"unreadable: {ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Joins the root (which always ends in a separator) with the resolved component stack.
    /// </summary>
    private static string Compose(string root, IReadOnlyList<string> parts) =>
        parts.Count is 0 ? root : root + string.Join(Path.DirectorySeparatorChar, parts);

    /// <summary>
    /// The symlink target of <paramref name="path"/>, or null when the path is not a link (or its
    /// target cannot be read — the component then stays as written, and a link the trust check
    /// cannot read cannot redirect it either). <see cref="FileSystemInfo.LinkTarget"/> reads the
    /// link itself without following it and reports a dangling link's target like any other; the
    /// file/directory distinction only picks the <see cref="FileSystemInfo"/> kind, which matters
    /// on Windows.
    /// </summary>
    private static string? ReadLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> or lies under it, compared with
    /// <see cref="PathComparison"/>. Callers pass canonical paths (<see cref="Canonical"/>) on both
    /// sides: a lexical prefix test can only be trusted when neither side hides a symlinked
    /// directory under an innocent-looking name.
    /// </summary>
    internal static bool IsAtOrUnder(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        string prefix = root[^1] == Path.DirectorySeparatorChar ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static void AddRunsRootFiles(string runsRoot, Action<string> add)
    {
        // Walk keys are absolute, like every other trust key (trust entries, argv entries and
        // declared outputs all resolve to full paths): the report-path rule and the collision check
        // compare absolute paths, so a relative --runs-root must not record keys such as
        // ".lean-worker/profiles.json" that no absolute comparison can ever match.
        string root = Path.GetFullPath(runsRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        string ledger = Path.Combine(root, "runs.jsonl");

        // The launcher's own bookkeeping lives under runs/, inbox/ and system/ and is never a gate
        // input, and runs.jsonl is the ledger (one record per run, append-only): do not descend
        // into those subtrees, so a planted or unreadable directory under them cannot stall the walk.
        HashSet<string> skip = new(PathComparer)
        {
            Path.Combine(root, "runs"),
            Path.Combine(root, "inbox"),
            Path.Combine(root, "system"),
        };

        foreach (string file in WalkFiles(root, dir => skip.Contains(dir)))
        {
            if (!string.Equals(file, ledger, PathComparison))
            {
                add(file);
            }
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
            if (string.Equals(current, toDir, PathComparison))
            {
                break;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, PathComparison))
            {
                // Reached the filesystem root before reaching the git root; emit one last set so the
                // top-level git root config files are still trusted.
                if (!string.Equals(current, toDir, PathComparison))
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

    /// <summary>
    /// Adds the gate executable to the trust set: <paramref name="resolvedExecutable"/> (round 1's
    /// single resolution, already absolute) when the caller has one, otherwise a fresh resolution
    /// from the command for callers that have not resolved it yet.
    /// </summary>
    private static void AddResolvedExecutable(string? resolvedExecutable, IReadOnlyList<string> gateCommand, string gateWorkingDirectory, Action<string> add)
    {
        string exe = resolvedExecutable ?? ResolveExecutable(gateCommand, gateWorkingDirectory);
        if (exe.Length is 0)
        {
            return;
        }

        add(exe);
    }

    /// <summary>
    /// Resolves the gate's argv[0] to an absolute path: a path with a directory part resolves against
    /// the gate working directory (<c>Path.GetFullPath</c>, so <c>./tools/gate.sh</c> cannot fall back
    /// to .NET's exe-directory-adjacent lookup at start time), a bare name on PATH
    /// (<see cref="Launcher.FindOnPath"/> already returns absolute results, so a relative PATH entry
    /// cannot produce a relative trust key). Returns the empty string when argv[0] cannot be resolved;
    /// <see cref="ResolveExecutableStrict"/> turns that into a launch error.
    /// </summary>
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

        return Launcher.FindOnPath(first) ?? string.Empty;
    }

    /// <summary>
    /// The one resolution of the gate's argv[0], run exactly once in round 1 before the worker starts:
    /// the returned absolute path is hashed as <see cref="TrustSource.Executable"/>, frozen on the
    /// gate spec (<c>ResolvedExecutable</c>) and started by <c>Gate.BuildStartInfo</c> verbatim. The
    /// gate runner never searches PATH again, so an executable the worker plants in an earlier PATH
    /// directory after this lookup cannot be swapped in for a later round. An argv[0] that cannot be
    /// resolved is a launch error (exit 2) before the worker: a bare name that no PATH entry has
    /// throws <c>not found on PATH</c>; an entry with a directory part that does not exist as a file
    /// (its <c>GetFullPath</c> resolution succeeds without the file ever existing) throws
    /// <c>not found</c> — spent worker money for a gate that cannot start is what the check avoids.
    /// </summary>
    internal static string ResolveExecutableStrict(IReadOnlyList<string> gateCommand, string gateWorkingDirectory)
    {
        string first = gateCommand.Count is 0 ? string.Empty : gateCommand[0];
        string resolved = ResolveExecutable(gateCommand, gateWorkingDirectory);
        if (resolved.Length is 0 && !HasDirectorySeparator(first))
        {
            throw new LaunchException($"gate executable '{first}' not found on PATH");
        }

        if (resolved.Length is 0 || !File.Exists(resolved))
        {
            throw new LaunchException($"gate executable '{first}' not found");
        }

        return resolved;
    }

    /// <summary>
    /// The existing file paths named in the gate's argv after argv[0], resolved against the gate
    /// working directory, wherever they live: argv[0] is trusted wherever it resolves, and an argv
    /// entry the worker can write changes what the gate runs just the same (a wrapper such as
    /// <c>env X=1 sh /opt/gates/gate.sh</c> hides the script behind env, so no position or
    /// inside-the-repo rule can pick it out). Entries that do not exist as files are skipped.
    /// </summary>
    private static void AddArgvEntries(IReadOnlyList<string> gateCommand, string gateWorkingDirectory, Action<string> add)
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

            add(full);
        }
    }

    private static bool HasDirectorySeparator(string s)
    {
        return s.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || s.Contains(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the operator's <c>gate.trust</c> entries against the git root (or the gate working
    /// directory when there is no git root) and adds them to the trust set. Literal entries (no glob
    /// characters) are added as-is, even when the file does not exist; a literal that names a
    /// directory is refused with a <see cref="LaunchException"/> naming <c>gate.trust[&lt;i&gt;]</c>
    /// (a directory hashes as non-regular and would fail every run with "is not a regular file", a
    /// configuration mistake better caught before the worker starts). Glob matches are limited to
    /// files that exist before the worker runs (WriteScope.InScope-style matching). A glob entry
    /// that matches no file adds <c>gate.trust[&lt;i&gt;] '&lt;glob&gt;' matched no files</c> to
    /// <paramref name="warnings"/> when that list is given.
    /// </summary>
    private static void AddExtraTrust(IReadOnlyList<string>? entries, string gitRoot, string gateWorkingDirectory, string runsRoot, Action<string> add, List<string>? warnings = null)
    {
        if (entries is null || entries.Count is 0)
        {
            return;
        }

        // Trust entries are repo-relative; the git root takes precedence when it is usable, the gate
        // working directory is the fallback so a non-git launch can still pin files.
        string anchor = Directory.Exists(gitRoot) ? gitRoot : gateWorkingDirectory;
        string skipRunsRoot = Path.GetFullPath(runsRoot);

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IndexOfAny(['*', '?']) >= 0)
            {
                AddGlobEntry(entries[i], i, anchor, skipRunsRoot, add, warnings ?? []);
            }
            else
            {
                AddLiteralEntry(entries[i], i, anchor, add);
            }
        }
    }

    private static void AddLiteralEntry(string entry, int index, string anchor, Action<string> add)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(anchor, entry));
        }
        catch (ArgumentException)
        {
            return;
        }

        if (Directory.Exists(full))
        {
            throw new LaunchException(
                $"gate.trust[{index.ToString(CultureInfo.InvariantCulture)}] '{entry}' is a directory: {full}");
        }

        add(full);
    }

    // Glob: enumerate every file under <anchor> and add the ones the glob matches that exist before
    // the worker runs (Absence here is not a violation on its own; the trust check also catches
    // "present and later gone"). A glob entry that ends up matching nothing is not an error, but the
    // operator should hear about it: a warning is recorded and noted in the result block.
    private static void AddGlobEntry(string entry, int index, string anchor, string skipRunsRoot, Action<string> add, List<string> warnings)
    {
        Regex matcher;
        try
        {
            matcher = WriteScope.BuildGlobRegex(entry);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!Directory.Exists(anchor))
        {
            return;
        }

        List<string> matches = [.. WalkFiles(anchor, dir => SkipWalkedDirectory(dir, anchor, skipRunsRoot))
            .Where(file => matcher.IsMatch(Path.GetRelativePath(anchor, file).Replace('\\', '/'))),];

        foreach (string file in matches)
        {
            add(file);
        }

        warnings.AddRange(matches.Count is 0 ? [$"gate.trust[{index.ToString(CultureInfo.InvariantCulture)}] '{entry}' matched no files"] : []);
    }

    /// <summary>
    /// Whether a walked subdirectory is pruned from the glob walk: the launcher's runs root (its own
    /// bookkeeping, including this run's gate.json, must never match a trust glob), and the git
    /// root's own top-level <c>.git</c> — the metadata store is walked for names only and a glob
    /// such as **/*.json would otherwise match files git itself created and trip every run. A
    /// directory named <c>.git</c> deeper in the tree is an ordinary directory and stays in the walk.
    /// The runs-root comparison runs on canonical paths: the walk never descends into a symlinked
    /// directory, but the anchor itself can sit behind symlinks, and only the canonical form of both
    /// sides names the same tree. A side that cannot be resolved keeps the directory in the walk.
    /// </summary>
    private static bool SkipWalkedDirectory(string dir, string anchor, string skipRunsRoot)
    {
        return (Path.GetFileName(dir) is ".git"
            && Path.GetDirectoryName(dir) is { } parent
            && string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(anchor), PathComparison))
            || (Canonical(dir) is { Resolved: true } dirCanonical
                && Canonical(skipRunsRoot) is { Resolved: true } runsCanonical
                && IsAtOrUnder(dirCanonical.Path, runsCanonical.Path));
    }

    private static void AddPricesFile(string? pricesFile, Action<string> add)
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

        // Always add the explicit --prices / profile "prices" path, even when it lies under the runs
        // root: the recursive walk there skips runs/, inbox/, system/ and runs.jsonl, so a prices
        // file in one of those places would otherwise never be hashed. CollectPaths dedupes.
        add(full);
    }
}
