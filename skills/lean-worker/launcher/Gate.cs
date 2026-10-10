// The gate runner. After a successful worker run, the launcher starts the gate's command in <cwd>, drains
// its stdout and stderr in parallel into <runDir>/gate.log (interleaved as the lines arrive; stderr lines
// prefixed "[stderr] ") and decides the run's next step from the process's exit code and the report file the
// gate names on its last stdout line.
//
// Outcomes:
//   "clean"    — exit 0, no findings (count 0, or the report's count path resolved to an empty array).
//   "findings" — exit 1 with a non-empty findings array on the configured count path of the report.
//   "error"    — any other condition (start failure, timeout, unexpected exit code, missing or malformed
//                report file, mis-wired count path, zero findings on exit 1).
//
// Gate chain: a chain is a sequence of runs of one task. Round 1 is the user's run. After a successful worker
// run the launcher runs the gate; "clean" / "error" end the chain, "findings" continue unless the chain is
// stuck (no decrease in two consecutive rounds, the round reached MaxRounds, or the summed worker cost
// exceeded MaxTotalUsd). GateChain.Decide captures the pure decision logic so it can be tested without
// processes.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal static class Gate
{
    /// <summary>
    /// The result of one gate run.
    /// </summary>
    internal sealed record GateResult(string Outcome, int ExitCode, int? Count, string? ReportPath, string Feedback, string? Error, TimeSpan Duration);

    public static async Task<GateResult> RunAsync(GateSpec spec, string runDir, CancellationToken token)
    {
        DateTimeOffset started = DateTimeOffset.Now;
        string logPath = Path.Combine(runDir, "gate.log");
        string gateJsonPath = Path.Combine(runDir, "gate.json");
        string cwd = Directory.GetCurrentDirectory();
        List<string> stdoutLines = [];

        ProcessStartInfo psi = BuildStartInfo(spec, cwd);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException or IOException or PlatformNotSupportedException)
        {
            return BuildStartFailureResult(ex.Message, started, gateJsonPath, spec);
        }

        if (process is null)
        {
            return BuildStartFailureResult("could not start gate", started, gateJsonPath, spec);
        }

        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }

            return await DriveProcessAsync(process, spec, logPath, gateJsonPath, started, cwd, stdoutLines, token).ConfigureAwait(false);
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

    private static ProcessStartInfo BuildStartInfo(GateSpec spec, string cwd)
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

        // The launcher's own environment: omit nothing, override nothing — ProcessStartInfo inherits the
        // current process's env block when UseShellExecute is false and no Environment entries are added.
        _ = psi.Environment;
        return psi;
    }

    private static GateResult BuildStartFailureResult(string message, DateTimeOffset started, string gateJsonPath, GateSpec spec)
    {
        GateResult startFailure = new(Outcome: "error", ExitCode: -1, Count: 0, ReportPath: null, Feedback: string.Empty, Error: message, Duration: DateTimeOffset.Now - started);
        _ = WriteGateJsonAsync(gateJsonPath, startFailure, spec);
        return startFailure;
    }

    private static async Task<GateResult> DriveProcessAsync(Process process, GateSpec spec, string logPath, string gateJsonPath,
        DateTimeOffset started, string cwd, List<string> stdoutLines, CancellationToken token)
    {
        StreamWriter log = new(logPath, append: false, Json.Utf8);
        await using ConfiguredAsyncDisposable logDisposal = log.ConfigureAwait(false);
        object logLock = new();

        Task stdoutPump = PumpAsync(process.StandardOutput, stdoutLines, line => WriteLog(log, logLock, line), prefix: null, token);
        Task stderrPump = PumpAsync(process.StandardError, sink: null, write: line => WriteLog(log, logLock, line), prefix: "[stderr] ", token);

        bool timedOut = !process.WaitForExit(TimeSpan.FromMinutes(spec.TimeoutMinutes));
        if (timedOut)
        {
            KillTree(process);
        }
        else
        {
            token.ThrowIfCancellationRequested();
        }

        await DrainAsync(process, stdoutPump, stderrPump).ConfigureAwait(false);

        TimeSpan duration = DateTimeOffset.Now - started;
        return timedOut
            ? await FinishTimeoutAsync(gateJsonPath, spec, duration, stdoutLines, logPath).ConfigureAwait(false)
            : await FinishResultAsync(gateJsonPath, spec, duration, cwd, stdoutLines, logPath, process.ExitCode).ConfigureAwait(false);
    }

    private static void WriteLog(StreamWriter log, object logLock, string line)
    {
        lock (logLock)
        {
            log.WriteLine(line);
            log.Flush();
        }
    }

    private static async Task DrainAsync(Process process, Task stdoutPump, Task stderrPump)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }

        // The streams hang up once the process is reaped; let the pumps finish (with a safety net).
        try
        {
            await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException) { /* best-effort drain; the timeout was enforced on the process itself */ }
        catch (OperationCanceledException) { }
    }

    private static async Task<GateResult> FinishTimeoutAsync(string gateJsonPath, GateSpec spec, TimeSpan duration, List<string> stdoutLines, string logPath)
    {
        GateResult timeoutResult = new(
            Outcome: "error",
            ExitCode: -1,
            Count: null,
            ReportPath: null,
            Feedback: JoinLines(stdoutLines, spec.FeedbackMaxChars, logPath),
            Error: string.Create(CultureInfo.InvariantCulture, $"gate timed out after {spec.TimeoutMinutes} minutes"),
            Duration: duration);
        await WriteGateJsonAsync(gateJsonPath, timeoutResult, spec).ConfigureAwait(false);
        return timeoutResult;
    }

    private static async Task<GateResult> FinishResultAsync(string gateJsonPath, GateSpec spec, TimeSpan duration, string cwd, List<string> stdoutLines, string logPath, int exitCode)
    {
        string feedback = JoinLines(stdoutLines, spec.FeedbackMaxChars, logPath);
        CountResolution resolution = TryResolveCount(stdoutLines, cwd, spec);
        GateResult result = BuildResult(exitCode, spec, feedback, resolution, duration);
        await WriteGateJsonAsync(gateJsonPath, result, spec).ConfigureAwait(false);
        return result;
    }

    private static async Task PumpAsync(TextReader reader, List<string>? sink, Action<string> write, string? prefix, CancellationToken token)
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

                if (sink is not null)
                {
                    lock (sink)
                    {
                        sink.Add(line);
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

    private static string JoinLines(List<string> lines, int max, string logPath)
    {
        string text = string.Join('\n', lines);
        if (text.Length <= max)
        {
            return text;
        }

        return text[..max] + Environment.NewLine + $"[truncated; full gate output: {logPath}]";
    }

    private readonly record struct CountResolution(int? Count, string? ReportPath, string? Error);

    private static CountResolution TryResolveCount(List<string> stdoutLines, string cwd, GateSpec spec)
    {
        string? last = null;
        for (int i = stdoutLines.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(stdoutLines[i]))
            {
                last = stdoutLines[i];
                break;
            }
        }

        if (last is null)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "no non-empty stdout line was printed by the gate");
        }

        Match m = spec.ReportFromLastLine.Match(last);
        if (!m.Success)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: $"report path regex did not match the last stdout line '{Trim(last)}'");
        }

        if (m.Groups.Count < 2)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "report path regex has no capture group");
        }

        string? reportPath = m.Groups[1].Value.Trim();
        if (reportPath.Length is 0)
        {
            return new CountResolution(Count: null, ReportPath: null, Error: "report path was empty");
        }

        string full = Path.IsPathRooted(reportPath) ? reportPath : Path.GetFullPath(Path.Combine(cwd, reportPath));
        return TryReadReportFile(full, spec.CountPath);
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
        catch (System.Text.Json.JsonException ex)
        {
            return new CountResolution(Count: null, ReportPath: full, Error: $"report file '{full}' is not valid JSON: {ex.Message}");
        }

        JsonNode? target;
        try
        {
            target = ResolveCountPath(root, countPath);
        }
        catch (System.Text.Json.JsonException ex)
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

    private static async Task WriteGateJsonAsync(string path, GateResult result, GateSpec spec)
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
        };
        await File.WriteAllTextAsync(path, o.ToJsonString(Json.Indented), Json.Utf8, CancellationToken.None).ConfigureAwait(false);
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
