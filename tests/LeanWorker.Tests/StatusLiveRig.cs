using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// What a test of `status --wait` or `status --watch` needs: a StatusIndexRig (runs, pointers, fake /proc, fake clock),
/// a fake terminal and a StatusHost whose sleep moves the fake clock instead of waiting, so no test takes real time.
/// Every run added here has a launcher process of its own (101, 102, ...), so a test ends one run by removing its
/// process and rewriting its status file, as the launcher would. A call runs on a pool thread under a hard time
/// limit, and a sleep counter cuts a loop that never ends. A call replaces <see cref="Console.Error"/>: use only in the
/// "launcher-process-state" collection.
/// </summary>
internal sealed class StatusLiveRig : IDisposable
{
    /// <summary>
    /// The owner key the runs of this rig carry by default (pass it as --owner).
    /// </summary>
    public const string Key = "k:1";

    // More sleeps than any test needs: a loop that gets this far is cut and the test fails.
    private const int SleepCap = 50_000;

    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cancel = new();
    private readonly List<(TimeSpan At, Action Do)> _timed = [];
    private int _nextPid = 101;

    /// <summary>
    /// One run of the rig: its documents and its launcher's pid.
    /// </summary>
    internal sealed record Live(StatusIndexRig.Added Run, int Pid)
    {
        public string Dir => Run.Dir;
    }

    /// <summary>
    /// What a call gave: the exit code, everything written to the output, an exception that left the command (none
    /// when it returned) and what was written to the error stream.
    /// </summary>
    internal sealed record Result(int Code, string Output, Exception? Error, string Stderr)
    {
        public string[] Lines => StatusIndexRig.Lines(Output);
    }

    public StatusIndexRig Idx { get; } = new();

    public FakeStatusTerminal Terminal { get; } = new();

    /// <summary>
    /// The writer the command and the terminal output go to.
    /// </summary>
    public StringWriter Out { get; } = new();

    /// <summary>
    /// How many times the host's sleep was called, and for how long each time.
    /// </summary>
    public List<TimeSpan> Pauses { get; } = [];

    /// <summary>
    /// Whether the sleep counter cut a loop (always a test failure).
    /// </summary>
    public bool Runaway { get; private set; }

    /// <summary>
    /// Fake time passed since the rig's clock started.
    /// </summary>
    public TimeSpan Elapsed => Idx.Clock.GetUtcNow() - StatusDocs.Now;

    /// <summary>
    /// Fake time that passed on the monotonic timestamp: what the sleeps added up to, whatever steps the wall clock took.
    /// A timed action (<see cref="At"/>) runs by this time.
    /// </summary>
    public TimeSpan Passed => Idx.Clock.Monotonic;

    /// <summary>
    /// A live run (its own launcher process, in its worker phase), owned by <paramref name="key"/>. A chain's later
    /// round passes <paramref name="launcher"/>, the first round's pid, as every round of a chain has one launcher.
    /// </summary>
    public Live Add(string name, string key = Key, TimeSpan? startedAgo = null, string? chain = null, int round = 1, string? id = null,
        int? launcher = null, Action<JsonObject>? tweak = null)
    {
        int pid = launcher ?? _nextPid++;
        if (launcher is null)
        {
            Idx.Proc.Add(pid, StatusDocs.LauncherTicks);
        }

        StatusIndexRig.Added run = Idx.Add(name, [key], startedAgo: startedAgo, chain: chain, round: round, id: id, tweak: d =>
        {
            d["launcher"]!["pid"] = pid;
            tweak?.Invoke(d);
        });
        return new Live(run, pid);
    }

    /// <summary>
    /// A run that ended before the rig was called: its launcher is gone and its status file records the end.
    /// </summary>
    public StatusIndexRig.Added AddEnded(string name, string status = "success", TimeSpan? endedAgo = null, TimeSpan? startedAgo = null, string key = Key) =>
        Idx.Add(name, [key], endedAgo: endedAgo ?? TimeSpan.FromMinutes(1), startedAgo: startedAgo, status: status);

    /// <summary>
    /// The run's launcher ends: its status file records the end (<paramref name="status"/>, with
    /// <paramref name="exitCode"/> or the usual one) and its process is gone.
    /// </summary>
    public void End(Live run, string status = "success", int? exitCode = null, bool omitExitCode = false, string? error = null)
    {
        Record(run, status, exitCode, omitExitCode, error);
        Idx.Proc.Remove(run.Pid);
    }

    /// <summary>
    /// The run's status file records an end and its launcher lives on (a gate chain's launcher runs the next round).
    /// </summary>
    public void Record(Live run, string status = "success", int? exitCode = null, bool omitExitCode = false, string? error = null)
    {
        JsonObject end = new()
        {
            ["status"] = status,
            ["exit_code"] = exitCode ?? (status is "success" ? 0 : 1),
            ["at"] = StatusDocs.Stamp(Idx.Clock.GetUtcNow()),
            ["summary"] = "summary.json",
        };
        if (error is not null)
        {
            end["error"] = error;
        }

        if (omitExitCode)
        {
            _ = end.Remove("exit_code");
        }

        run.Run.Status["phase"] = "done";
        run.Run.Status["end"] = end;
        Write(run);
    }

