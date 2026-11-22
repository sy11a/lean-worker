using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A runs root and a per-user index folder for the index scopes of `status`: runs are hand-made
/// (status.json in a run dir plus a pointer in the index), so the reader tests do not depend on the writer.
/// Launcher 100 (start ticks 5000) is alive in the fake /proc; launcher 900 is not there. The clock is fixed.
/// </summary>
internal sealed class StatusIndexRig : IDisposable
{
    /// <summary>
    /// The longest a call of the command may take in a test.
    /// </summary>
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(30);

    private readonly TempDirs _dirs = new();
    private int _next = 1000;

    public StatusIndexRig()
    {
        Root = _dirs.Create("lw-idx-root");
        State = _dirs.Create("lw-idx-state");
        Folder = Path.Combine(State, "runs");
        Proc = new FakeProcRoot();
        Clock = new FakeClock(StatusDocs.Now);
        Proc.Add(StatusDocs.LauncherPid, StatusDocs.LauncherTicks);
    }

    /// <summary>
    /// One hand-made run: its run dir, its pointer file name in the index and both documents.
    /// </summary>
    internal sealed record Added(string Dir, string Pointer, JsonObject Status, JsonObject PointerDoc)
    {
        public string Id => Path.GetFileName(Dir);
    }

    public string Root { get; }

    /// <summary>
    /// What LEAN_WORKER_STATE_DIR would be: the index is <c>State/runs</c>.
    /// </summary>
    public string State { get; }

    public string Folder { get; }

    public FakeProcRoot Proc { get; }

    public FakeClock Clock { get; }

    public TimeSpan ScanBudget { get; set; } = ReadBudget.DefaultTotal;

    public TimeSpan ReadFloor { get; set; } = ReadBudget.DefaultFloor;

    public static string Id(DateTimeOffset at, string name) => at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + name;

    /// <summary>
    /// An environment lookup over the given variables (the rest are unset).
    /// </summary>
    public static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    /// <summary>
    /// The environment of a Claude Code session with the given id.
    /// </summary>
    public static Func<string, string?> Session(string id, params (string Name, string Value)[] more) =>
        Env([("CLAUDE_CODE_SESSION_ID", id), .. more]);

    /// <summary>
    /// A run in the runs root with its pointer in the index. By default it is live (launcher 100 is alive), started
    /// five minutes ago, owned by <paramref name="keys"/>; with <paramref name="endedAgo"/> its launcher is gone
    /// (900) and its status file ends with <paramref name="status"/>. <paramref name="tweak"/> may change the
    /// status document before it is written.
    /// </summary>
    public Added Add(string name, string[]? keys = null, string? parent = null, TimeSpan? endedAgo = null, TimeSpan? startedAgo = null,
        string status = "success", string? chain = null, int round = 1, string? id = null, Action<JsonObject>? tweak = null)
    {
        DateTimeOffset started = StatusDocs.Now - (startedAgo ?? TimeSpan.FromMinutes(5));
        string runId = id ?? Id(started, name);
        bool ended = endedAgo is not null;
        JsonObject doc = StatusDocs.Make(name, phase: ended ? "done" : "worker", launcherPid: ended ? 900 : StatusDocs.LauncherPid,
            worker: StatusDocs.Worker(), end: ended ? StatusDocs.End(status, StatusDocs.Now - endedAgo!.Value) : null, startedAt: started);
        string dir = Path.Combine(Root, "runs", runId);
        JsonObject run = doc["run"]!.AsObject();
        run["id"] = runId;
        run["dir"] = dir;
        run["chain_id"] = chain ?? runId;
        run["round"] = round;
        run["parent_run"] = parent;
        doc["owner"] = new JsonObject
        {
            ["keys"] = new JsonArray([.. (keys ?? []).Select(k => (JsonNode?)k)]),
            ["source"] = "env",
            ["runtime"] = "claude",
            ["relay_name"] = null,
        };
        tweak?.Invoke(doc);
        _ = StatusDocs.WriteRun(Root, runId, doc);
        JsonObject pointer = Pointer(dir, chain ?? runId, parent, keys ?? [], doc["launcher"]!.DeepClone().AsObject(), started);
        string file = NextName(started, round);
        _ = WritePointer(file, pointer.ToJsonString());
        return new Added(dir, file, doc, pointer);
    }

