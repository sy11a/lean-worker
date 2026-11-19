// WorkerProcess: starts the worker process, feeds it the task on stdin, meters its stdout line by line, copies
// its stderr, and stops it at the timeout or when the meter asks (the hard cap). Split out of Launcher.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LeanWorker;

internal static class WorkerProcess
{
    /// <summary>
    /// Runs the worker, handing every stdout line to onLine; onLine returns true to stop the worker (hard cap).
    /// The exit code is the process's own, also after a timeout kill (<see cref="Launcher.RunWorkerAsync"/> maps that to -1).
    /// The worker leads a process group of its own (see <see cref="Prepared.Group"/>), and that group is
    /// signalled after the worker exits: the processes it leaves behind are reparented to init then and
    /// out of a tree kill's reach (issue #116).
    /// </summary>
    internal static async Task<(int ExitCode, bool TimedOut, bool CapKilled)> RunAsync(Prepared prep, string stdin,
        string streamPath, string stderrPath, int timeoutMinutes, Func<string, bool> record, Func<string, bool> onLine)
    {
        ProcessStartInfo psi = StartInfo(prep);
        using Process p = Process.Start(psi) ?? throw new LaunchException($"could not start {prep.Executable}");
        await prep.ResolveGroupAsync(p.Id).ConfigureAwait(false);
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
        bool timedOut;
        try
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            p.StandardInput.Close();
            timedOut = await WaitForExitAsync(p, timeoutMinutes).ConfigureAwait(false);
        }
        finally
        {
            // The worker has exited — or the launch failed before it could: whatever it left
            // behind is reparented to init and out of a tree kill's reach, so the group it shared
            // with the worker is signalled here — before the run decides what becomes of the
            // worktree (issue #116). The finally: an exception from the stdin write or from the
            // wait must not leave the group behind.
            await prep.FinishGroupAsync().ConfigureAwait(false);
        }

        _ = await Task.WhenAny(Task.WhenAll(pumpTask, errDone.Task), Task.Delay(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None)).ConfigureAwait(false);
        lock (state.Gate) { state.Closed = true; }
        await cts.CancelAsync().ConfigureAwait(false);
        await pumpTask.ConfigureAwait(false);
        bool capKilled;
        lock (state.Gate) { capKilled = state.CapKilled; }
        return (p.ExitCode, timedOut, capKilled);
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
        // The worker's working directory is the workspace root resolved from the task (see
        // Workspace.ResolveAsync); the launcher's own cwd is only a fallback for hand-built specs,
        // so a wrong directory the launcher was typed into cannot become the worker's project.
        string cwd = prep.WorkingDirectory ?? Directory.GetCurrentDirectory();
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
        // opencode reads its project dir from the inherited PWD env var, so pin it to the worker's
        // working directory (the workspace root the task's location resolves to)
        psi.Environment["PWD"] = cwd;
        _ = psi.Environment.Remove("OLDPWD");
        // The worker leads a process group of its own, so the processes it leaves behind can be
        // signalled after it exits (issue #116).
        prep.ApplyGroup(psi);
        return psi;
    }

    // The kill the timeout and cap paths use (the worker's whole process tree, quietly). On a
    // process that has already exited (or been disposed) the kill is a caught no-op.
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
}
