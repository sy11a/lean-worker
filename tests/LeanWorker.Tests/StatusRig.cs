using System.Text.Json.Nodes;

namespace LeanWorker.Tests;

/// <summary>
/// A writer wired to a runtime, a Meter and an Outcome the way a launch wires them, so a test feeds
/// it stream lines and reads status.json back. Prices: input $1000, output $2000 and cache read
/// $100 per million tokens, so a token costs a tenth of a cent, two tenths, a hundredth.
/// </summary>
internal sealed class StatusRig : IDisposable
{
    private const string BookJson = """
        { "rates": {"USD": 1}, "models": { "anthropic/m": { "input": 1000, "output": 2000, "cacheRead": 100 } } }
        """;

    private readonly TempDirs _dirs = new();

    public StatusRig(IRuntime runtime, decimal budget = 100m, decimal? wrapUpAt = null)
    {
        Runtime = runtime;
        RunDir = _dirs.Create("lw-rig");
        Clock = new FakeClock(StatusDocs.Now);
        Proc = new FakeProcRoot();
        Proc.Add(4000, 7432110);
        Meter = new Meter(PriceBook.FromJson(JsonNode.Parse(BookJson)!.AsObject()), "anthropic", _dirs.Create("lw-rig-meter"), budget, wrapUpAt, "HANDOFF");
        Outcome = new Outcome();
        Status = new RunStatus(new RunStatusSpec(RunDir, "rig", "rig", 1, Profile: null, "opencode", "anthropic", "m", budget, WrapUpUsd: null, 60,
            StatusDocs.Now, Worktree: null), Proc.Fs, Clock, 4000);
        Status.Track(Meter, Outcome, runtime);
        Status.Phase(RunPhase.Worker);
    }

    public IRuntime Runtime { get; }

    public string RunDir { get; }

    public FakeClock Clock { get; }

    public FakeProcRoot Proc { get; }

    public Meter Meter { get; }

    public Outcome Outcome { get; }

    public RunStatus Status { get; }

    /// <summary>
    /// One stream line, as the launcher handles it: the runtime parses it, the Meter prices its
    /// usage, then the writer sees it.
    /// </summary>
    public void Feed(string line)
    {
        JsonObject obj = JsonNode.Parse(line)!.AsObject();
        Usage? usage = Runtime.Parse(obj, Outcome);
        if (usage is null)
        {
            Status.OnLine(Runtime, obj, usage: false);
            return;
        }

        _ = Meter.Add(usage.Model.Length > 0 ? usage : usage with { Model = "m" });
        Status.OnLine(Runtime, obj, usage: true);
    }

    public JsonObject Disk() => StatusFileAssert.Read(RunDir);

    public JsonObject Live() => Disk()["live"]!.AsObject();

    public void Dispose()
    {
        Proc.Dispose();
        _dirs.Dispose();
    }
}