    /// <summary>
    /// A pointer document as the launcher writes it.
    /// </summary>
    public static JsonObject Pointer(string runDir, string chainId, string? parent, string[] keys, JsonObject launcher, DateTimeOffset created)
    {
        return new JsonObject
        {
            ["schema"] = "lean-worker.pointer/1",
            ["created_at"] = StatusDocs.Stamp(created),
            ["run_dir"] = runDir,
            ["chain_id"] = chainId,
            ["parent_run"] = parent,
            ["owner_keys"] = new JsonArray([.. keys.Select(k => (JsonNode?)k)]),
            ["launcher"] = launcher,
        };
    }

    /// <summary>
    /// A pointer file name of the right form that no other call of this rig gives.
    /// </summary>
    public string NextName(DateTimeOffset started, int round = 1) =>
        string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMdd-HHmmss}-{_next++}-r{round}.json");

    /// <summary>
    /// Writes <paramref name="text"/> as the file <paramref name="name"/> of the index folder.
    /// </summary>
    public string WritePointer(string name, string text)
    {
        _ = Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// The pointer names in the index folder, sorted.
    /// </summary>
    public string[] PointerNames() =>
        Directory.Exists(Folder) ? [.. Directory.GetFileSystemEntries(Folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)] : [];

    /// <summary>
    /// A fresh directory of this rig that is neither the runs root nor the index.
    /// </summary>
    public string Outside() => _dirs.Create("lw-idx-outside");

    /// <summary>
    /// One call of the status command over this rig's /proc, clock and index, with <paramref name="env"/> as the
    /// caller's environment, under a hard time limit.
    /// </summary>
    public async Task<(int Code, string Output)> CallAsync(Func<string, string?> env, params string[] args)
    {
        StatusReader reader = new(Proc.Fs, Clock, FakeProcRoot.DefaultBoot) { ScanBudget = ScanBudget, ReadFloor = ReadFloor };
        Task<(int Code, string Output)> call = Task.Run(async () =>
        {
            await using StringWriter output = new();
            int code = await StatusCommand.RunAsync(args, output, reader, Root, new StatusEnvironment(env, Folder));
            return (code, output.ToString());
        }, TestContext.Current.CancellationToken);
        (int Code, string Output) result = await call.WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);
        return result;
    }

    /// <summary>
    /// One call of the status command from a caller that has no identity (no variable set).
    /// </summary>
    public async Task<(int Code, string Output)> CallAsync(params string[] args)
    {
        (int code, string output) = await CallAsync(NoCaller, args);
        return (code, output);
    }

    /// <summary>
    /// The environment of a caller that has no variable set.
    /// </summary>
    public static Func<string, string?> NoCaller { get; } = _ => null;

    /// <summary>
    /// The listing of --all as plain text.
    /// </summary>
    public async Task<(int Code, string Output)> AllAsync(params string[] more)
    {
        (int code, string output) = await CallAsync(NoCaller, ["--all", .. more]);
        return (code, output);
    }

    /// <summary>
    /// The lines of a plain output without its header.
    /// </summary>
    public static string[] Rows(string plain) => [.. Lines(plain).Skip(1)];

    public static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The names of the runs of a --json listing, in order (the name its status file gives, else the row's own).
    /// </summary>
    public static string[] JsonNames(string json) =>
        [.. JsonNode.Parse(json)!["runs"]!.AsArray().Select(r => (r!["run"]?["name"] ?? r["name"])!.GetValue<string>())];

    /// <summary>
    /// The --json rows.
    /// </summary>
    public static List<JsonObject> JsonRows(string json) => [.. JsonNode.Parse(json)!["runs"]!.AsArray().Select(r => r!.AsObject())];

    /// <summary>
    /// Every file and directory under the roots with its size, write time and content, sorted: two snapshots are
    /// equal when nothing under the roots was created, changed or deleted.
    /// </summary>
    public static string[] Snapshot(params string[] roots)
    {
        List<string> entries = [];
        foreach (string root in roots.Where(r => Directory.Exists(r)))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                entries.Add(Directory.Exists(path) ? "D " + path : string.Create(CultureInfo.InvariantCulture,
                    $"F {path} {new FileInfo(path).Length} {File.GetLastWriteTimeUtc(path).Ticks} {File.ReadAllText(path)}"));
            }
        }

        return [.. entries.Order(StringComparer.Ordinal)];
    }

    public void Dispose()
    {
        Proc.Dispose();
        _dirs.Dispose();
    }
}
