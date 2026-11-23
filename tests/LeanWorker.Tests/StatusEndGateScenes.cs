using System.Text.Json.Nodes;

namespace LeanWorker.Tests;

/// <summary>
/// The pieces the tests of <c>end.gate</c> share (the follow-up review of #147): the first round of a gate chain on a
/// StatusLiveRig, rows that are or are not a later round of it, and a row of the same chain id in a runs folder of its
/// own. A round whose gate went on records <c>"continue"</c> with its end (<see cref="Continue"/>).
/// </summary>
internal static class StatusEndGateScenes
{
    /// <summary>
    /// The JSON text of the <c>end.gate</c> a round whose gate went on writes.
    /// </summary>
    public const string Continue = "\"continue\"";

    public const string FirstId = "20261122-115000-gate-g1";

    public const string LaterId = "20261122-115300-gate-g1";

    /// <summary>
    /// The end of the last line of a wait that exits with <paramref name="code"/>.
    /// </summary>
    public static string Exit(int code) => "(exit " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// Round 1 of the chain, its launcher alive; its name is "g-first".
    /// </summary>
    public static StatusLiveRig.Live First(StatusLiveRig rig) =>
        rig.Add("g-first", startedAgo: TimeSpan.FromMinutes(10), chain: FirstId, id: FirstId);

    /// <summary>
    /// A second row, named "g-later", that names the first round's chain as <paramref name="kind"/> says: "launcher:N" runs
    /// under the first round's launcher (its own chain id), "chain:N" has the first round's chain id and a launcher of its
    /// own; N is the round it records, "none" records no round at all.
    /// </summary>
    public static StatusLiveRig.Live Later(StatusLiveRig rig, StatusLiveRig.Live first, string kind)
    {
        string[] parts = kind.Split(':');
        bool byLauncher = parts[0] is "launcher";
        int round = parts[1] is "none" ? 2 : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        Action<JsonObject>? tweak = parts[1] is "none" ? d => _ = d["run"]!.AsObject().Remove("round") : null;
        return rig.Add("g-later", startedAgo: TimeSpan.FromMinutes(7), chain: byLauncher ? null : FirstId, round: round, id: LaterId,
            launcher: byLauncher ? first.Pid : null, tweak: tweak);
    }

    /// <summary>
    /// A finished run (its launcher is not there) with the chain id <paramref name="chainId"/> and round 2, in the runs
    /// folder under <paramref name="runsRoot"/>, listed in the index for the rig's owner key.
    /// </summary>
    public static string Ended(StatusLiveRig rig, string runsRoot, string name, string chainId, string status = "error")
    {
        DateTimeOffset started = StatusDocs.Now - TimeSpan.FromMinutes(7);
        string id = StatusIndexRig.Id(started, name);
        string dir = Path.Combine(runsRoot, "runs", id);
        JsonObject doc = StatusDocs.Make(name, phase: "done", launcherPid: 900, worker: StatusDocs.Worker(),
            end: StatusDocs.End(status, StatusDocs.Now - TimeSpan.FromMinutes(1)), startedAt: started);
        JsonObject run = doc["run"]!.AsObject();
        run["id"] = id;
        run["dir"] = dir;
        run["chain_id"] = chainId;
        run["round"] = 2;
        run["parent_run"] = null;
        doc["owner"] = new JsonObject
        {
            ["keys"] = new JsonArray(StatusLiveRig.Key),
            ["source"] = "env",
            ["runtime"] = "claude",
            ["relay_name"] = null,
        };
        _ = StatusDocs.WriteRun(runsRoot, id, doc);
        JsonObject pointer = StatusIndexRig.Pointer(dir, chainId, parent: null, [StatusLiveRig.Key], doc["launcher"]!.DeepClone().AsObject(), started);
        _ = rig.Idx.WritePointer(rig.Idx.NextName(started, round: 2), pointer.ToJsonString());
        return dir;
    }
}
