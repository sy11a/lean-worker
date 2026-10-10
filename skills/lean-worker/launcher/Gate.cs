// The gate runner. After a successful worker run, the launcher starts the gate's command in <cwd>, drains
// its stdout and stderr in parallel into <runDir>/gate.log (interleaved as the lines arrive; stderr lines
// prefixed "[stderr] ") and decides the run's next step from the process's exit code and the report file the
// gate names on its last stdout line.
//
// Outcomes:
//   "clean"    — exit 0, no findings (count 0, or the report's count path resolved to an empty array).
//   "findings" — exit 1 with a non-empty findings array on the configured count path of the report.
//   "error"    — any other condition (start failure, timeout, cancellation, unexpected exit code, missing
//                or malformed report file, mis-wired count path, zero findings on exit 1, the worker
//                having changed files the gate trusts).
//
// Gate chain: a chain is a sequence of runs of one task. Round 1 is the user's run. After a successful worker
// run the launcher runs the gate; "clean" / "error" end the chain, "findings" continue unless the chain is
// stuck (no decrease in two consecutive rounds, the round reached MaxRounds, or the summed worker cost
// exceeded MaxTotalUsd). GateChain.Decide captures the pure decision logic so it can be tested without
// processes.
//
// Trust boundary: the gate's environment is built from an allowlist (a default set + the profile's
// `gate.env`), not inherited from the launcher. Names that pass the gate's filter are recorded as
// `env_names` in gate.json (sorted, never the values) so the run's record shows exactly which
// variables reached the gate.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal static class Gate
{
    /// <summary>
    /// The result of one gate run.
    /// </summary>
    internal sealed record GateResult(string Outcome, int ExitCode, int? Count, string? ReportPath, string Feedback, string? Error, TimeSpan Duration);

    private sealed class StdoutBuffer
    {
        public readonly object Lock = new();
        public readonly StringBuilder Text = new();
        public string? LastNonEmptyLine;
    }

    private readonly record struct WaitResult(bool TimedOut, bool Cancelled, int ExitCode);

    public static async Task<GateResult> RunAsync(GateSpec spec, string runDir, CancellationToken token)
    {
        DateTimeOffset started = DateTimeOffset.Now;
        string logPath = Path.Combine(runDir, "gate.log");
        string gateJsonPath = Path.Combine(runDir, "gate.json");
        string cwd = Directory.GetCurrentDirectory();
        StdoutBuffer stdout = new();
        List<string> envNames = [];

        ProcessStartInfo psi;
        try
        {
            psi = BuildStartInfo(spec, cwd, envNames);
        }
        catch (LaunchException ex)
        {
            // BuildStartInfo throws when the gate's argv[0] cannot be resolved. For a spec the
            // launcher resolved in round 1 this cannot happen (the resolved path is absolute and
            // used as-is); only a caller that bypasses round 1 resolves here, and its failure is
            // a start failure with a precise, named message.
            return await BuildStartFailureResultAsync(ex.Message, started, gateJsonPath, spec, envNames).ConfigureAwait(false);
        }

        // gate.log is opened before Process.Start: if the open fails, no child exists yet and there
        // is nothing to kill (the failure surfaces as a gate error below); once the child runs, the
        // writer is already open and an open failure mid-run cannot orphan the process.
        StreamWriter log = Launcher.CreateWriter(logPath, Json.Utf8);
        await using ConfiguredAsyncDisposable logDisposal = log.ConfigureAwait(false);

        Process? childProcess;
        try
        {
            childProcess = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException or IOException or PlatformNotSupportedException)
        {
            return await BuildStartFailureResultAsync(ex.Message, started, gateJsonPath, spec, envNames).ConfigureAwait(false);
        }

        if (childProcess is null)
        {
            return await BuildStartFailureResultAsync("could not start gate", started, gateJsonPath, spec, envNames).ConfigureAwait(false);
        }

        Process process = childProcess;
        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }

            return await DriveProcessAsync(process, spec, logPath, gateJsonPath, started, cwd, stdout, envNames, log, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Walks <paramref name="path"/> from <paramref name="root"/>. Each path segment is a JSON property name
    /// optionally followed by one non-negative integer index. Returns the resolved node, or null if any segment
    /// does not resolve (missing property, missing index, walked off a non-object/non-array).
    /// </summary>
    internal static JsonNode? ResolveCountPath(JsonNode root, string path)
    {
        JsonNode? cur = root;
        foreach (string segment in path.Split('.'))
        {
            if (cur is not JsonObject obj)
            {
                return null;
            }

            string propName;
            int? index = null;
            int bracket = segment.AsSpan().IndexOf('[');
            if (bracket < 0)
            {
                propName = segment;
            }
            else
            {
                propName = segment[..bracket];
                string tail = segment[(bracket + 1)..];
                if (!tail.EndsWith(']'))
                {
                    return null;
                }

                if (!int.TryParse(tail[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0)
                {
                    return null;
                }

                index = n;
            }

            if (propName.Length is 0 || !obj.TryGetPropertyValue(propName, out JsonNode? next) || next is null)
            {
                return null;
            }

            cur = next;
            if (index is { } idx)
            {
                if (cur is not JsonArray arr || idx >= arr.Count)
                {
                    return null;
                }

                cur = arr[idx];
            }
        }

        return cur;
    }

    private static ProcessStartInfo BuildStartInfo(GateSpec spec, string cwd, List<string> envNames)
    {
        List<string> cmd = spec.Command;
        // The executable: exactly the path round 1 resolved (GateSpec.ResolvedExecutable — the same
        // string the trust check hashed as TrustSource.Executable, frozen on the spec the chain
        // carries into every round). The gate never searches PATH again at gate time: a worker could
        // plant an executable in an earlier PATH directory after round 1's lookup, and re-resolving
        // here would run the planted file while the trust check hashes the real one. Only a spec no
        // launcher has resolved (a direct Gate.RunAsync caller) resolves now, once, by the same rule.
        string resolved = spec.ResolvedExecutable ?? GateTrust.ResolveExecutableStrict(cmd, cwd);

        ProcessStartInfo psi = new()
        {
            FileName = resolved,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Json.Utf8,
            StandardErrorEncoding = Json.Utf8,
            UseShellExecute = false,
        };
        for (int i = 1; i < cmd.Count; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        // Minimal environment: only the allow-listed names reach the gate (no API keys, no provider creds
        // unless explicitly listed). The names that pass the filter are also recorded for gate.json.
        psi.Environment.Clear();
        List<EnvPattern> patterns = BuildEnvPatterns(spec.Env);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            if (e.Value is not string value || !MatchesAny(patterns, key))
            {
                continue;
            }

            psi.Environment[key] = value;
            envNames.Add(key);
        }

        envNames.Sort(StringComparer.Ordinal);
        return psi;
    }

    /// <summary>
    /// The patterns the gate's environment filter accepts: the built-in defaults plus the profile's
    /// <c>gate.env</c> entries. A pattern with no trailing <c>*</c> matches exactly; one ending in
    /// <c>*</c> matches any name that starts with the prefix before the star.
    /// </summary>
    private static List<EnvPattern> BuildEnvPatterns(IReadOnlyList<string> extra)
    {
        string[] builtIn =
        [
            "PATH", "HOME", "USER", "LOGNAME", "LANG", "TMPDIR", "TERM",
            "LC_*", "DOTNET_*", "NUGET_PACKAGES", "NUGET_*", "NuGetPackageSourceCredentials_*", "MSBUILD*",
        ];
        List<EnvPattern> patterns = new(builtIn.Length + extra.Count);
        foreach (string p in builtIn)
        {
            patterns.Add(new EnvPattern(p));
        }

        foreach (string p in extra)
        {
            patterns.Add(new EnvPattern(p));
        }

        return patterns;
    }

    private static bool MatchesAny(List<EnvPattern> patterns, string name)
    {
        foreach (EnvPattern p in patterns)
        {
            if (p.Matches(name))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct EnvPattern(string Raw)
    {
        public bool Matches(string name) => Raw.EndsWith('*') ? name.StartsWith(Raw[..^1], StringComparison.Ordinal) : name == Raw;
    }

    private static async Task<GateResult> BuildStartFailureResultAsync(string message, DateTimeOffset started, string gateJsonPath, GateSpec spec, IReadOnlyList<string> envNames)
    {
        GateResult startFailure = new(Outcome: "error", ExitCode: -1, Count: null, ReportPath: null, Feedback: string.Empty, Error: message, Duration: DateTimeOffset.Now - started);
        await WriteGateJsonAsync(gateJsonPath, startFailure, spec, envNames).ConfigureAwait(false);
        return startFailure;
    }

    private static async Task<GateResult> DriveProcessAsync(Process process, GateSpec spec, string logPath, string gateJsonPath,
        DateTimeOffset started, string cwd, StdoutBuffer stdoutBuf, IReadOnlyList<string> envNames, StreamWriter log, CancellationToken token)
    {
        // The log writer is opened by the caller (before Process.Start) and disposed there; this
        // method only writes it.
        object logLock = new();
        int feedbackCap = spec.FeedbackMaxChars + 1;

        // One cancellation source for the pumps: cancelling it stops both readers cleanly when the
        // bounded drain safety net expires (otherwise a stuck child can keep them appending to stdout
        // and writing to a disposed gate.log).
        using CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task stdoutPump = PumpStdoutAsync(process.StandardOutput, stdoutBuf, feedbackCap, line => WriteLog(log, logLock, line), prefix: null, pumpCts.Token);
        Task stderrPump = PumpStdoutAsync(process.StandardError, buffer: null, cap: 0, line => WriteLog(log, logLock, line), prefix: "[stderr] ", pumpCts.Token);

        WaitResult wait = await WaitForExitAsync(process, spec.TimeoutMinutes, token).ConfigureAwait(false);
        if (wait.TimedOut || wait.Cancelled)
        {
            KillTree(process);
        }

        // Drain: let the pumps finish (with a 30 s safety net). On timeout, cancel them so they unwind
        // and then await them so no further mutation of shared state or writes to a disposed gate.log.
        Task pumpsDone = Task.WhenAll(stdoutPump, stderrPump);
        Task safetyNet = Task.Delay(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        if (await Task.WhenAny(pumpsDone, safetyNet).ConfigureAwait(false) == safetyNet)
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try { await stdoutPump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            try { await stderrPump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        TimeSpan duration = DateTimeOffset.Now - started;
        return await FinishAsync(wait, gateJsonPath, spec, duration, cwd, stdoutBuf, logPath, envNames).ConfigureAwait(false);
    }

    private static async Task<WaitResult> WaitForExitAsync(Process process, int timeoutMinutes, CancellationToken token)
    {
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        waitCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            return new WaitResult(TimedOut: false, Cancelled: false, ExitCode: process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // waitCts was cancelled by the timer (timeout) or by the caller's token; distinguish the two.
            bool cancelled = token.IsCancellationRequested;
            return new WaitResult(TimedOut: !cancelled, Cancelled: cancelled, ExitCode: -1);
        }
    }

    private static async Task<GateResult> FinishAsync(WaitResult wait, string gateJsonPath, GateSpec spec, TimeSpan duration, string cwd, StdoutBuffer stdout, string logPath, IReadOnlyList<string> envNames)
    {
        if (wait.TimedOut)
        {
            return await FinishTimeoutAsync(gateJsonPath, spec, duration, stdout, logPath, envNames).ConfigureAwait(false);
        }

        if (wait.Cancelled)
        {
            return await FinishCancelledAsync(gateJsonPath, spec, duration, stdout, logPath, envNames).ConfigureAwait(false);
        }

        return await FinishResultAsync(gateJsonPath, spec, duration, cwd, stdout, logPath, envNames, wait.ExitCode).ConfigureAwait(false);
    }

    private static void WriteLog(StreamWriter log, object logLock, string line)
    {
        lock (logLock)
        {
            log.WriteLine(line);
            log.Flush();
        }
    }

    private static async Task<GateResult> FinishTimeoutAsync(string gateJsonPath, GateSpec spec, TimeSpan duration, StdoutBuffer stdout, string logPath, IReadOnlyList<string> envNames)
    {
        GateResult timeoutResult = new(
            Outcome: "error",
            ExitCode: -1,
            Count: null,
            ReportPath: null,
            Feedback: JoinStdout(stdout, spec.FeedbackMaxChars, logPath),
            Error: string.Create(CultureInfo.InvariantCulture, $"gate timed out after {spec.TimeoutMinutes} minutes"),
            Duration: duration);
        await WriteGateJsonAsync(gateJsonPath, timeoutResult, spec, envNames).ConfigureAwait(false);
        return timeoutResult;
    }

    private static async Task<GateResult> FinishCancelledAsync(string gateJsonPath, GateSpec spec, TimeSpan duration, StdoutBuffer stdout, string logPath, IReadOnlyList<string> envNames)
    {
        GateResult cancelledResult = new(
            Outcome: "error",
            ExitCode: -1,
            Count: null,
            ReportPath: null,
            Feedback: JoinStdout(stdout, spec.FeedbackMaxChars, logPath),
            Error: "gate cancelled",
            Duration: duration);
        await WriteGateJsonAsync(gateJsonPath, cancelledResult, spec, envNames).ConfigureAwait(false);
        return cancelledResult;
    }

    private static async Task<GateResult> FinishResultAsync(string gateJsonPath, GateSpec spec, TimeSpan duration, string cwd, StdoutBuffer stdout, string logPath, IReadOnlyList<string> envNames, int exitCode)
    {
        string feedback = JoinStdout(stdout, spec.FeedbackMaxChars, logPath);
        CountResolution resolution = await TryResolveCountAsync(stdout, cwd, spec).ConfigureAwait(false);
        GateResult result = BuildResult(exitCode, spec, feedback, resolution, duration);
        await WriteGateJsonAsync(gateJsonPath, result, spec, envNames).ConfigureAwait(false);
        return result;
    }

    private static async Task PumpStdoutAsync(TextReader reader, StdoutBuffer? buffer, int cap, Action<string> write, string? prefix, CancellationToken token)
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (buffer is not null)
                {
                    lock (buffer.Lock)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            buffer.LastNonEmptyLine = line;
                        }

                        if (buffer.Text.Length < cap)
                        {
                            int remaining = cap - buffer.Text.Length;
                            if (line.Length <= remaining)
                            {
                                _ = buffer.Text.Append(line).Append('\n');
                            }
                            else
                            {
                                _ = buffer.Text.Append(line.AsSpan(0, remaining));
                            }
                        }
                    }
                }

                write(prefix is null ? line : prefix + line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static void KillTree(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static string JoinStdout(StdoutBuffer buffer, int max, string logPath)
    {
        string text;
        lock (buffer.Lock)
        {
            text = buffer.Text.ToString().TrimEnd('\n');
        }

        if (text.Length <= max)
        {
            return text;
        }

        return text[..max] + '\n' + $"[truncated; full gate output: {logPath}]";
    }

    private readonly record struct CountResolution(int? Count, string? ReportPath, string? Error);

    private static async Task<CountResolution> TryResolveCountAsync(StdoutBuffer stdout, string cwd, GateSpec spec)
    {
        string? last;
        lock (stdout.Lock)
        {
            last = stdout.LastNonEmptyLine;
        }

        if (last is null)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "no non-empty stdout line was printed by the gate");
        }

        Match m;
        try
        {
            m = spec.ReportFromLastLine.Match(last);
        }
        catch (RegexMatchTimeoutException)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "report path regex timed out on the last stdout line");
        }

        string? reportPath = ExtractReportPath(m);
        if (!m.Success)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: $"report path regex did not match the last stdout line '{Trim(last)}'");
        }

        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "report path was empty");
        }

        string full;
        try
        {
            // GetFullPath also normalizes an absolute path (a printed "../" hop or a "." segment
            // must not blur the run-directory exemption or the source lookup).
            full = Path.GetFullPath(Path.IsPathRooted(reportPath) ? reportPath : Path.Combine(cwd, reportPath));
        }
        catch (ArgumentException ex)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: $"report path '{reportPath}' is invalid: {ex.Message}");
        }

        return await TryReadReportFileAsync(full, spec.CountPath).ConfigureAwait(false);
    }

    /// <summary>
    /// The first usable capture in <paramref name="m"/>: group 1; otherwise the first named group
    /// other than 0 and 1. Returns the empty string when every capture is empty or whitespace (so the
    /// caller can produce a "report path was empty" error before resolving it as the working directory).
    /// </summary>
    private static string? ExtractReportPath(Match m)
    {
        if (m.Groups.Count < 2)
        {
            return null;
        }

        if (m.Groups[1].Value.Length > 0)
        {
            return m.Groups[1].Value.Trim();
        }

        for (int i = 0; i < m.Groups.Count; i++)
        {
            Group g = m.Groups[i];
            if (g.Name is "0" or "1")
            {
                continue;
            }

            if (g.Value.Length > 0)
            {
                return g.Value.Trim();
            }
        }

        return string.Empty;
    }

    private static async Task<CountResolution> TryReadReportFileAsync(string full, string countPath)
    {
        // The report path is a path the gate (a worker-controlled process) prints. Read it through
        // the same guarded reader the trust check uses: a planted symlink to /dev/zero would
        // otherwise run the launcher out of memory; a FIFO at the path blocks the read forever.
        // Refusals become a `CountResolution` error so the gate reports `error` with a precise
        // message naming the path.
        BoundedReadResult read = await BoundedReadReportAsync(full).ConfigureAwait(false);
        if (read.Error is not null)
        {
            return new CountResolution(Count: null, ReportPath: full, Error: read.Error);
        }

        string text = read.Text!;

        JsonNode root;
        try
        {
            root = Json.ParseLenient(text);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return new CountResolution(Count: null, ReportPath: full, Error: $"report file '{full}' is not valid JSON: {ex.Message}");
        }

        JsonNode? target;
        try
        {
            target = ResolveCountPath(root, countPath);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return new CountResolution(Count: null, ReportPath: full, Error: $"walking {countPath} failed: {ex.Message}");
        }

        if (target is not JsonArray arr)
        {
            return new CountResolution(Count: null, ReportPath: full, Error: $"{countPath} is not an array");
        }

        return new CountResolution(Count: arr.Count, ReportPath: full, Error: null);
    }

    /// <summary>
    /// Per-file time bound for reading the gate's report. A FIFO at the report path parks the read
    /// in the kernel until a writer appears; the bound matches the trust check's open-read timeout so
    /// the gate and the trust check fail in a comparable time.
    /// </summary>
    private static readonly TimeSpan _reportReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Largest report the launcher will read into memory. Reports larger than this are refused (a
    /// gate that genuinely needs more can stream; a planted symlink to <c>/dev/zero</c> cannot).
    /// </summary>
    private const long MaxReportBytes = 64L * 1024L * 1024L;

    private sealed record BoundedReadResult(string? Text, string? Error);

    /// <summary>
    /// Reads a regular file the gate named on its last stdout line. Refuses symlinks (a worker can
    /// plant one to swap the read into <c>/dev/</c>, <c>/proc/</c>, <c>/sys/</c>, a FIFO, or
    /// elsewhere), targets that are not a regular file (including pseudo-filesystem entries), reads
    /// that exceed the per-file time bound or byte cap, and any I/O / permission failure. Each
    /// refusal is a precise <c>CountResolution</c> error naming <paramref name="full"/>.
    /// </summary>
    private static async Task<BoundedReadResult> BoundedReadReportAsync(string full)
    {
        BoundedReadResult? preflight = PreflightReport(full);
        if (preflight is not null)
        {
            return preflight;
        }

        using CancellationTokenSource cts = new(_reportReadTimeout, TimeProvider.System);
        CancellationToken token = cts.Token;

        // Abandoned-task fault observation: see the equivalent comment in GateTrust's HashFileAsync. A read
        // that gets stuck on a FIFO open() leak is acceptable for a short-lived launcher, but a fault
        // surfaced after we have returned must not surface later as an unobserved exception.
        // StartNew hands the open off to the thread pool, otherwise the FileStream ctor can park the
        // caller's thread on a FIFO with no writer and the WhenAny timer never gets a chance to win.
        Task<BoundedReadResult> readTask = ReadOffPoolAsync(full, token);
        _ = readTask.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        Task<BoundedReadResult> timer = BoundedReadTimeoutAsync(_reportReadTimeout, TimeProvider.System, token);
        Task<BoundedReadResult> winner = await Task.WhenAny(readTask, timer).ConfigureAwait(false);
        if (winner == readTask)
        {
            return await readTask.ConfigureAwait(false);
        }

        return new BoundedReadResult(Text: null, Error: $"report file '{full}' could not be read in time");
    }

    private static BoundedReadResult? PreflightReport(string full)
    {
        if (!File.Exists(full))
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' does not exist");
        }

        return CheckLinkReport(full) ?? CheckAttributesReport(full);
    }

    private static BoundedReadResult? CheckLinkReport(string full)
    {
        // A symlink at the report path is refused outright so a worker cannot swap the read into
        // another file (the gate, after all, just told us where to look).
        string? linkTarget;
        try
        {
            linkTarget = new FileInfo(full).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            // ReadLink failed for an I/O / platform reason; treat as a non-link and rely on the
            // attribute / open checks below to classify it.
            return null;
        }

        return linkTarget is not null
            ? new BoundedReadResult(Text: null, Error: $"report file '{full}' is not a regular file")
            : null;
    }

    private static BoundedReadResult? CheckAttributesReport(string full)
    {
        // Attribute pre-check: directories, devices, reparse points, and /dev/, /proc/, /sys/ paths
        // are not regular files the launcher reads.
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(full);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' is not a regular file: {ex.Message}");
        }

        if (attrs.HasFlag(FileAttributes.Directory)
            || attrs.HasFlag(FileAttributes.Device)
            || attrs.HasFlag(FileAttributes.ReparsePoint))
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' is not a regular file");
        }

        // Pseudo-filesystem refusal: /dev/, /proc/, /sys/ are kernel-generated surfaces. /dev/null
        // passes every other guard and would otherwise read empty content; refuse at the path.
        return full.StartsWith("/dev/", StringComparison.Ordinal)
            || full.StartsWith("/proc/", StringComparison.Ordinal)
            || full.StartsWith("/sys/", StringComparison.Ordinal)
            ? new BoundedReadResult(Text: null, Error: $"report file '{full}' is not a regular file")
            : null;
    }

    private static async Task<BoundedReadResult> ReadReportCoreAsync(string full, CancellationToken token)
    {
        try
        {
            return await ReadOpenReportAsync(full, token).ConfigureAwait(false);
        }
        catch (OpenReportFailedException ex)
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' could not be read: {ex.Message}");
        }
    }

    private static async Task<BoundedReadResult> ReadOpenReportAsync(string full, CancellationToken token)
    {
        FileStream fs = OpenReportOrThrow(full);
        await using (fs.ConfigureAwait(false))
        {
            return await DrainReportStreamAsync(full, fs, token).ConfigureAwait(false);
        }
    }

    private static FileStream OpenReportOrThrow(string full)
    {
        // FileMode.Open + FileAccess.Read throws on a missing or unreadable file; the caller maps
        // the exception into a `report file could not be read` sentinel after the `using`
        // disposes any half-open handle.
        try
        {
            return new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new OpenReportFailedException(ex.Message);
        }
    }

    private sealed class OpenReportFailedException : Exception
    {
        public OpenReportFailedException(string message) : base(message) { }
    }

    private static async Task<BoundedReadResult> DrainReportStreamAsync(string full, FileStream fs, CancellationToken token)
    {
        BoundedReadResult? preflight = PreflightReportStream(full, fs);
        if (preflight is not null)
        {
            return preflight;
        }

        byte[] buffer = new byte[80 * 1024];
        MemoryStream ms = new();
        while (true)
        {
            BoundedReadResult? chunk = await ReadReportChunkAsync(full, fs, buffer, ms, token).ConfigureAwait(false);
            if (chunk is not null)
            {
                return chunk;
            }

            if (ms.Length > MaxReportBytes)
            {
                return new BoundedReadResult(Text: null, Error: $"report file '{full}' is too large");
            }
        }
    }

    private static BoundedReadResult? PreflightReportStream(string full, FileStream fs)
    {
        // CanSeek guards FIFOs, sockets, and character devices on .NET 8 (UnixFileMode.TypeMask lands
        // in .NET 9 and is not available here).
        if (!fs.CanSeek)
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' is not a regular file");
        }

        return fs.Length > MaxReportBytes
            ? new BoundedReadResult(Text: null, Error: $"report file '{full}' is too large")
            : null;
    }

    private static async Task<BoundedReadResult?> ReadReportChunkAsync(string full, FileStream fs, byte[] buffer, MemoryStream ms, CancellationToken token)
    {
        int n;
        try
        {
            n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' could not be read in time");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' could not be read: {ex.Message}");
        }

        if (n is 0)
        {
            return DecodeReportBuffer(full, ms);
        }

        await ms.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
        return ms.Length > MaxReportBytes
            ? new BoundedReadResult(Text: null, Error: $"report file '{full}' is too large")
            : null;
    }

    private static BoundedReadResult DecodeReportBuffer(string full, MemoryStream ms)
    {
        try
        {
            string text = Json.Utf8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            return new BoundedReadResult(Text: text, Error: null);
        }
        catch (Exception ex) when (ex is System.Text.DecoderFallbackException)
        {
            return new BoundedReadResult(Text: null, Error: $"report file '{full}' is not valid UTF-8: {ex.Message}");
        }
    }

    /// <summary>
    /// A timed-out delay that returns a <see cref="BoundedReadResult"/> when the limit is reached. Used
    /// alongside <see cref="ReadReportCoreAsync"/> in <see cref="Task.WhenAny{TResult}(Task{TResult}[])"/>
    /// to bound how long the report read can run.
    /// </summary>
    private static async Task<BoundedReadResult> BoundedReadTimeoutAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken token)
    {
        await Task.Delay(timeout, timeProvider, token).ConfigureAwait(false);
        return new BoundedReadResult(Text: null, Error: "timeout");
    }

    private static async Task<BoundedReadResult> ReadOffPoolAsync(string full, CancellationToken token)
    {
        // StartNew with Func<object, Task<...>> returns Task<Task<...>> and takes a TaskScheduler
        // (satisfies CA2008/VSTHRD105). Unwrap exposes the inner Task<BoundedReadResult>; awaiting
        // it gives the inner result, the async method's signature wraps that back into Task<...>
        // for the caller's WhenAny with a timer.
        Task<Task<BoundedReadResult>> wrapped = Task.Factory.StartNew(async _ => await ReadReportCoreAsync(full, token).ConfigureAwait(false), state: null, token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
        return await wrapped.Unwrap().ConfigureAwait(false);
    }

    private static GateResult BuildResult(int exitCode, GateSpec spec, string feedback, CountResolution resolution, TimeSpan duration)
    {
        if (exitCode is 0)
        {
            // Clean: if a report resolved cleanly, use the count; otherwise zero.
            int count = resolution.Error is null ? (resolution.Count ?? 0) : 0;
            return new GateResult(Outcome: "clean", ExitCode: 0, Count: count, ReportPath: resolution.ReportPath, Feedback: feedback, Error: null, Duration: duration);
        }

        if (exitCode is 1)
        {
            if (resolution.Error is null && resolution.Count is { } c && c > 0)
            {
                return new GateResult(Outcome: "findings", ExitCode: 1, Count: c, ReportPath: resolution.ReportPath, Feedback: feedback, Error: null, Duration: duration);
            }

            string reason = resolution.Error ?? $"{spec.CountPath} has 0 entries";
            return new GateResult(Outcome: "error", ExitCode: 1, Count: resolution.Count, ReportPath: resolution.ReportPath, Feedback: feedback,
                Error: $"gate exited 1 but {reason}", Duration: duration);
        }

        return new GateResult(Outcome: "error", ExitCode: exitCode, Count: resolution.Count, ReportPath: resolution.ReportPath, Feedback: feedback,
            Error: string.Create(CultureInfo.InvariantCulture, $"gate exited {exitCode}"), Duration: duration);
    }

    internal static async Task WriteGateJsonAsync(string path, GateResult result, GateSpec spec, IReadOnlyList<string> envNames)
    {
        JsonObject o = new()
        {
            ["outcome"] = result.Outcome,
            ["exit_code"] = result.ExitCode,
            ["count"] = result.Count,
            ["report_path"] = result.ReportPath,
            ["error"] = result.Error,
            ["duration_ms"] = (long)result.Duration.TotalMilliseconds,
            ["command"] = new JsonArray([.. spec.Command.Select(c => (JsonNode)c)]),
            ["env_names"] = new JsonArray([.. envNames.Select(n => (JsonNode)n)]),
        };
        // The declared gate outputs, as written in the profile: the record shows which paths the
        // gate may write (they leave the after-gate trust comparison, nothing else does).
        if (spec.Outputs is { Count: > 0 })
        {
            o["outputs"] = new JsonArray([.. spec.Outputs.Select(c => (JsonNode)c)]);
        }

        if (spec.Warnings is { Count: > 0 })
        {
            o["warnings"] = new JsonArray([.. spec.Warnings.Select(w => (JsonNode)w)]);
        }

        await Launcher.WritePlainAsync(path, o.ToJsonString(Json.Indented)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an error gate result for the trust-boundary case (a worker changed a file the gate
    /// trusts, or the gate ended with bad state). Writes <c>gate.json</c> and returns the
    /// <see cref="GateResult"/> that the chain decision uses. When <paramref name="gateRan"/> is
    /// null (the violation was found before the gate ran), <c>ExitCode</c> is <c>-1</c> because no
    /// gate process ran (mirrors the build-start failure / timeout / cancellation codepath, which
    /// also records <c>-1</c>; a trust violation must not be reported as a gate exit code). When the
    /// gate already ran, only the outcome and the error are replaced: the gate's real exit code,
    /// count, report path, feedback and duration stay in <c>gate.json</c> and the summary.
    /// </summary>
    internal static async Task<GateResult> WriteFailureAsync(string runDir, GateSpec spec, string errorMessage, GateResult? gateRan = null)
    {
        GateResult result = gateRan is null
            ? new GateResult(Outcome: "error", ExitCode: -1, Count: null, ReportPath: null,
                Feedback: string.Empty, Error: errorMessage, Duration: TimeSpan.Zero)
            : gateRan with { Outcome = "error", Error = errorMessage };
        await WriteGateJsonAsync(Path.Combine(runDir, "gate.json"), result, spec, envNames: []).ConfigureAwait(false);
        return result;
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}