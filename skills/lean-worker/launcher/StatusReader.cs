// The status reader: one run dir into one row (docs/research/live-status/README.md, section 4.1,
// "What a reader does" and the ended rule). It never writes or deletes anything. The /proc root,
// the clock (with its local time zone, for the stamps in legacy dir names) and the current boot id
// are its inputs, so a test drives it without a real /proc.

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
    /// The reader of this machine: the real /proc, the system clock and the current boot id.
    /// </summary>
    public static StatusReader Host() => new(ProcFs.Host, TimeProvider.System, ProcFs.Host.BootId());

    /// <summary>
    /// The detail of a run whose files gave the reader something it could not use.
    /// </summary>
    internal const string UnusableDetail = "the run's files hold content the reader cannot use";

    /// <summary>
    /// One run dir: a legacy row without status.json, an unreadable row when the run dir or one of
    /// its files is not a plain directory or regular file, when status.json does not parse (after
    /// one retry), or when anything about the run fails while it is read; else the state the ended
    /// rule gives. Nothing about one run dir throws out of here.
    /// </summary>
    public StatusRow Read(string runDir)
    {
        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(runDir));
        try
        {
            return ReadChecked(runDir, id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
        {
            // Whatever the run's files hold: the row says so with a fixed text, never the exception's.
            return Unreadable(runDir, id, UnusableDetail);
        }
    }

    private StatusRow ReadChecked(string runDir, string id)
    {
        if (StatusFile.IsLink(runDir))
        {
            return Unreadable(runDir, id, "the run dir is a symbolic link; not followed");
        }

        string path = Path.Combine(runDir, RunStatus.FileName);
        (JsonObject? doc, string? problem, bool retry) = Load(path);
        if (doc is null && problem is null)
        {
            return Legacy(runDir, id);
        }

        if (doc is null && retry)
        {
            (doc, problem, _) = Load(path);
        }

        return doc is null
            ? Unreadable(runDir, id, $"{RunStatus.FileName}: {problem ?? "is gone"}")
            : FromStatus(runDir, id, doc);
    }

    private StatusRow Unreadable(string runDir, string id, string detail)
    {
        return new StatusRow(runDir, StampName(id), StatusStates.Unreadable, Liveness.Unknown, Status: null, detail, Stamp(id),
            EndedAt: null, Attention: true, Quiet: null);
    }

    private StatusRow FromStatus(string runDir, string id, JsonObject doc)
    {
        LauncherVerdict launcher = _probe.Launcher(doc["launcher"] as JsonObject);
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
            string state = status is RunStatus.LaunchFailed or "launcher-signalled" ? StatusStates.Failed : status;
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
        StatusFile.Content content = StatusFile.Read(path);
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
        StatusFile.Content content = StatusFile.Read(Path.Combine(runDir, "summary.json"));
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
