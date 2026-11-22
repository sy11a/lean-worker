// The liveness proofs of the status reader (docs/research/live-status/README.md, section 4.1,
// "What a reader does", steps 3 to 6): whether the launcher recorded in a status file is provably
// alive, provably gone or not to be told, and, for a run that ended without a word, whether its
// worker's group is still there (orphaned) or not (lost).

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// The /proc checks of the reader, against the boot id it was given.
/// </summary>
internal sealed class StatusProbe(ProcFs proc, string? bootId)
{
    /// <summary>
    /// Steps 3 to 6: the launcher is alive when the boot id is the current one, its pid's stat line
    /// exists, its start ticks are the recorded ones and it is not a zombie; it is gone on a clear
    /// mismatch (another boot, no such pid, other start ticks, exited); anything else is unknown.
    /// </summary>
    public LauncherVerdict Launcher(JsonObject? launcher)
    {
        long pid = Json.Num(launcher?["pid"]);
        long? ticks = NumOrNull(launcher?["start_ticks"]);
        string? recordedBoot = Json.Str(launcher, "boot_id");
        if (bootId is null)
        {
            return Unknown("the current boot id cannot be read");
        }

        if (recordedBoot is null || ticks is null || pid <= 0 || pid > int.MaxValue)
        {
            return Unknown("no launcher identity recorded");
        }

        if (!string.Equals(recordedBoot, bootId, StringComparison.Ordinal))
        {
            return new LauncherVerdict(LeanWorker.Liveness.Gone, OtherBoot: true, Reason: null);
        }

        ProcProbe probe = proc.Stat((int)pid);
        return probe.Kind switch
        {
            ProbeKind.Failed => Unknown(probe.Error ?? "the launcher's stat line cannot be read"),
            ProbeKind.Present when probe.Stat.StartTicks == ticks && !probe.Stat.Exited =>
                new LauncherVerdict(LeanWorker.Liveness.Alive, OtherBoot: false, Reason: null),
            _ => new LauncherVerdict(LeanWorker.Liveness.Gone, OtherBoot: false, Reason: null),
        };
    }

    /// <summary>
    /// Step 5 for a run that ended with neither an end record nor a summary: another boot is lost
    /// with no scan; a group leader with the recorded worker start ticks is orphaned; a leader pid
    /// with other start ticks is a recycled number, lost; a gone leader with members left in the
    /// recorded group and session is orphaned; otherwise lost. A probe that fails is unknown.
    /// </summary>
    public GroupVerdict Group(JsonObject? worker, bool otherBoot)
    {
        if (otherBoot)
        {
            return new GroupVerdict(StatusStates.Lost, "launcher gone (another boot)");
        }

        long? pgid = NumOrNull(worker?["pgid"]);
        long? sid = NumOrNull(worker?["sid"]);
        long? ticks = NumOrNull(worker?["start_ticks"]);
        if (worker is null)
        {
            return new GroupVerdict(StatusStates.Lost, "launcher gone before the worker started");
        }

        if (pgid is not { } group || group <= 1 || group > int.MaxValue)
        {
            return new GroupVerdict(StatusStates.Lost, "launcher gone; the worker had no process group of its own");
        }

        if (ticks is null)
        {
            return new GroupVerdict(StatusStates.Unknown, "launcher gone; the worker's start time was not recorded");
        }

        return Leader((int)group, ticks.Value, sid);
    }

    private GroupVerdict Leader(int pgid, long ticks, long? sid)
    {
        string id = pgid.ToString(CultureInfo.InvariantCulture);
        ProcProbe leader = proc.Stat(pgid);
        if (leader.Kind is ProbeKind.Failed)
        {
            return new GroupVerdict(StatusStates.Unknown, leader.Error ?? "the worker's stat line cannot be read");
        }

        if (leader.Kind is ProbeKind.Present && leader.Stat.StartTicks != ticks)
        {
            return new GroupVerdict(StatusStates.Lost, $"launcher gone; pid {id} now belongs to another process");
        }

        if (leader.Kind is ProbeKind.Present && !leader.Stat.Exited)
        {
            string started = ticks.ToString(CultureInfo.InvariantCulture);
            return new GroupVerdict(StatusStates.Orphaned, $"launcher gone, worker group {id} (start ticks {started}) alive");
        }

        return Members(pgid, sid);
    }

    private GroupVerdict Members(int pgid, long? sid)
    {
        string id = pgid.ToString(CultureInfo.InvariantCulture);
        if (sid is not { } session || session > int.MaxValue || session < 0)
        {
            return new GroupVerdict(StatusStates.Lost, "launcher and worker gone");
        }

        return proc.HasGroupMember(pgid, (int)session) switch
        {
            true => new GroupVerdict(StatusStates.Orphaned, $"launcher and worker gone, processes of group {id} (session {session.ToString(CultureInfo.InvariantCulture)}) alive"),
            false => new GroupVerdict(StatusStates.Lost, "launcher and worker gone"),
            null => new GroupVerdict(StatusStates.Unknown, "launcher gone; the process list cannot be read"),
        };
    }

    private static LauncherVerdict Unknown(string reason) => new(LeanWorker.Liveness.Unknown, OtherBoot: false, reason);

    /// <summary>
    /// A whole number from a status file, or null when the field is absent, null or not a number.
    /// </summary>
    internal static long? NumOrNull(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue(out long l) || TryInt(v, out l)) ? l : null;

    private static bool TryInt(JsonValue v, out long value)
    {
        bool ok = v.TryGetValue(out int i);
        value = i;
        return ok;
    }
}
