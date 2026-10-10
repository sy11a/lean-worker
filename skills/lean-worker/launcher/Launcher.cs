// LeanWorker: runs one well-scoped task in a separate, minimal-context worker process (Claude Code `claude -p`
// or opencode `opencode run`) and prints a compact report with token usage and cost.
//
// Settings resolve in this order: command-line option > profile in <runs-root>/profiles.json > built-in default.
// Project notes (<runs-root>/project.md) are given to every worker unless --no-project-notes;
// a per-task --system file is appended after them.
//
// Spend is metered live from the worker's stream with the price book (prices.json). Past the wrap-up share of the
// budget a pre-tool hook blocks every tool call, so the worker's last message is a handoff; at the budget the
// launcher stops the worker.
//
// Exit codes: 0 = worker finished without error (or a gate chain ended clean), 1 = worker reported an error,
// 2 = launcher failed, 3 = worker wrapped up near its budget and left a handoff (continue with
// --continue-from <run-dir>), 4 = a gate chain ended stuck, 5 = a gate chain ended in an error.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Launcher
{
    internal static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    internal static readonly string[] PermissionModes = ["acceptEdits", "dontAsk", "plan", "manual", "auto", "bypassPermissions"];
    // git flags that write files or run programs whatever prefix allowed the command (deny beats allow).
    internal static readonly string[] DeniedFloor = ["Bash(git *--output*)", "Bash(git *--ext-diff*)", "Bash(git *--textconv*)"];
    internal const string ContinuationHeading = "## Continuation (lean-worker)";
    internal const string GateHeading = "## Gate report (lean-worker)";
    public const int RunSchemaVersion = 1;

    public static async Task<int> RunAsync(Options o)
    {
        if (o.Help)
        {
            await Console.Out.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return 0;
        }

        LaunchRun run = new(o, gateContext: null);
        int code = await run.RunAsync().ConfigureAwait(false);
        while (run.LastGate is { Decision: GateChain.Continue })
        {
            // The next round compares against round 1's before-worker trust snapshot and its fixed
            // path list, so nothing that changed on disk since then can become the new baseline.
            GateContext nextContext = new(
                ChainId: run.ChainId,
                Round: run.Round + 1,
                Counts: [.. run.Counts],
                ChainCostUsd: run.ChainCostUsd,
                OriginalTask: run.OriginalTask,
                OriginalName: run.OriginalName,
                Feedback: run.LastGate.Result,
                Spec: run.InitialGateSpec ?? throw new LaunchException("gate chain has no spec"),
                TrustSnapshot: run.TrustSnapshot ?? throw new LaunchException("gate chain has no trust snapshot"),
                TrustPaths: run.TrustPaths ?? throw new LaunchException("gate chain has no trust paths"),
                FixedSources: run.FixedSources ?? throw new LaunchException("gate chain has no trust path sources"),
                FixedRunnable: run.FixedRunnable ?? throw new LaunchException("gate chain has no runnable gate inputs"));
            run = new LaunchRun(o, gateContext: nextContext);
            code = await run.RunAsync().ConfigureAwait(false);
        }

        if (run.LastGate is null)
        {
            return code;
        }

        return run.LastGate.Decision switch
        {
            GateChain.Clean => 0,
            GateChain.Stuck => 4,
            GateChain.Error => 5,
            _ => code,
        };
    }

    /// <summary>
    /// metered | subscription. Anthropic's "auto" is a subscription when the worker uses the login, not a key.
    /// </summary>
    public static string Billing(Provider p, string runtime, bool hasKey)
    {
        return p.Billing switch
        {
            "auto" when runtime is "claude" => hasKey ? "metered" : "subscription",
            "auto" => "metered",
            var b => b,
        };
    }

    internal static string? NextInChain(List<string> chain, string provider, string model)
    {
        int i = chain.FindIndex(c => PriceBook.Split(c) == (provider, model));
        return i >= 0 && i + 1 < chain.Count ? chain[i + 1] : null;
    }

    internal static JsonObject? QuotaDelta(QuotaReading? before, QuotaReading? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        JsonObject d = [];
        foreach (QuotaWindow w in after.Windows)
        {
            if (before.Windows.Find(b => b.Name == w.Name) is { } b)
            {
                d[w.Name] = w.Percent - b.Percent;
            }
        }

        return d;
    }

    /// <summary>
    /// Runs the worker, handing every stdout line to onLine; onLine returns true to stop the worker (hard cap).
    /// </summary>
    internal static async Task<(int ExitCode, bool TimedOut, bool CapKilled)> RunWorkerAsync(Prepared prep, string stdin,
        string streamPath, string stderrPath, int timeoutMinutes, Func<string, bool> record, Func<string, bool> onLine)
    {
        ProcessStartInfo psi = StartInfo(prep);
        using Process p = Process.Start(psi) ?? throw new LaunchException($"could not start {prep.Executable}");
        using CancellationTokenSource cts = new();
        StreamWriter stream = new(streamPath, append: false, Json.Utf8);
        await using ConfiguredAsyncDisposable streamDisposal = stream.ConfigureAwait(false);
        StreamWriter stderr = new(stderrPath, append: false, Json.Utf8);
        await using ConfiguredAsyncDisposable stderrDisposal = stderr.ConfigureAwait(false);
        TaskCompletionSource errDone = new();
        MeterState state = new();
        Task pumpTask = PumpStdoutAsync(p.StandardOutput, line => TryMeter(state, stream, record, onLine, line),
            ex => FailClosed(state, stderrPath, ex), () => StopOnce(state, p), cts.Token);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                _ = errDone.TrySetResult();
            }
            else
            {
                lock (state.Gate) { if (!state.Closed) { stderr.WriteLine(e.Data); } }
            }
        };
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();
        bool timedOut = await WaitForExitAsync(p, timeoutMinutes).ConfigureAwait(false);
        _ = await Task.WhenAny(Task.WhenAll(pumpTask, errDone.Task), Task.Delay(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None)).ConfigureAwait(false);
        lock (state.Gate) { state.Closed = true; }
        await cts.CancelAsync().ConfigureAwait(false);
        await pumpTask.ConfigureAwait(false);
        bool capKilled;
        lock (state.Gate) { capKilled = state.CapKilled; }
        return (timedOut ? -1 : p.ExitCode, timedOut, capKilled);
    }

    private sealed class MeterState
    {
        public readonly object Gate = new();
        public bool Closed;
        public bool CapKilled;
    }

    private static void StopOnce(MeterState state, Process p)
    {
        lock (state.Gate)
        {
            if (!state.CapKilled)
            {
                state.CapKilled = true;
                KillQuietly(p);
            }
        }
    }

    /// <summary>
    /// Reads the worker's stdout line by line and meters every line via the supplied callback; a true return from
    /// meter, or a caught launch-failure exception from meter, triggers stop (which sets the cap-killed flag and
    /// kills the worker) but every subsequent line is still metered until the stream closes or cancellation ends
    /// the loop normally. An uncaught exception calls stop from the finally and propagates to the caller.
    /// </summary>
    private static async Task PumpStdoutAsync(TextReader stdout, Func<string, bool> meter, Action<Exception> failClosed, Action stop, CancellationToken token)
    {
        bool completed = false;
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                if (line is null)
                {
                    break;
                }
                bool stopLine = false;
                try
                {
                    if (meter(line))
                    {
                        stopLine = true;
                    }
                }
                catch (Exception ex) when (ex is LaunchException or System.Text.Json.JsonException or FormatException or OverflowException
                                               or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failClosed(ex);
                    stopLine = true;
                }
                if (stopLine)
                {
                    stop();
                }
            }
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                stop();
            }
        }
    }

    internal static ProcessStartInfo StartInfo(Prepared prep)
    {
        string cwd = Directory.GetCurrentDirectory();
        ProcessStartInfo psi = new()
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Json.Utf8,
            StandardErrorEncoding = Json.Utf8,
            FileName = prep.Executable,
            WorkingDirectory = cwd,
        };
        foreach (string arg in prep.Args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach ((string? k, string? v) in prep.Env)
        {
            if (v is null)
            {
                _ = psi.Environment.Remove(k);
            }
            else
            {
                psi.Environment[k] = v;
            }
        }
        // opencode reads its project dir from the inherited PWD env var, so pin it to the launcher's cwd
        psi.Environment["PWD"] = cwd;
        _ = psi.Environment.Remove("OLDPWD");
        return psi;
    }

    private static void KillQuietly(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task<bool> WaitForExitAsync(Process p, int timeoutMinutes)
    {
        bool timedOut = !p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes));
        if (timedOut)
        {
            KillQuietly(p);
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return timedOut;
    }

    private static bool TryMeter(MeterState state, StreamWriter stream, Func<string, bool> record, Func<string, bool> onLine, string data)
    {
        lock (state.Gate) { if (state.Closed) { return false; } if (record(data)) { stream.WriteLine(data); stream.Flush(); } }
        return onLine(data);
    }

    private static void FailClosed(MeterState state, string stderrPath, Exception ex)
    {
        lock (state.Gate)
        {
            if (!state.Closed)
            {
                File.AppendAllText(stderrPath + ".launcher", $"metering failed, worker stopped: {ex}{Environment.NewLine}");
            }
        }
    }

    /// <summary>
    /// The claude runtime reaches Anthropic and any provider with an Anthropic-compatible endpoint; others need opencode.
    /// </summary>
    internal static string DefaultRuntime(Provider p) => p.Name is "anthropic" || p.AnthropicBaseUrl is not null ? "claude" : "opencode";

    public static string? FindOnPath(string command)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim('"'), command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// First 12 hex chars of the SHA-256 of the UTF-8 bytes; the path is the same in every run of identical content.
    /// </summary>
    internal static string Sha12(string content)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Json.Utf8.GetBytes(content));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    internal static async Task<int> CountLinesAsync(string path)
    {
        int n = 0;
        await foreach (string _ in File.ReadLinesAsync(path, CancellationToken.None).ConfigureAwait(false))
        {
            n++;
        }
        return n;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically (temp + move), only if the file is absent.
    /// </summary>
    internal static void AtomicWrite(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, Json.Utf8);
            try { File.Move(temp, path); }
            catch (IOException) { /* lost the race; the other writer's content has the same hash */ }
        }
        finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> so a symlink planted at
    /// <paramref name="path"/> is replaced, not followed. The bytes go to a randomly named temp file
    /// in the same directory (an unpredictable name cannot be pre-planted), then a rename moves the
    /// temp over the path: rename swaps the directory entry, so an entry that is itself a symlink is
    /// replaced rather than written through. The temp file is deleted when anything fails.
    /// </summary>
    internal static async Task WritePlainAsync(string path, string content)
    {
        string temp = TempSibling(path);
        try
        {
            await File.WriteAllTextAsync(temp, content, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing like <c>new StreamWriter(path, append: false, encoding)</c>,
    /// but never follows a symlink planted at <paramref name="path"/>: the handle is created on a randomly
    /// named temp file in the same directory and the temp is renamed over the path while the handle is open,
    /// so the writer keeps writing the file the path now names (rename replaces the entry; the open handle
    /// follows the inode). See <see cref="WritePlainAsync"/> for the rationale. The temp file is deleted
    /// when the rename fails.
    /// </summary>
    internal static StreamWriter CreateWriter(string path, System.Text.Encoding encoding)
    {
        string temp = TempSibling(path);
        FileStream fs = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            fs.Dispose();
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return new StreamWriter(fs, encoding);
    }

    private static string TempSibling(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? ".", ".tmp-" + Guid.NewGuid().ToString("N"));
}