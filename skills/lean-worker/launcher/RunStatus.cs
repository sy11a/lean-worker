// The live status of one run: <run-dir>/status.json (schema lean-worker.status/1), the whole
// object written again at each phase change, at wrap-up and after each stream line that changes a
// displayed field. A reader (`status`) sees the old or the new file, never a torn one: every write
// goes through a temp sibling plus rename, and one lock serialises every write and assigns `seq`
// inside it, so no older object is published after a newer one.
//
// Best effort: a failed write never fails or changes the run. The first failure leaves one note
// for the summary; later writes are still tried. LEAN_WORKER_STATUS=off turns the writer off, and
// with it the run's pointer in the per-user index (StatusIndex, written once after the first
// status.json) and the record-only signal handler (StatusSignals). The owner (OwnerKeys) is
// resolved once at the start and recorded in both files.
//
// The file is trusted for display only: no verdict, no ledger field and no exit code is read
// from it (docs/research/live-status/README.md, section 4.1).

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// The writer of <c>status.json</c>. The main thread reports the phases, the stream thread the
/// lines; both go through one lock.
/// </summary>
internal sealed class RunStatus
{
    public const string FileName = "status.json";
    public const string Schema = "lean-worker.status/1";
    public const string KillSwitch = "LEAN_WORKER_STATUS";

    /// <summary>
    /// The end status of a launcher that failed with an exception (not a ledger status).
    /// </summary>
    public const string LaunchFailed = "launch-failed";

    /// <summary>
    /// The end status of a launcher that got SIGTERM, SIGINT or SIGHUP (not a ledger status).
    /// </summary>
    public const string LauncherSignalled = "launcher-signalled";

    /// <summary>
    /// How long the signal handler waits for a status write in progress and its own write together
    /// before it gives up: the signal's default action follows either way, so the handler never
    /// holds the launcher's end up for longer than this.
    /// </summary>
    internal static readonly TimeSpan SignalLockWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A stream line that changes nothing displayed still refreshes the file when the last write is
    /// this old, so <c>last_event_at</c> does not lag a long-streaming step by more than this.
    /// </summary>
    internal static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the writes stay held back after a recorded signal: a launcher still running when
    /// this has passed survived the signal (its default action did not end it), and the file shows
    /// its own state again.
    /// </summary>
    internal static readonly TimeSpan SignalHold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest tool name stored: the name comes from the worker's stream.
    /// </summary>
    internal const int ToolNameLimit = 64;

    private static readonly JsonSerializerOptions _snakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly object _lock = new();
    private readonly RunStatusSpec _spec;
    private readonly ProcFs _proc;
    private readonly TimeProvider _clock;
    private readonly long _startTimestamp;
    private readonly string _path;
    private readonly JsonObject _launcher;
    private readonly int _launcherPid;
    private RunOwner? _owner;
    private StatusSignals? _signals;
    private int? _gateMaxRounds;
    private JsonObject? _worker;
    private Meter? _meter;
    private Outcome? _outcome;
    private IRuntime? _runtime;
    private string _phase = RunPhase.Starting;
    private int? _gateRound;
    private DateTimeOffset? _lastEventAt;
    private LastTool? _lastTool;
    private JsonObject? _end;
    // The recorded signal's end while the hold runs: the file shows it (phase failed, no gate round)
    // over the run's own state, which the held-back writers keep updating underneath.
    private JsonObject? _signalEnd;
    private IDisposable? _holdTimer;
    private long _holdGeneration;
    private long _seq;
    private string? _shown;
    private long _lastWriteTimestamp;
    private string? _note;
    private bool _noteTaken;

