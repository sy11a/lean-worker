using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A runs root with one healthy live run (name "healthy", launcher 120 alive) and room for one hostile
/// run dir ("hostile"), so a test can write something wrong into it and list both with the status
/// command, in plain and --json mode, under a hard time limit.
/// </summary>
internal sealed class StatusHostileRig : IDisposable
{
    /// <summary>
    /// Text a hostile file carries that must never reach an output.
    /// </summary>
    public const string Marker = "SECRET-MARKER";

    public const string HostileId = "20261122-115000-hostile";

    public const string HealthyId = "20261122-115500-healthy";

    public const int LauncherPid = 120;

    private static readonly TimeSpan _defaultLimit = TimeSpan.FromSeconds(30);

    private readonly TempDirs _dirs = new();

    public StatusHostileRig()
    {
        Root = _dirs.Create("lw-hostile");
        Proc = new FakeProcRoot();
        Clock = new FakeClock(StatusDocs.Now);
        Proc.Add(LauncherPid, StatusDocs.LauncherTicks);
        _ = StatusDocs.WriteRun(Root, HealthyId, Healthy());
    }

    /// <summary>
    /// What a listing printed: the exit code and output of the plain and the --json call and what
    /// both wrote to the error stream.
    /// </summary>
    internal sealed record Listing(int PlainCode, string Plain, int JsonCode, string Json, string Error);

    public string Root { get; }

    public FakeProcRoot Proc { get; }

    public FakeClock Clock { get; }

    public string RunsDir => Path.Combine(Root, "runs");

    public static JsonObject Healthy() => StatusDocs.Make("healthy", launcherPid: LauncherPid, worker: StatusDocs.Worker());

    /// <summary>
    /// The status document of the hostile run: a live run whose launcher is process 120, like the healthy one.
    /// </summary>
    public static JsonObject Hostile(string name = "hostile") => StatusDocs.Make(name, launcherPid: LauncherPid, worker: StatusDocs.Worker());

    /// <summary>
    /// A directory of this rig's own that is not the runs root (for link targets and files to be linked).
    /// </summary>
    public string Outside() => _dirs.Create("lw-outside");

    /// <summary>
    /// The hostile run dir, created (empty) under the runs folder.
    /// </summary>
    public string HostileDir()
    {
        string dir = Path.Combine(RunsDir, HostileId);
        _ = Directory.CreateDirectory(dir);
        return dir;
    }

    public string HostilePath(string file) => Path.Combine(HostileDir(), file);

    /// <summary>
    /// The listing of the runs root in both modes, each call under <paramref name="limit"/> (the
    /// call is made on a pool thread, so a hang in it fails the test instead of the run).
    /// </summary>
    public async Task<Listing> ListAsync(TimeSpan? limit = null)
    {
        TimeSpan bound = limit ?? _defaultLimit;
        (int plainCode, string plain, string plainErr) = await CallAsync(bound, "--here", "--runs-root", Root);
        (int jsonCode, string json, string jsonErr) = await CallAsync(bound, "--here", "--runs-root", Root, "--json");
        return new Listing(plainCode, plain, jsonCode, json, plainErr + jsonErr);
    }

