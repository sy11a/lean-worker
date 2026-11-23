// The status reader: one run dir into one row (docs/research/live-status/README.md, section 4.1,
// "What a reader does" and the ended rule). It never writes or deletes anything. The /proc root,
// the clock (with its local time zone, for the stamps in legacy dir names), the current boot id and
// the scan budget (ScanBudget, ReadFloor) are its inputs, so a test drives it without a real /proc.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class StatusReader(ProcFs proc, TimeProvider clock, string? bootId)
{
    /// <summary>
    /// A live run whose last event is older than this is marked quiet.
    /// </summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(10);

    private const int StampLength = 15;

    private readonly StatusProbe _probe = new(proc, bootId);

    public TimeProvider Clock { get; } = clock;

    /// <summary>
    /// The /proc the reader probes (also for the caller's pid keys).
    /// </summary>
    public ProcFs Proc { get; } = proc;

    /// <summary>
    /// The reader of this machine: the real /proc, the system clock and the current boot id.
    /// </summary>
    public static StatusReader Host() => new(ProcFs.Host, TimeProvider.System, ProcFs.Host.BootId());

    /// <summary>
    /// The detail of a run whose files gave the reader something it could not use.
    /// </summary>
    internal const string UnusableDetail = "the run's files hold content the reader cannot use";

    /// <summary>
    /// The detail of a run whose files did not answer within the scan's budget.
    /// </summary>
    internal const string BlockedDetail = "a file of the run did not answer in time; not read";

    /// <summary>
    /// The scan budget a reader starts with (<see cref="ReadBudget.DefaultTotal"/>; a test sets less).
    /// </summary>
    public TimeSpan ScanBudget { get; init; } = ReadBudget.DefaultTotal;

    /// <summary>
    /// What each read still gets once the scan budget is used up (<see cref="ReadBudget.DefaultFloor"/>).
    /// </summary>
    public TimeSpan ReadFloor { get; init; } = ReadBudget.DefaultFloor;

    /// <summary>
    /// A fresh budget for one scan, with this reader's limits.
    /// </summary>
    public ReadBudget NewBudget() => new(ScanBudget, ReadFloor);

    /// <summary>
    /// One run dir, with a budget of its own.
    /// </summary>
    public StatusRow Read(string runDir) => Read(runDir, NewBudget());

    /// <summary>
    /// One run dir: a legacy row without status.json, an unreadable row when the run dir or one of
    /// its files is not a plain directory or regular file, when status.json does not parse (after
    /// one retry), when the files do not answer within <paramref name="budget"/>, or when anything
    /// about the run fails while it is read; else the state the ended rule gives. Nothing about one
    /// run dir throws out of here. A run read through a pointer (<paramref name="launcher"/> is the
    /// pointer's launcher identity, which then decides the ended rule) is <c>gone</c> when its run
    /// dir or its status.json is provably not there and its launcher is not alive.
    /// </summary>
    public StatusRow Read(string runDir, ReadBudget budget, JsonObject? launcher = null)
    {
        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(runDir));
        bool pointed = launcher is not null;
        try
        {
            // Every lstat, stat, open and read of the run dir runs on the thread with the deadline.
            return budget.Run(() => ReadGuarded(runDir, id, launcher, pointed), out StatusRow? row) && row is not null
                ? row
                : Unreadable(runDir, id, BlockedDetail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
        {
            return Unreadable(runDir, id, UnusableDetail);
        }
    }

    private StatusRow ReadGuarded(string runDir, string id, JsonObject? launcher, bool pointed)
    {
        try
        {
            (StatusRow row, bool read) = ReadChecked(runDir, id, launcher, pointed);
            // Checked again after the reads: a run dir swapped for a link while it was read would
            // have had its files read from the link's target (StatusFile's header).
            return read && StatusFile.IsLink(runDir)
                ? Unreadable(runDir, id, "the run dir became a symbolic link while it was read; not used")
                : row;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
        {
            // Whatever the run's files hold: the row says so with a fixed text, never the exception's.
            return Unreadable(runDir, id, UnusableDetail);
        }
    }

    /// <summary>
    /// The row of a run dir, and whether files of it were read (so the run dir is checked again).
    /// A run read through a pointer has the pointer's launcher probed first: while it is alive the
    /// run is live whatever its files say; else a run dir or status file that cannot be reached is
    /// gone only when it provably is not there (<see cref="StatusFile.Exists"/>), unknown otherwise.
    /// </summary>
    private (StatusRow Row, bool Read) ReadChecked(string runDir, string id, JsonObject? launcher, bool pointed)
    {
        if (StatusFile.IsLink(runDir))
        {
            return (Unreadable(runDir, id, "the run dir is a symbolic link; not followed"), false);
        }

        if (!pointed)
        {
            return ReadStatus(runDir, id, verdict: null);
        }

        LauncherVerdict verdict = _probe.Launcher(launcher);
        bool? dir = StatusFile.Exists(runDir);
        return dir is true
            ? ReadStatus(runDir, id, verdict)
            : (Unreached(runDir, id, verdict, dir is false, dir is false ? "run dir removed" : "the run dir cannot be reached"), false);
    }

    /// <summary>
    /// The row of the status file in a run dir that is there; <paramref name="verdict"/> is the
    /// pointer's launcher verdict for a pointed run, null otherwise (a missing file is a legacy row then).
    /// </summary>
    private (StatusRow Row, bool Read) ReadStatus(string runDir, string id, LauncherVerdict? verdict)
    {
        string path = Path.Combine(runDir, RunStatus.FileName);
        (JsonObject? doc, string? problem, bool retry) = Load(path);
        if (doc is null && problem is null)
        {
            return verdict is { } pointed ? (Missing(runDir, id, pointed, path), false) : (Legacy(runDir, id), true);
        }

        if (doc is null && retry)
        {
            (doc, problem, _) = Load(path);
        }

        return doc is null
            ? (Unreadable(runDir, id, $"{RunStatus.FileName}: {problem ?? "is gone"}"), true)
            : (Annotated(FromStatus(runDir, id, doc, verdict ?? _probe.Launcher(doc["launcher"] as JsonObject)), doc), true);
    }

    // A pointed run whose status file was not found: gone only when it provably is not there.
    private StatusRow Missing(string runDir, string id, LauncherVerdict launcher, string path)
    {
        bool missing = StatusFile.Exists(path) is false;
        return Unreached(runDir, id, launcher, missing,
            missing ? $"run dir has no {RunStatus.FileName} any more" : $"{RunStatus.FileName} cannot be reached");
    }

    /// <summary>
    /// A pointed run whose run dir or status file was not reached: live with an unreadable-status
    /// note while its launcher is alive; gone when <paramref name="missing"/> (provably not there);
    /// else unknown, never ended.
    /// </summary>
    private StatusRow Unreached(string runDir, string id, LauncherVerdict launcher, bool missing, string detail)
    {
        if (launcher.Liveness is Liveness.Alive)
        {
            return Fileless(runDir, id, StatusStates.Unknown, Liveness.Alive, "unreadable status: " + detail);
        }

        return missing ? Gone(runDir, id, detail) : Fileless(runDir, id, StatusStates.Unknown, Liveness.Unknown, detail);
    }

    private StatusRow Unreadable(string runDir, string id, string detail) => Fileless(runDir, id, StatusStates.Unreadable, Liveness.Unknown, detail);

    private StatusRow Gone(string runDir, string id, string detail) => Fileless(runDir, id, StatusStates.Gone, Liveness.Gone, detail);

    /// <summary>
    /// A row without a status file's content, named and dated by its run id; it needs attention
    /// unless it is gone.
    /// </summary>
    private StatusRow Fileless(string runDir, string id, string state, string liveness, string detail)
    {
        return new StatusRow(runDir, StampName(id), state, liveness, Status: null, detail, Stamp(id),
            EndedAt: null, Attention: state is not StatusStates.Gone, Quiet: null);
    }

    /// <summary>
    /// The row with the owner and the parent run its status file records, each one only when it
    /// passes the key and run id checks.
    /// </summary>
    private static StatusRow Annotated(StatusRow row, JsonObject doc)
    {
        string? owner = (doc["owner"] as JsonObject)?["keys"] is JsonArray { Count: > 0 } keys && keys[0] is JsonValue v
                        && v.TryGetValue(out string? first) && OwnerKeys.Valid(first) ? first : null;
        string? parent = OwnerKeys.RunId(Json.Str(doc["run"] as JsonObject, "parent_run"));
        return row with { OwnerKey = owner, ParentRun = parent };
    }

    /// <summary>
    /// The row of a status file, by <paramref name="launcher"/>'s verdict: a pointer's launcher
    /// identity decides when the run is read through the index (the pointer is written once at
    /// launch, the status file again and again), else the status file's.
    /// </summary>
    private StatusRow FromStatus(string runDir, string id, JsonObject doc, LauncherVerdict launcher)
    {
        string name = Json.Str(doc["run"] as JsonObject, "name") ?? StampName(id);
        DateTimeOffset? started = Time(Json.Str(doc["spec"] as JsonObject, "started_at"));
        if (launcher.Liveness is Liveness.Alive)
        {
            return LiveRow(runDir, name, doc, started);
        }

        if (launcher.Liveness is Liveness.Unknown)
        {
            return new StatusRow(runDir, name, StatusStates.Unknown, Liveness.Unknown, doc,
                $"launcher state unknown: {launcher.Reason}", started, EndedAt: null, Attention: true, Quiet: null);
        }

        return EndedRow(runDir, name, doc, started, launcher.OtherBoot);
    }

    /// <summary>
    /// An earlier round of a gate chain: a round with an end and a later round in the same chain is
    /// final, even while their one launcher still runs the later round (ending only applies to the
    /// latest). Any other row is returned as it is; nothing here throws.
    /// </summary>
    public StatusRow EarlierRound(StatusRow r)
    {
        if (r.State is not StatusStates.Ending || r.Status is not { } doc || doc["end"] is not JsonObject)
        {
            return r;
        }

        try
        {
            StatusRow ended = EndedRow(r.Dir, r.Name, doc, r.StartedAt, otherBoot: false);
            return ended with
            {
                Pointer = r.Pointer,
                PointerLauncher = r.PointerLauncher,
                OwnerKey = r.OwnerKey,
                ParentRun = r.ParentRun,
                Chain = r.Chain,
                Depth = r.Depth,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
        {
            return r;
        }
    }

    /// <summary>
    /// Step 4 and 7: the phase of a live run (ending when it already wrote done or failed), and the
    /// quiet annotation for a worker that has been silent longer than <see cref="QuietAfter"/>.
    /// </summary>
    private StatusRow LiveRow(string runDir, string name, JsonObject doc, DateTimeOffset? started)
    {
        string phase = Json.Str(doc, "phase") ?? StatusStates.Unknown;
        JsonObject? live = doc["live"] as JsonObject;
        string state = phase switch
        {
            RunPhase.Done or RunPhase.Failed => StatusStates.Ending,
            RunPhase.Worker when Json.Bool(live, "wrapped_up") => StatusStates.WrapUp,
            RunPhase.Worker => StatusStates.Running,
            _ => phase,
        };
        DateTimeOffset now = Clock.GetUtcNow();
        DateTimeOffset? last = Time(Json.Str(live, "last_event_at")) ?? Time(Json.Str(doc, "updated_at"));
        TimeSpan? quiet = phase is RunPhase.Worker && now - last > QuietAfter ? now - last : null;
        string detail = StatusText.LiveDetail(phase, live, now, quiet);
        return new StatusRow(runDir, name, state, Liveness.Alive, doc, detail, started, EndedAt: null, Attention: quiet is not null, quiet);
    }

    /// <summary>
    /// Step 5 and the ended rule: the final state is the status file's end, else the summary's
    /// status, else orphaned or lost by the group check.
    /// </summary>
    private StatusRow EndedRow(string runDir, string name, JsonObject doc, DateTimeOffset? started, bool otherBoot)
    {
        if (doc["end"] is JsonObject end)
        {
            string status = Json.Str(end, "status") ?? StatusStates.Unknown;
            if (status is RunStatus.LauncherSignalled && SignalledGroup(runDir, name, doc, started, end, otherBoot) is { } held)
            {
                return held;
            }

            string state = status is RunStatus.LaunchFailed or RunStatus.LauncherSignalled ? StatusStates.Failed : status;
            return new StatusRow(runDir, name, state, Liveness.Gone, doc, StatusText.EndDetail(runDir, doc, end), started,
                Time(Json.Str(end, "at")), IsFailure(state), Quiet: null);
        }

        DateTimeOffset? updated = Time(Json.Str(doc, "updated_at"));
        (string? summary, string? summaryProblem) = SummaryStatus(runDir);
        if (summaryProblem is not null)
        {
            return Unreadable(runDir, Path.GetFileName(Path.TrimEndingDirectorySeparator(runDir)), summaryProblem);
        }

        if (summary is not null)
        {
            return new StatusRow(runDir, name, summary, Liveness.Gone, doc,
                "launcher gone before its last status write; status from summary.json", started, updated, IsFailure(summary), Quiet: null);
        }

        GroupVerdict group = _probe.Group(doc["worker"] as JsonObject, otherBoot);
        // An unknown group verdict means the run cannot be called ended: it may still have a live worker.
        string liveness = group.State is StatusStates.Unknown ? Liveness.Unknown : Liveness.Gone;
        return new StatusRow(runDir, name, group.State, liveness, doc, group.Detail, started, updated, Attention: true, Quiet: null);
    }

    /// <summary>
    /// A signalled launcher left its worker running (the handler records the signal and lets the
    /// default action end the launcher only): while the worker's group lives the run is orphaned,
    /// and while the group check cannot tell it is unknown, as for a run that ended without a word
    /// (section 5). Null when the group is gone: the run is failed (signalled).
    /// </summary>
    private StatusRow? SignalledGroup(string runDir, string name, JsonObject doc, DateTimeOffset? started, JsonObject end, bool otherBoot)
    {
        GroupVerdict group = _probe.Group(doc["worker"] as JsonObject, otherBoot);
        string signal = Json.Str(end, "signal") is { } s ? " (" + s + ")" : string.Empty;
        return group.State switch
        {
            StatusStates.Orphaned => new StatusRow(runDir, name, StatusStates.Orphaned, Liveness.Gone, doc,
                $"launcher signalled{signal}; {group.Detail}", started, Time(Json.Str(end, "at")), Attention: true, Quiet: null),
            StatusStates.Unknown => new StatusRow(runDir, name, StatusStates.Unknown, Liveness.Unknown, doc,
                $"launcher signalled{signal}; {group.Detail}", started, EndedAt: null, Attention: true, Quiet: null),
            _ => null,
        };
    }

    /// <summary>
    /// A run dir without status.json (an older launcher): its name and stamp from the dir name and
    /// the summary's status when there is one; liveness unknown, since no pid was recorded.
    /// </summary>
    private StatusRow Legacy(string runDir, string id)
    {
        (string? summary, string? problem) = SummaryStatus(runDir);
        if (problem is not null)
        {
            return Unreadable(runDir, id, problem);
        }

        string detail = summary is null ? "no status.json, no summary.json" : $"no status.json; summary: {summary}";
        return new StatusRow(runDir, StampName(id), StatusStates.Legacy, Liveness.Unknown, Status: null, detail, Stamp(id),
            EndedAt: null, Attention: false, Quiet: null)
        {
            SummaryStatus = summary,
        };
    }

    /// <summary>
    /// Whether an ended run's final state is a failure (anything but success and wrapped-up).
    /// </summary>
    internal static bool IsFailure(string state) => state is not ("success" or "wrapped-up");

    /// <summary>
    /// status.json: the object; nothing at all when the file is absent; else the problem (a fixed
    /// text) and whether it is a parse problem, which is worth one retry (a torn read).
    /// </summary>
    private static (JsonObject? Doc, string? Problem, bool Retry) Load(string path)
    {
        StatusFile.Content content = StatusFile.ReadHere(path);
        if (content.Bytes is not { } bytes)
        {
            return (null, content.Problem, false);
        }

        if (StatusFile.ParseObject(bytes, out string? problem) is not { } doc)
        {
            return (null, problem, true);
        }

        return Json.Str(doc, "schema") is RunStatus.Schema ? (doc, null, false) : (null, "unknown schema", true);
    }

    /// <summary>
    /// The status in a run dir's summary.json: null when there is none or it does not parse; a
    /// problem (a fixed text) when it is there but is no regular file the reader may read.
    /// </summary>
    private static (string? Status, string? Problem) SummaryStatus(string runDir)
    {
        StatusFile.Content content = StatusFile.ReadHere(Path.Combine(runDir, "summary.json"));
        if (content.Bytes is not { } bytes)
        {
            return (null, content.Problem is { } problem ? "summary.json: " + problem : null);
        }

        return (StatusFile.ParseObject(bytes, out _) is { } s ? Json.Str(s, "status") : null, null);
    }

    /// <summary>
    /// A time from a status file (UTC with Z); null when absent or not a time.
    /// </summary>
    internal static DateTimeOffset? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t) ? t : null;

    /// <summary>
    /// The start stamp of a run dir name (yyyyMMdd-HHmmss, the launcher's local time); null when
    /// the name does not start with one.
    /// </summary>
    private DateTimeOffset? Stamp(string id)
    {
        if (id.Length < StampLength
            || !DateTime.TryParseExact(id[..StampLength], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(local, Clock.LocalTimeZone.GetUtcOffset(local));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run name in a run dir name: what follows the stamp and its dash, else the whole name.
    /// </summary>
    internal static string StampName(string id) =>
        id.Length > StampLength + 1 && id[StampLength] is '-' ? id[(StampLength + 1)..] : id;
}