    /// <summary>
    /// A writer for the run in <paramref name="spec"/>, reading /proc through <paramref name="proc"/>
    /// and the time from <paramref name="clock"/>; <paramref name="launcherPid"/> is the launcher's
    /// own pid. Nothing is written until the first phase is reported.
    /// </summary>
    public RunStatus(RunStatusSpec spec, ProcFs proc, TimeProvider clock, int launcherPid)
    {
        _spec = spec;
        _proc = proc;
        _clock = clock;
        _startTimestamp = clock.GetTimestamp();
        _lastWriteTimestamp = _startTimestamp;
        _path = Path.Combine(spec.RunDir, FileName);
        _launcherPid = launcherPid;
        HoldTimer = (due, fire) => clock.CreateTimer(_ => fire(), state: null, due, Timeout.InfiniteTimeSpan);
        _launcher = new JsonObject { ["pid"] = launcherPid, ["start_ticks"] = null, ["boot_id"] = null };
        BestEffort(() =>
        {
            ProcProbe self = proc.Stat(launcherPid);
            _launcher["start_ticks"] = self.Kind is ProbeKind.Present ? self.Stat.StartTicks : null;
            _launcher["boot_id"] = proc.BootId();
        });
    }

    /// <summary>
    /// The one-shot timer of the signal hold (the test seam): it calls the action once after the
    /// time given and is disposed to cancel it. The default is a timer of the writer's clock, which
    /// never keeps the process alive.
    /// </summary>
    internal Func<TimeSpan, Action, IDisposable> HoldTimer { get; set; }

    /// <summary>
    /// The sequence number of the last write (0 before the first).
    /// </summary>
    public long Seq
    {
        get { lock (_lock) { return _seq; } }
    }

    /// <summary>
    /// The note of the first failed write; null while every write worked.
    /// </summary>
    public string? Note
    {
        get { lock (_lock) { return _note; } }
    }