    /// <summary>
    /// One call of the status command with the output and the error stream captured.
    /// </summary>
    public async Task<(int Code, string Output, string Error)> CallAsync(TimeSpan limit, params string[] args)
    {
        TextWriter oldErr = Console.Error;
        await using StringWriter err = new();
        Console.SetError(err);
        try
        {
            StatusReader reader = new(Proc.Fs, Clock, FakeProcRoot.DefaultBoot);
            string cwd = Root;
            Task<(int Code, string Output)> call = Task.Run(async () =>
            {
                await using StringWriter output = new();
                int code = await StatusCommand.RunAsync(args, output, reader, cwd);
                return (code, output.ToString());
            }, TestContext.Current.CancellationToken);
            try
            {
                (int code, string output) = await call.WaitAsync(limit, TimeProvider.System, TestContext.Current.CancellationToken);
                return (code, output, err.ToString());
            }
            catch (TimeoutException)
            {
                Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"the status command did not return within {limit.TotalSeconds} s: ") + string.Join(' ', args));
                throw;
            }
        }
        finally
        {
            Console.SetError(oldErr);
        }
    }

    /// <summary>
    /// The rows of a --json listing.
    /// </summary>
    public static List<JsonObject> Rows(string json) => [.. JsonNode.Parse(json)!["runs"]!.AsArray().Select(r => r!.AsObject())];

    /// <summary>
    /// The row of the healthy run in a --json listing (by the name in its status document).
    /// </summary>
    public static JsonObject HealthyRow(string json) =>
        Rows(json).Single(r => IsHealthy(r));

    private static bool IsHealthy(JsonObject row) => (row["run"] as JsonObject)?["name"]?.GetValue<string>() is "healthy";

    /// <summary>
    /// The one row that is not the healthy run's.
    /// </summary>
    public static JsonObject OtherRow(string json) =>
        Assert.Single(Rows(json), r => !IsHealthy(r));

    /// <summary>
    /// The common claims of a listing next to a healthy run: both calls exit 0 and print nothing
    /// unclean, the command did not fail the launch or write an error, the healthy run is listed as
    /// running in both modes, no text in <paramref name="absent"/> is in any output, and the listing has
    /// <paramref name="otherRows"/> more rows. Returns the rows of the --json listing but the healthy one.
    /// </summary>
    public static List<JsonObject> AssertHealthyStillListed(Listing l, int otherRows = 1, params string[] absent)
    {
        Assert.Equal(0, l.PlainCode);
        Assert.Equal(0, l.JsonCode);
        Assert.DoesNotContain("LEAN-WORKER LAUNCH FAILED", l.Plain + l.Json + l.Error, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', l.Error);
        Assert.Empty(TerminalAssert.PlainProblems(l.Error.Replace("\r", string.Empty, StringComparison.Ordinal)));
        string[] lines = TerminalAssert.AssertPlain(l.Plain, otherRows + 1);
        Assert.Contains(lines, line => line.StartsWith("running", StringComparison.Ordinal) && line.Contains("healthy", StringComparison.Ordinal));
        Assert.Empty(TerminalAssert.JsonProblems(l.Json));
        List<JsonObject> rows = Rows(l.Json);
        Assert.Equal(otherRows + 1, rows.Count);
        JsonObject healthy = HealthyRow(l.Json);
        Assert.Equal("running", healthy["state"]!.GetValue<string>());
        Assert.Equal("alive", healthy["liveness"]!.GetValue<string>());
        foreach (string text in absent.Append(Marker))
        {
            Assert.DoesNotContain(text, l.Plain, StringComparison.Ordinal);
            Assert.DoesNotContain(text, l.Json, StringComparison.Ordinal);
            Assert.DoesNotContain(text, l.Error, StringComparison.Ordinal);
        }

        return [.. rows.Where(r => !IsHealthy(r))];
    }

    /// <summary>
    /// <see cref="AssertHealthyStillListed"/> and the one other row is an unreadable one whose reason
    /// is a fixed text: the file's content and no exception text are in it.
    /// </summary>
    public static JsonObject AssertOnlyHostileUnreadable(Listing l, params string[] absent)
    {
        JsonObject row = Assert.Single(AssertHealthyStillListed(l, otherRows: 1, absent));
        Assert.Equal("unreadable", row["state"]!.GetValue<string>());
        string detail = row["detail"]!.GetValue<string>();
        Assert.DoesNotContain("Exception", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", detail, StringComparison.Ordinal);
        Assert.Contains("unreadable", l.Plain, StringComparison.Ordinal);
        return row;
    }

    public void Dispose()
    {
        Proc.Dispose();
        _dirs.Dispose();
    }
}