    /// <summary>
    /// The launcher dies without a word: nothing recorded, no worker left (state lost).
    /// </summary>
    public void Lose(Live run) => Idx.Proc.Remove(run.Pid);

    /// <summary>
    /// The launcher dies without a word and its worker's group lives on (state orphaned).
    /// </summary>
    public void Orphan(Live run)
    {
        Idx.Proc.Remove(run.Pid);
        Idx.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
    }

    /// <summary>
    /// The launcher dies and the run dir is deleted (state gone).
    /// </summary>
    public void Vanish(Live run)
    {
        Idx.Proc.Remove(run.Pid);
        Directory.Delete(run.Dir, recursive: true);
    }

    /// <summary>
    /// The launcher's process cannot be read (state unknown, whatever happened to it).
    /// </summary>
    public void Blur(Live run)
    {
        Idx.Proc.Remove(run.Pid);
        Idx.Proc.AddUnreadable(run.Pid);
    }

    /// <summary>
    /// The run's status file stops being a status file (state unreadable while its launcher lives).
    /// </summary>
    public static void Garble(Live run) => File.WriteAllText(Path.Combine(run.Dir, "status.json"), "{ torn");

    /// <summary>
    /// The run's status file is never written (or is deleted): the run dir stays, its pointer stays.
    /// </summary>
    public static void Unwrite(Live run) => File.Delete(Path.Combine(run.Dir, "status.json"));

    /// <summary>
    /// The run's phase changes (gate, done, ...), its launcher still alive.
    /// </summary>
    public static void SetPhase(Live run, string phase)
    {
        run.Run.Status["phase"] = phase;
        Write(run);
    }

    /// <summary>
    /// The pointer of the run is removed from the index (the run drops out of the listing).
    /// </summary>
    public void Unlist(Live run) => File.Delete(Path.Combine(Idx.Folder, run.Run.Pointer));

    /// <summary>
    /// Runs <paramref name="what"/> once the fake clock's monotonic time has passed <paramref name="elapsed"/>, at the end of the sleep
    /// that took it there: the next read sees what it changed.
    /// </summary>
    public void At(TimeSpan elapsed, Action what) => _timed.Add((elapsed, what));

    /// <summary>
    /// The host's token is cancelled (the sleep that is running, or the next one, ends with a cancellation).
    /// </summary>
    public void CancelHost() => _cancel.Cancel();

    /// <summary>
    /// One call of the status command as the caller with no identity (scopes by --owner, --here or --all).
    /// </summary>
    public async Task<Result> CallAsync(params string[] args)
    {
        Result result = await CallAsync(StatusIndexRig.NoCaller, args);
        return result;
    }

    /// <summary>
    /// One call of the status command over this rig, as a caller with <paramref name="env"/>: under a hard time limit,
    /// with the sleeps moving the clock. It fails when the sleep counter had to cut the call short.
    /// </summary>
    public async Task<Result> CallAsync(Func<string, string?> env, params string[] args)
    {
        TextWriter oldErr = Console.Error;
        await using StringWriter err = new();
        Console.SetError(err);
        try
        {
            StatusReader reader = new(Idx.Proc.Fs, Idx.Clock, FakeProcRoot.DefaultBoot);
            StatusHost host = new(async (delay, token) => await SleepAsync(delay, token), Terminal, _cancel.Token);
            StatusEnvironment environment = new(env, Idx.Folder);
            Task<Result> call = Task.Run(async () =>
            {
                try
                {
                    int code = await StatusCommand.RunAsync(args, Out, reader, Idx.Root, environment, host);
                    return new Result(code, Out.ToString(), Error: null, string.Empty);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return new Result(-1, Out.ToString(), ex, string.Empty);
                }
            }, TestContext.Current.CancellationToken);
            Result result = await call.WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);
            Assert.False(Runaway, $"the call slept {Pauses.Count} times without ending: " + string.Join(' ', args));
            return result with { Stderr = err.ToString() };
        }
        finally
        {
            Console.SetError(oldErr);
        }
    }

    private async Task SleepAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Pauses.Add(delay);
        if (Pauses.Count > SleepCap)
        {
            Runaway = true;
            await _cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
        }

        Idx.Clock.Advance(delay);
        foreach ((TimeSpan at, Action action) in _timed.Where(t => t.At <= Passed).ToArray())
        {
            _ = _timed.Remove((at, action));
            action();
        }

        await Task.Yield();
    }

    private static void Write(Live run) => File.WriteAllText(Path.Combine(run.Dir, "status.json"), run.Run.Status.ToJsonString());

    public void Dispose()
    {
        _cancel.Dispose();
        Out.Dispose();
        Idx.Dispose();
    }
}