    /// <summary>
    /// Whether the environment's <see cref="KillSwitch"/> value leaves the writer on: anything but "off".
    /// </summary>
    public static bool Enabled(string? killSwitch) => !string.Equals(killSwitch?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The writer of a launch, from what the run records at its start: null when
    /// <see cref="KillSwitch"/> is off. The first write (<c>phase: starting</c>) happens here,
    /// right after the run dir exists, then the pointer in the per-user index; from here until
    /// the run's last status write, SIGTERM, SIGINT and SIGHUP are recorded. <paramref name="owner"/>
    /// is the launch's --owner key (already checked), if any.
    /// </summary>
    public static RunStatus? Open(string runDir, string name, string chainId, int round, string? profile, string runtime,
        string provider, string model, decimal budgetUsd, decimal? wrapUpUsd, int timeoutMinutes, DateTimeOffset startedAt, string? worktree,
        string? owner = null)
    {
        RunStatusSpec spec = new(runDir, name, chainId, round, profile, runtime, provider, model, budgetUsd, wrapUpUsd,
            timeoutMinutes, startedAt, worktree);
        return Open(spec, owner, variable => Environment.GetEnvironmentVariable(variable), ProcFs.Host, TimeProvider.System, Environment.ProcessId, handleSignals: true);
    }

    /// <summary>
    /// <see cref="Open(string, string, string, int, string?, string, string, string, decimal, decimal?, int, DateTimeOffset, string?, string?)"/>
    /// with its inputs: the environment (the kill switch, the owner variables, the index folder),
    /// /proc, the clock, the launcher's pid and whether the signal handler is registered.
    /// </summary>
    internal static RunStatus? Open(RunStatusSpec spec, string? owner, Func<string, string?> env, ProcFs proc, TimeProvider clock,
        int launcherPid, bool handleSignals)
    {
        if (!Enabled(env(KillSwitch)))
        {
            return null;
        }

        RunStatus status = new(spec, proc, clock, launcherPid);
        status.BestEffort(() => status.Own(OwnerKeys.AtLaunch(owner, env, proc)));
        status.Phase(RunPhase.Starting);
        status.WritePointer(StatusIndex.Folder(env));
        if (handleSignals)
        {
            status.HandleSignals();
        }

        return status;
    }

    /// <summary>
    /// The owner the status file and the pointer record.
    /// </summary>
    internal void Own(RunOwner owner)
    {
        lock (_lock)
        {
            _owner = owner;
        }
    }

    /// <summary>
    /// Writes the run's pointer into the index folder <paramref name="folder"/> (null: none could
    /// be found), once; a failure is a status failure like any other (one note, the run goes on).
    /// </summary>
    internal void WritePointer(string? folder) => BestEffort(() => RecordPointer(folder), "the run index pointer");

    private void RecordPointer(string? folder)
    {
        if (folder is null)
        {
            throw new IOException("no folder for it: HOME, XDG_STATE_HOME and LEAN_WORKER_STATE_DIR give no absolute path");
        }

        JsonObject pointer;
        string fileName;
        lock (_lock)
        {
            RunOwner owner = _owner ?? RunOwner.None;
            pointer = StatusIndex.Pointer(_clock.GetUtcNow(), Path.GetFullPath(_spec.RunDir), _spec.ChainId, owner.ParentRun, owner.Keys,
                _launcher.DeepClone().AsObject());
            fileName = StatusIndex.FileName(_spec.StartedAt, _launcherPid, _spec.Round);
        }

        _ = StatusIndex.Write(folder, fileName, pointer);
    }

    /// <summary>
    /// Registers the record-only signal handler (<see cref="Signalled"/>) until the run's last
    /// write, for the signals the launcher does not ignore (<see cref="ProcFs.IgnoredSignals"/>; none
    /// when that cannot be read).
    /// </summary>
    internal void HandleSignals()
    {
        BestEffort(() =>
        {
            StatusSignals signals = StatusSignals.Register(signal => _ = Signalled(signal), _proc.IgnoredSignals());
            lock (_lock)
            {
                _signals = signals;
            }
        });
    }

    /// <summary>
    /// The launcher got <paramref name="signal"/>: unless the run already has its end, the file
    /// gets <c>phase: failed</c> and <c>end.status: launcher-signalled</c> with the signal's name,
    /// and the other writes are held back for <see cref="SignalHold"/> (a second signal records
    /// again and restarts it), so none erases the record while the launcher dies of it; a launcher
    /// still running then survived it and the file shows its own state again (<c>Finish</c> and
    /// <c>Fail</c> write their end at once). The wait for the lock and the write together take at most <see cref="SignalLockWait"/>: they
    /// run on a thread of their own, and a write in progress that holds the lock longer, or a write
    /// of the handler's own that blocks (a stalled disk), is abandoned, so the handler can never
    /// deadlock with the writer and the signal's default action follows either way. True when it
    /// was recorded within the bound.
    /// </summary>
    internal bool Signalled(PosixSignal signal)
    {
        // Set once the handler gives up: a record that only then gets the lock writes nothing.
        int abandoned = 0;
        try
        {
            if (new ReadBudget(SignalLockWait, TimeSpan.Zero).Run(() => RecordSignal(signal, () => Volatile.Read(ref abandoned) is 1), out bool recorded))
            {
                return recorded;
            }
        }
        catch (IOException)
        {
            // RecordSignal keeps its own failures; nothing may leave the handler.
        }

        _ = Interlocked.Exchange(ref abandoned, 1);
        return false;
    }

    private bool RecordSignal(PosixSignal signal, Func<bool> abandoned)
    {
        if (!Monitor.TryEnter(_lock, SignalLockWait))
        {
            return false;
        }

        try
        {
            if (_end is not null || abandoned())
            {
                return false;
            }

            // The hold starts (or, for a second signal during it, starts again) before the record is
            // set: a record whose write throws is still taken back when the hold ends.
            JsonObject? before = _signalEnd;
            StartHold();
            _signalEnd = new JsonObject
            {
                ["status"] = LauncherSignalled,
                ["exit_code"] = null,
                ["at"] = Stamp(_clock.GetUtcNow()),
                ["signal"] = StatusSignals.Name(signal),
            };
            // Asked again at the last point before the file is replaced: a write that stalled anywhere
            // until the handler gave up publishes nothing, and the state is the one before the signal.
            if (WriteLocked(() => !abandoned()))
            {
                return true;
            }

            _signalEnd = before;
            if (before is null)
            {
                StopHold();
            }

            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
        {
            // The lock is held here, so the note is set without waiting for it again.
            _note ??= $"status.json could not be updated ({ex.GetType().Name}); the run itself is not affected";
            return false;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>
    /// Under the lock: (re)starts the one-shot timer of the signal hold. Only the timer of the
    /// latest start acts when it fires.
    /// </summary>
    private void StartHold()
    {
        StopHold();
        long generation = _holdGeneration;
        _holdTimer = HoldTimer(SignalHold, () => BestEffort(() => HoldEnded(generation)));
    }

    /// <summary>
    /// Under the lock: cancels the hold's timer; a callback already on its way finds a newer
    /// generation and does nothing.
    /// </summary>
    private void StopHold()
    {
        _holdGeneration++;
        IDisposable? timer = _holdTimer;
        _holdTimer = null;
        timer?.Dispose();
    }

    /// <summary>
    /// The hold's timer fired: the launcher is still running, so it survived the signal it recorded
    /// (its default action did not end it: a SIGTERM inherited as ignored, which the runtime's own
    /// handler hides from SigIgn, does that). The record goes and one write shows the run's own
    /// state, with whatever the held-back writers changed. Nothing happens after the run's end, or
    /// for a timer a later signal or the end replaced.
    /// </summary>
    private void HoldEnded(long generation)
    {
        lock (_lock)
        {
            if (generation != _holdGeneration || _signalEnd is null || _end is not null)
            {
                return;
            }

            StopHold();
            _signalEnd = null;
            WriteLocked();
        }
    }

    /// <summary>
    /// Under the lock: a normal write, unless a recorded signal is held (the launcher may be dying
    /// of it, and no write may erase the record before that); the state it carries is written when
    /// the hold ends.
    /// </summary>
    private void WriteUnlessHeld()
    {
        if (_signalEnd is not null)
        {
            return;
        }

        WriteLocked();
    }

    /// <summary>
    /// Ends the signal handler: the run's last write is done.
    /// </summary>
    private void StopSignals()
    {
        StatusSignals? signals;
        lock (_lock)
        {
            signals = _signals;
            _signals = null;
        }

        // Outside the lock: a handler waiting for it is never waited on here.
        signals?.Dispose();
    }

    /// <summary>
    /// The live counters' sources: the Meter (calls, tokens, cost, wrap-up), the outcome and the
    /// runtime (turns, thinking tokens).
    /// </summary>
    public void Track(Meter meter, Outcome outcome, IRuntime runtime)
    {
        lock (_lock)
        {
            _meter = meter;
            _outcome = outcome;
            _runtime = runtime;
        }
    }

    /// <summary>
    /// The worker runs: its pid, and its group as the launcher read it.
    /// </summary>
    public void WorkerStarted(int pid, WorkerGroup group) =>
        BestEffort(() => WorkerStarted(pid, group.Available && group.GroupId > 0 ? group.GroupId : null));

    /// <summary>
    /// The worker runs: its pid, its group id (null without a group of its own), and its session
    /// and start time read from /proc. The phase becomes <c>worker</c>.
    /// </summary>
    public void WorkerStarted(int pid, int? pgid) => BestEffort(() => RecordWorker(pid, pgid));

    private void RecordWorker(int pid, int? pgid)
    {
        ProcProbe probe = _proc.Stat(pid);
        bool present = probe.Kind is ProbeKind.Present;
        lock (_lock)
        {
            _worker = new JsonObject
            {
                ["pid"] = pid,
                ["pgid"] = pgid,
                ["sid"] = pgid is not null && present ? probe.Stat.Session : null,
                ["start_ticks"] = present ? probe.Stat.StartTicks : null,
            };
            _phase = RunPhase.Worker;
            WriteUnlessHeld();
        }
    }

    /// <summary>
    /// One stream line, after the Meter has seen it: the runtime reads what it says besides usage
    /// (<see cref="IRuntime.Activity"/>); <paramref name="usage"/> is whether it carried usage.
    /// What the runtime cannot read in the line is a failed status update like any other.
    /// </summary>
    public void OnLine(IRuntime runtime, JsonObject line, bool usage) => BestEffort(() => OnLine(runtime.Activity(line), usage));

    /// <summary>
    /// One stream line, after the Meter has seen it: <paramref name="activity"/> is what the runtime
    /// read from it besides usage, <paramref name="usage"/> whether it carried usage. The file is
    /// written when a displayed field changed (a call, tokens, cost, turns, wrap-up, a tool), on an
    /// error or retry event, and when the last write is older than <see cref="RefreshAfter"/>.
    /// </summary>
    public void OnLine(StreamActivity? activity, bool usage) => BestEffort(() => RecordLine(activity, usage));

    private void RecordLine(StreamActivity? activity, bool usage)
    {
        lock (_lock)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            _lastEventAt = now;
            bool due = activity is not null;
            if (activity is not null)
            {
                Apply(activity, now);
            }

            if (!due && usage)
            {
                due = !string.Equals(Counters().ToJsonString(), _shown, StringComparison.Ordinal);
            }

            if (due || _clock.GetElapsedTime(_lastWriteTimestamp) >= RefreshAfter)
            {
                WriteUnlessHeld();
            }
        }
    }

    /// <summary>
    /// The gate of round <paramref name="round"/> runs.
    /// </summary>
    public void GateStarted(int round) => Phase(RunPhase.Gate, round);

    /// <summary>
    /// The gate of round <paramref name="round"/> of at most <paramref name="maxRounds"/> runs.
    /// </summary>
    public void GateStarted(int round, int maxRounds)
    {
        BestEffort(() =>
        {
            lock (_lock)
            {
                _gateMaxRounds = maxRounds;
            }

            RecordPhase(RunPhase.Gate, round);
        });
    }

    /// <summary>
    /// The launcher decides the worktree, computes the stats and writes the summary.
    /// </summary>
    public void Finishing() => Phase(RunPhase.Finishing);

    /// <summary>
    /// A phase change; <paramref name="gateRound"/> is the round of the <c>gate</c> phase.
    /// </summary>
    public void Phase(string phase, int? gateRound = null) => BestEffort(() => RecordPhase(phase, gateRound));

    private void RecordPhase(string phase, int? gateRound)
    {
        lock (_lock)
        {
            _phase = phase;
            _gateRound = phase is RunPhase.Gate ? gateRound : null;
            WriteUnlessHeld();
        }
    }

    /// <summary>
    /// The run is done: summary, report and ledger row are written. <paramref name="status"/> is the
    /// ledger's status, <paramref name="exitCode"/> the launcher's exit code for this round.
    /// <paramref name="gate"/> is <see cref="GateChain.Continue"/> when this round's gate decided to
    /// go on to another round (the end then records it as <c>gate</c>, written with the end itself,
    /// so before the next round starts), else null and the end has no <c>gate</c>.
    /// </summary>
    public void Finish(string status, int exitCode, string? gate = null)
    {
        BestEffort(() => RecordFinish(status, exitCode, gate));
        BestEffort(() => StopSignals());
    }

    private void RecordFinish(string status, int exitCode, string? gate)
    {
        lock (_lock)
        {
            _phase = RunPhase.Done;
            _gateRound = null;
            // The run's own end replaces a held signal record at once.
            StopHold();
            _signalEnd = null;
            _end = new JsonObject
            {
                ["status"] = status,
                ["exit_code"] = exitCode,
                ["at"] = Stamp(_clock.GetUtcNow()),
                ["summary"] = "summary.json",
            };
            if (gate is not null)
            {
                _end["gate"] = gate;
            }

            WriteLocked();
        }
    }

    /// <summary>
    /// The launcher failed with an exception and is about to print LEAN-WORKER LAUNCH FAILED.
    /// <paramref name="summaryStatus"/> is the summary's status when summary.json was already
    /// written, else null. Like every write it never throws: the caller's exception stays the one
    /// that propagates.
    /// </summary>
    public void Fail(string message, string? summaryStatus)
    {
        BestEffort(() => RecordFailure(message, summaryStatus));
        BestEffort(() => StopSignals());
    }

    private void RecordFailure(string message, string? summaryStatus)
    {
        lock (_lock)
        {
            _phase = RunPhase.Failed;
            _gateRound = null;
            StopHold();
            _signalEnd = null;
            _end = new JsonObject
            {
                ["status"] = LaunchFailed,
                ["exit_code"] = null,
                ["at"] = Stamp(_clock.GetUtcNow()),
                // An exception's message can quote stream content: no lone surrogate may make the file unwritable.
                ["error"] = WellFormed(message),
                ["after_summary"] = summaryStatus is not null,
            };
            if (summaryStatus is not null)
            {
                _end["summary_status"] = WellFormed(summaryStatus);
            }

            WriteLocked();
        }
    }

    /// <summary>
    /// Runs one status update; any failure of it (an I/O error, a stream value the runtime cannot
    /// read, a counter that overflows) leaves the first note and nothing else: no exception leaves
    /// a public method of the writer, so the stream pump, the Meter and the launcher's own paths
    /// see the run exactly as with the writer off.
    /// </summary>
    private void BestEffort(Action update, string what = FileName)
    {
        try
        {
            update();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                _note ??= $"{what} could not be written ({ex.Message}); the run itself is not affected";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
        {
            // The type only: the message of a parse or format failure can carry the stream's content.
            lock (_lock)
            {
                _note ??= $"{what} could not be updated ({ex.GetType().Name}); the run itself is not affected";
            }
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every lone surrogate replaced by U+FFFD, so it can be written as JSON.
    /// </summary>
    internal static string WellFormed(string text)
    {
        if (!text.Any(c => char.IsSurrogate(c)))
        {
            return text;
        }

        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '�';
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Adds the note of the first failed write to <paramref name="notes"/>, once.
    /// </summary>
    public void AddNoteTo(List<string> notes)
    {
        lock (_lock)
        {
            if (_note is null || _noteTaken)
            {
                return;
            }

            _noteTaken = true;
            notes.Add(_note);
        }
    }

    /// <summary>
    /// The object the next write would publish, with the current seq (for tests).
    /// </summary>
    internal JsonObject Snapshot()
    {
        lock (_lock)
        {
            return Build(Counters());
        }
    }

    /// <summary>
    /// A UTC time as the status files write it: ISO 8601 with milliseconds and a Z.
    /// </summary>
    public static string Stamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A tool start, or a tool end that names its tool, becomes the last tool; a tool end that
    /// names only its call (claude's tool_result) updates the last tool when it is that call.
    /// </summary>
    private void Apply(StreamActivity activity, DateTimeOffset now)
    {
        string? state = ToolState(activity.State);
        if (activity.Kind is StreamActivityKind.ToolStart || (activity.Kind is StreamActivityKind.ToolEnd && activity.Tool is not null))
        {
            _lastTool = new LastTool(ToolName(activity.Tool), activity.CallId, now, state);
        }
        else if (activity.Kind is StreamActivityKind.ToolEnd && _lastTool is not null && activity.CallId is not null
                 && string.Equals(activity.CallId, _lastTool.CallId, StringComparison.Ordinal))
        {
            _lastTool = _lastTool with { At = now, State = state };
        }
    }

    /// <summary>
    /// A tool state as the file stores it: <c>running</c> (opencode's <c>pending</c> too),
    /// <c>completed</c> (also when the event gives none) or <c>error</c>; null for anything else,
    /// which is never stored as the stream gave it.
    /// </summary>
    internal static string? ToolState(string? state)
    {
        return state switch
        {
            null or StreamActivity.Completed => StreamActivity.Completed,
            StreamActivity.Running or "pending" => StreamActivity.Running,
            StreamActivity.Error => StreamActivity.Error,
            _ => null,
        };
    }

    /// <summary>
    /// A tool name as the file stores it: at most <see cref="ToolNameLimit"/> characters of
    /// <c>[A-Za-z0-9_.:/-]</c>, any other character replaced by <c>?</c>; <c>?</c> when there is none.
    /// </summary>
    internal static string ToolName(string? tool)
    {
        if (string.IsNullOrEmpty(tool))
        {
            return "?";
        }

        string kept = tool.Length > ToolNameLimit ? tool[..ToolNameLimit] : tool;
        return string.Create(kept.Length, kept, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '/' or '-' ? c : '?';
            }
        });
    }

    // A failure here propagates to BestEffort, which leaves the note.
    private void WriteLocked() => _ = WriteLocked(static () => true);

    /// <summary>
    /// The write, published only while <paramref name="publish"/> says so at the last point before the
    /// file is replaced. When it says no, the file and the write's own state (seq, shown counters, last
    /// write time) stay as they were. True when the file was replaced.
    /// </summary>
    private bool WriteLocked(Func<bool> publish)
    {
        (long seq, string? shown, long lastWrite) = (_seq, _shown, _lastWriteTimestamp);
        _seq++;
        JsonObject counters = Counters();
        _shown = counters.ToJsonString();
        JsonObject doc = Build(counters);
        _lastWriteTimestamp = _clock.GetTimestamp();
        if (Launcher.WritePlain(_path, doc.ToJsonString(Json.Indented), publish))
        {
            return true;
        }

        (_seq, _shown, _lastWriteTimestamp) = (seq, shown, lastWrite);
        return false;
    }

    /// <summary>
    /// The counters a write compares against the last one: a change in any of them is a displayed change.
    /// </summary>
    private JsonObject Counters()
    {
        List<Usage> calls = _meter?.Calls() ?? [];
        long? turns = _outcome is not null && _runtime is not null ? _runtime.LiveTurns(_outcome) : null;
        return new JsonObject
        {
            ["api_calls"] = calls.Count,
            ["turns"] = turns,
            ["tokens"] = Usage.Totals(calls, _outcome?.Thinking ?? 0),
            ["cost_usd"] = decimal.Round(_meter?.Spent ?? 0m, 6, MidpointRounding.ToEven),
            ["wrapped_up"] = _meter?.WrappedUp ?? false,
            ["last_tool"] = _lastTool?.ToJson(),
        };
    }

    private JsonObject Build(JsonObject live)
    {
        live["elapsed_ms"] = (long)_clock.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        live["last_event_at"] = _lastEventAt is { } at ? Stamp(at) : null;
        bool signalled = _signalEnd is not null;
        live["gate_round"] = signalled ? null : _gateRound;
        if (_gateMaxRounds is { } maxRounds)
        {
            live["gate_max_rounds"] = maxRounds;
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["seq"] = _seq,
            ["updated_at"] = Stamp(_clock.GetUtcNow()),
            ["run"] = new JsonObject
            {
                ["id"] = Path.GetFileName(_spec.RunDir),
                ["dir"] = Path.GetFullPath(_spec.RunDir),
                ["name"] = _spec.Name,
                ["chain_id"] = _spec.ChainId,
                ["round"] = _spec.Round,
                ["parent_run"] = _owner?.ParentRun,
            },
            ["launcher"] = _launcher.DeepClone(),
            ["worker"] = _worker?.DeepClone(),
            ["owner"] = _owner?.ToJson(),
            ["spec"] = SpecJson(),
            ["phase"] = signalled ? RunPhase.Failed : _phase,
            ["live"] = live,
            ["end"] = (_signalEnd ?? _end)?.DeepClone(),
        };
    }

    /// <summary>
    /// The spec section: the run's settings, its start and the worker's deadline, in snake_case.
    /// </summary>
    private JsonNode? SpecJson()
    {
        SpecSection section = new(_spec.Profile, _spec.Runtime, _spec.Provider, _spec.Model, _spec.BudgetUsd, _spec.WrapUpUsd,
            _spec.TimeoutMinutes, Stamp(_spec.StartedAt), Stamp(_spec.StartedAt.AddMinutes(_spec.TimeoutMinutes)), _spec.Worktree);
        return JsonSerializer.SerializeToNode(section, _snakeCase);
    }

    /// <summary>
    /// The fields of the spec section, in the order the file shows them.
    /// </summary>
    private sealed record SpecSection(string? Profile, string Runtime, string Provider, string Model, decimal BudgetUsd,
        decimal? WrapUpUsd, int TimeoutMinutes, string StartedAt, string DeadlineAt, string? Worktree);

    /// <summary>
    /// The last tool call the stream showed: its name, its call id (to match the result), when the
    /// last event about it came and its state.
    /// </summary>
    private sealed record LastTool(string Name, string? CallId, DateTimeOffset At, string? State)
    {
        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["name"] = Name,
                ["at"] = Stamp(At),
                ["state"] = State,
            };
        }
    }
}
