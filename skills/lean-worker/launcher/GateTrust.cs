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
//   - the ResolveLinkTarget check at the top of HashOneAsync (a path whose final link target does not
//     exist as a file or directory is a dangling link → "unreadable: dangling link" under the link path)
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

using System.Globalization;
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
    /// Where a trusted path came from. A path can be in the trusted set for more than one reason (an
    /// argv entry that is also a gate.trust literal); <see cref="CollectChainPaths"/> and
    /// <see cref="CollectVolatilePaths"/> record every source when the caller hands them a dictionary.
    /// The gate's report path may be dropped from a trust comparison only when its sources are exactly
    /// <see cref="TrustSource.ArgvEntry"/> — a gate writing its own report file named in its argv —
    /// and the argv entry is not runnable (<see cref="MarkRunnableArgv"/>): the exclusion must never
    /// cover something the gate runs. Any other source (the resolved executable, a gate.trust
    /// literal/glob, the prices file, the config walk or the runs-root walk) makes the path a trusted
    /// input that a report written over it would corrupt. <see cref="ReportPathViolation"/> enforces
    /// that rule.
    /// </summary>
    internal enum TrustSource
    {
        /// <summary>
        /// An existing file path named in the gate's argv (after argv[0]), under the git root.
        /// </summary>
        ArgvEntry,
        /// <summary>
        /// The resolved gate executable (argv[0]).
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
        HashSet<string> now = new(paths, StringComparer.Ordinal);
        IEnumerable<string> added = paths.Where(p => exclusions?.Contains(p) is not true && !before.Hashes.ContainsKey(p));
        IEnumerable<string> removed = before.Hashes.Keys.Where(p => exclusions?.Contains(p) is not true && !now.Contains(p));
        return [.. added.Concat(removed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The trust violation for dropping <paramref name="reportPath"/> from a trust comparison, or
    /// null when the exclusion is allowed. The report path (resolved from the gate's stdout after
    /// the gate runs) is dropped from the checked list and from the snapshot's keys, so a gate that
    /// writes its report over a trusted input would otherwise hide the change. The exclusion is
    /// therefore legal only when the path sits in the trusted set solely as an argv file entry that
    /// is not runnable — the gate writing its own report file named in its argv, a pure output. A
    /// path in <paramref name="runnable"/> (<see cref="MarkRunnableArgv"/>) is something the gate
    /// executes, and the exclusion is exactly what would hide an input the worker swapped and the
    /// gate then ran; the same holds when any other source (the resolved executable, a gate.trust
    /// literal/glob, the prices file, the config walk or the runs-root walk) put the path in the
    /// trusted set. A path that is not in the trusted set has nothing to exclude (null); otherwise
    /// the returned message names the path and the caller ends the chain with <c>error</c> instead
    /// of comparing a set the report path no longer takes part in.
    /// </summary>
    public static string? ReportPathViolation(string? reportPath, IReadOnlyDictionary<string, HashSet<TrustSource>> sources, IReadOnlySet<string>? runnable = null)
    {
        if (reportPath is null || reportPath.Length is 0 || !sources.TryGetValue(reportPath, out HashSet<TrustSource>? from))
        {
            return null;
        }

        bool argvOutputEntry = from.Count is 1 && from.Contains(TrustSource.ArgvEntry) && runnable?.Contains(reportPath) is not true;
        return argvOutputEntry ? null : $"the gate's report path is a trusted input: {reportPath}";
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
            Dictionary<string, string> hashes = new(StringComparer.Ordinal);
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
    /// The paths the gate chain treats as trusted inputs (the composition of
    /// <see cref="CollectChainPaths"/> and <see cref="CollectVolatilePaths"/>): every file under
    /// <paramref name="runsRoot"/> (recursively, except the launcher-owned subtrees <c>runs/</c>,
    /// <c>inbox/</c>, <c>system/</c> and the ledger <c>runs.jsonl</c>), <c>dotnet-tools.json</c>,
    /// <c>.config/dotnet-tools.json</c>, <c>global.json</c> and the three
    /// <c>NuGet.config</c>/<c>NuGet.Config</c>/<c>nuget.config</c> casings at every directory from
    /// <paramref name="gateWorkingDirectory"/> up to <paramref name="gitRoot"/> (deduped), the
    /// resolved gate executable (argv[0]: PATH when the entry has no directory part, else resolved
    /// against <paramref name="gateWorkingDirectory"/> — wherever it lives, inside the repo or not),
    /// every remaining argv entry that is an existing file path under <paramref name="gitRoot"/>
    /// (resolved against <paramref name="gateWorkingDirectory"/>), any <paramref name="extraTrust"/>
    /// paths or globs the operator pinned (literal entries included even when absent, glob matches
    /// limited to files that exist before the worker runs; a glob that matches no file adds
    /// <c>gate.trust[&lt;i&gt;] '&lt;glob&gt;' matched no files</c> to <paramref name="warnings"/>
    /// when that list is given), and the price book file at
    /// <paramref name="extraPricesFile"/> whenever one is given (even under
    /// <paramref name="runsRoot"/>, whose walk skips runs/, inbox/, system/ and runs.jsonl). When
    /// <paramref name="sources"/> is given, every add records its <see cref="TrustSource"/> in it
    /// (merged across collectors, so a path named twice is trusted from both). When
    /// <paramref name="runnable"/> is given, the gate's runnable inputs are added to it (see
    /// <see cref="MarkRunnableArgv"/>).
    /// </summary>
    public static List<string> CollectPaths(string runsRoot, string gitRoot, string gateWorkingDirectory, IReadOnlyList<string> gateCommand,
        IReadOnlyList<string>? extraTrust = null, string? extraPricesFile = null, List<string>? warnings = null,
        Dictionary<string, HashSet<TrustSource>>? sources = null, HashSet<string>? runnable = null)
    {
        List<string> paths = CollectChainPaths(runsRoot, gitRoot, gateWorkingDirectory, gateCommand, extraTrust, extraPricesFile, warnings, sources, runnable);
        HashSet<string> dedupe = new(paths, StringComparer.Ordinal);
        foreach (string path in CollectVolatilePaths(runsRoot, gateWorkingDirectory, gitRoot, sources))
        {
            if (dedupe.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// The part of the trust set that is fixed for a whole gate chain: the resolved gate executable,
    /// the existing argv file entries, and the <c>gate.trust</c> entries (literal paths and glob
    /// matches). The launcher resolves this list once, before round 1's worker runs, and reuses it
    /// for every later hash, so glob matches and argv entries never pick up files the worker or the
    /// gate created later. The glob walk skips the git root's top-level <c>.git</c> directory and
    /// the runs root; a glob that matches no file adds a warning to <paramref name="warnings"/>
    /// when that list is given. When <paramref name="sources"/> is given, every add records its
    /// <see cref="TrustSource"/> in it — even for a path the per-collector dedupe skips, since the
    /// report-path rule must see every source a path is trusted from. When
    /// <paramref name="runnable"/> is given, the gate's runnable inputs are added to it at the same
    /// moment, from the same command and working directory (<see cref="MarkRunnableArgv"/>), so the
    /// report-path rule never depends on what the gate or the worker did later.
    /// </summary>
    public static List<string> CollectChainPaths(string runsRoot, string gitRoot, string gateWorkingDirectory, IReadOnlyList<string> gateCommand,
        IReadOnlyList<string>? extraTrust = null, string? extraPricesFile = null, List<string>? warnings = null,
        Dictionary<string, HashSet<TrustSource>>? sources = null, HashSet<string>? runnable = null)
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

        // Runnable marking runs beside the adds, not inside them: the rules scan every argv entry
        // (also ones outside the git root, which the trust set never holds, because the position of
        // the first non-file argument decides which later entries an interpreter could still run).
        if (runnable is not null)
        {
            MarkRunnableArgv(gateCommand, gateWorkingDirectory, runnable);
        }

        AddResolvedExecutable(gateCommand, gateWorkingDirectory, p => AddTracked(p, TrustSource.Executable));
        AddArgvEntries(gateCommand, gateWorkingDirectory, gitRoot, p => AddTracked(p, TrustSource.ArgvEntry));
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
        HashSet<string> dedupe = new(StringComparer.Ordinal);
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
    /// Whether <paramref name="path"/> is <paramref name="root"/> or lies under it (ordinal).
    /// </summary>
    private static bool IsAtOrUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        string prefix = root[^1] == Path.DirectorySeparatorChar ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static void AddRunsRootFiles(string runsRoot, Action<string> add)
    {
        if (!Directory.Exists(runsRoot))
        {
            return;
        }

        string ledger = Path.Combine(runsRoot, "runs.jsonl");

        // The launcher's own bookkeeping lives under runs/, inbox/ and system/ and is never a gate
        // input, and runs.jsonl is the ledger (one record per run, append-only): do not descend
        // into those subtrees, so a planted or unreadable directory under them cannot stall the walk.
        HashSet<string> skip = new(StringComparer.Ordinal)
        {
            Path.Combine(runsRoot, "runs"),
            Path.Combine(runsRoot, "inbox"),
            Path.Combine(runsRoot, "system"),
        };

        foreach (string file in WalkFiles(runsRoot, dir => skip.Contains(dir)))
        {
            if (!string.Equals(file, ledger, StringComparison.Ordinal))
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

    /// <summary>
    /// Marks the gate's runnable inputs — the paths the gate executes — in <paramref name="runnable"/>.
    /// The report-path exclusion drops a path from both sides of a trust comparison, so it must never
    /// cover something the gate runs: an input the worker swapped between the before-worker snapshot
    /// and the gate's execution would otherwise be run by the gate while the post-gate comparison,
    /// blinded by the exclusion, never sees the swap. A path is runnable when it is
    ///   - the resolved executable (argv[0]: found on PATH when the entry has no directory part, else
    ///     resolved against the gate working directory); or
    ///   - argv[1] only, when it is an existing file — the script an interpreter such as <c>sh</c>,
    ///     <c>bash</c>, <c>dotnet</c> or <c>python</c> consumes as the thing it runs
    ///     (<c>dotnet-lock.sh</c> in <c>sh dotnet-lock.sh dotnet build ...</c>); later file arguments
    ///     are data the command reads or writes, not scripts, so they are not runnable by this rule
    ///     (an output file named after the script, e.g. <c>report.out</c> in <c>sh gate.sh report.out</c>
    ///     with a pre-existing <c>report.out</c>, stays excludable); or
    ///   - any argv file entry with an execute bit (File.GetUnixFileMode: user, group or other) —
    ///     a file the command could execute no matter where it sits in the argv.
    /// Everything is evaluated when the fixed trust list is collected (round 1, before the worker):
    /// existence and execute bits are frozen then, so the rule never depends on what the gate did —
    /// in particular a report file the gate creates mid-run is never retroactively runnable, and a
    /// report path that equals a runnable input is refused by <see cref="ReportPathViolation"/> even
    /// when it is trusted solely as an argv entry.
    /// </summary>
    private static void MarkRunnableArgv(IReadOnlyList<string> gateCommand, string gateWorkingDirectory, HashSet<string> runnable)
    {
        string exe = ResolveExecutable(gateCommand, gateWorkingDirectory);
        if (exe.Length is not 0)
        {
            runnable.Add(exe);
        }

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
                // Not resolvable, hence not an existing file: only the execute-bit rule could apply.
                continue;
            }

            if (!File.Exists(full))
            {
                continue;
            }

            if (i is 1)
            {
                runnable.Add(full);
            }

            if (HasExecuteBit(full))
            {
                runnable.Add(full);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> carries any execute bit (user, group or other). The check is
    /// bounded to platforms where Unix file modes exist; elsewhere no entry becomes runnable through
    /// this rule (the other two rules do not depend on it).
    /// </summary>
    private static bool HasExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & ExecuteBits) is not UnixFileMode.None;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            // The path vanished or cannot be stat'ed: it cannot be executed from the argv either.
            return false;
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
                AddLiteralEntry(entries[i], anchor, add);
            }
        }
    }

    private static void AddLiteralEntry(string entry, string anchor, Action<string> add)
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
    /// </summary>
    private static bool SkipWalkedDirectory(string dir, string anchor, string skipRunsRoot)
    {
        return (Path.GetFileName(dir) is ".git"
            && Path.GetDirectoryName(dir) is { } parent
            && string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(anchor), StringComparison.Ordinal))
            || IsAtOrUnder(dir, skipRunsRoot);
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
