using System.Text.Json.Nodes;

namespace LeanWorker.Tests;

/// <summary>
/// Hand-made status.json documents and run dirs for the reader tests, so they do not depend on the
/// writer: the fields are the ones section 4.1 of the design lists.
/// </summary>
internal static class StatusDocs
{
    public const int LauncherPid = 100;
    public const long LauncherTicks = 5000;
    public const int WorkerPid = 300;
    public const long WorkerTicks = 6000;

    /// <summary>
    /// The instant the reader tests take as "now".
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static string Stamp(DateTimeOffset t) => RunStatus.Stamp(t);

    public static JsonObject Worker(int pid = WorkerPid, int? pgid = WorkerPid, int? sid = WorkerPid, long? ticks = WorkerTicks)
    {
        return new JsonObject
        {
            ["pid"] = pid,
            ["pgid"] = pgid,
            ["sid"] = sid,
            ["start_ticks"] = ticks,
        };
    }

    /// <summary>
    /// A status document as the launcher would have written it, by default for a run in its worker
    /// phase whose launcher is process 100 (start ticks 5000) of boot "boot-A".
    /// </summary>
    public static JsonObject Make(string name, string phase = "worker", string boot = FakeProcRoot.DefaultBoot, int launcherPid = LauncherPid,
        long launcherTicks = LauncherTicks, JsonObject? worker = null, JsonObject? end = null, DateTimeOffset? updatedAt = null,
        DateTimeOffset? lastEventAt = null, DateTimeOffset? startedAt = null, bool wrappedUp = false, int? gateRound = null, JsonObject? lastTool = null)
    {
        DateTimeOffset updated = updatedAt ?? Now;
        DateTimeOffset started = startedAt ?? Now.AddMinutes(-5);
        return new JsonObject
        {
            ["schema"] = "lean-worker.status/1",
            ["seq"] = 7,
            ["updated_at"] = Stamp(updated),
            ["run"] = new JsonObject { ["id"] = "x", ["dir"] = "/x", ["name"] = name, ["chain_id"] = "x", ["round"] = 1, ["parent_run"] = null },
            ["launcher"] = new JsonObject { ["pid"] = launcherPid, ["start_ticks"] = launcherTicks, ["boot_id"] = boot },
            ["worker"] = worker,
            ["owner"] = null,
            ["spec"] = new JsonObject
            {
                ["profile"] = "code-minimax",
                ["runtime"] = "opencode",
                ["provider"] = "minimax-coding-plan",
                ["model"] = "MiniMax-M3",
                ["budget_usd"] = 3.0m,
                ["wrap_up_usd"] = 2.4m,
                ["timeout_minutes"] = 60,
                ["started_at"] = Stamp(started),
                ["deadline_at"] = Stamp(started.AddMinutes(60)),
                ["worktree"] = null,
            },
            ["phase"] = phase,
            ["live"] = new JsonObject
            {
                ["api_calls"] = 12,
                ["turns"] = null,
                ["tokens"] = new JsonObject { ["input"] = 3100, ["cache_write"] = 0, ["cache_read"] = 399000, ["output"] = 9870, ["thinking"] = 3200 },
                ["cost_usd"] = 0.4123m,
                ["wrapped_up"] = wrappedUp,
                ["elapsed_ms"] = 221_000,
                ["last_event_at"] = Stamp(lastEventAt ?? updated),
                ["gate_round"] = gateRound,
                ["last_tool"] = lastTool,
            },
            ["end"] = end,
        };
    }

    public static JsonObject End(string status, DateTimeOffset? at = null)
    {
        return new JsonObject
        {
            ["status"] = status,
            ["exit_code"] = status is "success" ? 0 : 1,
            ["at"] = Stamp(at ?? Now.AddMinutes(-1)),
            ["summary"] = "summary.json",
        };
    }

    public static JsonObject Tool(string name, string state, DateTimeOffset at)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["at"] = Stamp(at),
            ["state"] = state,
        };
    }

    /// <summary>
    /// Creates &lt;runsRoot&gt;/runs/&lt;id&gt; with the document (when given) and a summary.json with the status (when given).
    /// </summary>
    public static string WriteRun(string runsRoot, string id, JsonObject? doc, string? summaryStatus = null)
    {
        string dir = Path.Combine(runsRoot, "runs", id);
        _ = Directory.CreateDirectory(dir);
        if (doc is not null)
        {
            File.WriteAllText(Path.Combine(dir, "status.json"), doc.ToJsonString());
        }

        if (summaryStatus is not null)
        {
            File.WriteAllText(Path.Combine(dir, "summary.json"), new JsonObject { ["status"] = summaryStatus }.ToJsonString());
        }

        return dir;
    }
}
