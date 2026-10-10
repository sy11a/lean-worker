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

        ProcessStartInfo psi = BuildStartInfo(spec, cwd, envNames);

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

            return await DriveProcessAsync(process, spec, logPath, gateJsonPath, started, cwd, stdout, envNames, token).ConfigureAwait(false);
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
        string first = cmd[0];
        // The executable: PATH lookup only when there's no directory part (a path with a directory is used as given).
        bool hasDir = first.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                   || first.Contains(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal);
        string fileName = hasDir ? first : (Launcher.FindOnPath(first) ?? first);

        ProcessStartInfo psi = new()
        {
            FileName = fileName,
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
        DateTimeOffset started, string cwd, StdoutBuffer stdoutBuf, IReadOnlyList<string> envNames, CancellationToken token)
    {
        StreamWriter log = new(logPath, append: false, Json.Utf8);
        await using ConfiguredAsyncDisposable logDisposal = log.ConfigureAwait(false);
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
        CountResolution resolution = TryResolveCount(stdout, cwd, spec);
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

    private static CountResolution TryResolveCount(StdoutBuffer stdout, string cwd, GateSpec spec)
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

        if (reportPath is null)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "report path was empty");
        }

        string full;
        try
        {
            full = Path.IsPathRooted(reportPath) ? reportPath : Path.GetFullPath(Path.Combine(cwd, reportPath));
        }
        catch (ArgumentException ex)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: $"report path '{reportPath}' is invalid: {ex.Message}");
        }

        return TryReadReportFile(full, spec.CountPath);
    }

    /// <summary>
    /// The first usable capture in <paramref name="m"/>: group 1; otherwise the first named group
    /// other than 0 and 1. Returns null when there is no usable capture (so the caller can produce an
    /// "empty" or "no capture group" error).
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

        return null;
    }

    private static CountResolution TryReadReportFile(string full, string countPath)
    {
        if (!File.Exists(full))
        {
            return new CountResolution(Count: null, ReportPath: full, Error: $"report file '{full}' does not exist");
        }

        string text;
        try
        {
            text = File.ReadAllText(full, Json.Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CountResolution(Count: null, ReportPath: full, Error: $"report file '{full}' could not be read: {ex.Message}");
        }

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
        await File.WriteAllTextAsync(path, o.ToJsonString(Json.Indented), Json.Utf8, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an error gate result for the trust-boundary case (a worker changed a file the gate
    /// trusts). Writes <c>gate.json</c> and returns the <see cref="GateResult"/> that the chain decision
    /// uses.
    /// </summary>
    internal static async Task<GateResult> WriteFailureAsync(string runDir, GateSpec spec, string errorMessage)
    {
        GateResult result = new(Outcome: "error", ExitCode: 5, Count: null, ReportPath: null,
            Feedback: string.Empty, Error: errorMessage, Duration: TimeSpan.Zero);
        await WriteGateJsonAsync(Path.Combine(runDir, "gate.json"), result, spec, envNames: []).ConfigureAwait(false);
        return result;
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}