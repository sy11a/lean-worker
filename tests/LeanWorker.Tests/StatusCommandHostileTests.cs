using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status` over files and directory names an attacker wrote (the worker can write its run dir): no
/// control character reaches the plain output, one run is one line, and --json stays valid JSON with
/// every hostile character escaped and the original values read back. The usage-error messages that
/// echo an argument are held to the same rule.
/// </summary>
public sealed class StatusCommandHostileTests : IDisposable
{
    private const int HostileRuns = 6;

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly FakeClock _clock = new(StatusDocs.Now);
    private readonly string _root;

    public StatusCommandHostileTests() => _root = _dirs.Create("lw-status-hostile");

    /// <summary>
    /// An escape sequence and a title, a CR, an LF and a forged row, a bidi override, then the C1,
    /// line-separator, DEL, tab and NUL characters.
    /// </summary>
    public static TheoryData<string> Payloads
    {
        get
        {
            return
            [
                "\u001b[2J\u001b]0;title\u0007",
                "\r",
                "\nrunning  forged-run  oc  M  1 calls  $0.00/$1.00  1s  -",
                "\U0000202Eevil\U00002066x\U0000200F",
                "\u0085\u009b[31m\U00002028\U00002029\u007f\t\0",
            ];
        }
    }

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private static DateTimeOffset Ago(TimeSpan by) => StatusDocs.Now - by;

    private async Task<(int Code, string Output)> RunAsync(string cwd, params string[] args)
    {
        await using StringWriter output = new();
        int code = await StatusCommand.RunAsync(args, output, new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot), cwd);
        return (code, output.ToString());
    }

    private async Task<(int Code, string Output)> HereAsync(params string[] args)
    {
        string[] all = ["--here", "--runs-root", _root, .. args];
        (int code, string output) = await RunAsync(_root, all);
        return (code, output);
    }

    private string Write(string id, JsonObject? doc, string? summary = null) => StatusDocs.WriteRun(_root, id, doc, summary);

    /// <summary>
    /// Six runs, each with the payload in the fields the plain row prints: a live run (name, model,
    /// runtime, last tool), an ended run whose end status is the payload, a launch failure whose error
    /// and summary status are, a live run whose phase is, a legacy run whose summary status is, and a
    /// run whose launcher died whose summary status (its state) is.
    /// </summary>
    private void WriteHostileRuns(string p)
    {
        _proc.Add(120, StatusDocs.LauncherTicks);
        _proc.Add(121, StatusDocs.LauncherTicks);

        JsonObject live = StatusDocs.Make("name" + p, launcherPid: 120, worker: StatusDocs.Worker(), startedAt: Ago(TimeSpan.FromMinutes(5)),
            lastTool: StatusDocs.Tool("tool" + p, "completed", Ago(TimeSpan.FromSeconds(4))));
        live["spec"]!["model"] = "model" + p;
        live["spec"]!["runtime"] = "runtime" + p;
        _ = Write("20261122-115500-live", live);

        _ = Write("20261122-112000-ended", StatusDocs.Make("ended" + p, phase: "done", launcherPid: 900,
            end: StatusDocs.End("state" + p, Ago(TimeSpan.FromMinutes(30))), startedAt: Ago(TimeSpan.FromMinutes(40))));

        JsonObject failure = StatusDocs.End("launch-failed", Ago(TimeSpan.FromMinutes(25)));
        failure["error"] = "error" + p;
        failure["after_summary"] = true;
        failure["summary_status"] = "sumstatus" + p;
        _ = Write("20261122-112500-failed", StatusDocs.Make("failed" + p, phase: "failed", launcherPid: 901, end: failure,
            startedAt: Ago(TimeSpan.FromMinutes(35))));

        _ = Write("20261122-115000-phase", StatusDocs.Make("phase-run", phase: "phase" + p, launcherPid: 121, startedAt: Ago(TimeSpan.FromMinutes(10))));

        _ = Write("20261122-114000-legacy", doc: null, summary: "legacy" + p);

        _ = Write("20261122-113000-died", StatusDocs.Make("sumstate" + p, phase: "finishing", launcherPid: 902, worker: StatusDocs.Worker(),
            updatedAt: Ago(TimeSpan.FromMinutes(10)), startedAt: Ago(TimeSpan.FromMinutes(20))), summary: "summary" + p);
    }

    private static JsonObject RunNamed(JsonObject doc, string name) =>
        doc["runs"]!.AsArray().Select(r => r!.AsObject()).Single(r => (r["run"]?["name"] ?? r["name"])?.GetValue<string>() == name);

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task The_plain_output_has_no_control_character_and_one_line_per_run_whatever_the_files_holdAsync(string p)
    {
        WriteHostileRuns(p);

        (int code, string output) = await HereAsync();

        Assert.Equal(0, code);
        _ = TerminalAssert.AssertPlain(output, HostileRuns);
    }

    [Fact]
    public async Task An_escape_sequence_in_a_name_shows_as_a_question_mark_in_front_of_its_visible_partAsync()
    {
        WriteHostileRuns("\u001b[2J\u001b]0;title\u0007");

        (_, string output) = await HereAsync();

        string[] lines = TerminalAssert.AssertPlain(output, HostileRuns);
        Assert.Contains(lines, l => l.Contains("name?[2J?]0;title?", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("last tool?[2J?]0;title? 4s ago", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_break_and_a_forged_row_in_a_name_stay_inside_that_runs_lineAsync()
    {
        WriteHostileRuns("\r\nrunning  forged-run  oc  M  1 calls  $0.00/$1.00  1s  -");

        (_, string output) = await HereAsync();

        string[] lines = TerminalAssert.AssertPlain(output, HostileRuns);
        Assert.DoesNotContain(lines, l => l.StartsWith("running  forged-run", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("name??running  forged-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_over_its_column_limit_is_cut_with_a_tilde_and_one_at_the_limit_is_notAsync()
    {
        int limit = StatusText.CellLimits[1];
        _proc.Add(120, StatusDocs.LauncherTicks);
        _proc.Add(121, StatusDocs.LauncherTicks);
        _ = Write("20261122-115500-over", StatusDocs.Make(new string('n', limit + 50), launcherPid: 120, worker: StatusDocs.Worker()));
        _ = Write("20261122-115400-exact", StatusDocs.Make(new string('m', limit), launcherPid: 121, worker: StatusDocs.Worker()));

        (_, string output) = await HereAsync();

        string[] lines = TerminalAssert.AssertPlain(output, 2);
        Assert.Contains(lines, l => l.Contains(new string('n', limit - 1) + "~", StringComparison.Ordinal) && !l.Contains(new string('n', limit), StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(new string('m', limit), StringComparison.Ordinal) && !l.Contains('~', StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task An_unreadable_status_file_whose_parse_error_echoes_the_text_is_still_one_clean_lineAsync(string p)
    {
        _ = Write("20261122-114000-torn", doc: null);
        await File.WriteAllTextAsync(Path.Combine(_root, "runs", "20261122-114000-torn", "status.json"), "{ " + p, TestContext.Current.CancellationToken);

        (int plainCode, string plain) = await HereAsync();
        (int jsonCode, string json) = await HereAsync("--json");

        Assert.Equal(0, plainCode);
        Assert.Equal(0, jsonCode);
        _ = TerminalAssert.AssertPlain(plain, 1);
        Assert.Empty(TerminalAssert.JsonProblems(json));
        Assert.Equal("unreadable", RunNamed(JsonNode.Parse(json)!.AsObject(), "torn")["state"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task The_json_output_is_valid_escapes_every_hostile_character_and_parses_back_to_the_valuesAsync(string p)
    {
        WriteHostileRuns(p);

        (int code, string output) = await HereAsync("--json");

        Assert.Equal(0, code);
        Assert.Empty(TerminalAssert.JsonProblems(output));
        JsonObject doc = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(HostileRuns, doc["runs"]!.AsArray().Count);

        JsonObject live = RunNamed(doc, "name" + p);
        Assert.Equal("model" + p, live["spec"]!["model"]!.GetValue<string>());
        Assert.Equal("runtime" + p, live["spec"]!["runtime"]!.GetValue<string>());
        Assert.Equal("tool" + p, live["live"]!["last_tool"]!["name"]!.GetValue<string>());

        JsonObject ended = RunNamed(doc, "ended" + p);
        Assert.Equal("state" + p, ended["state"]!.GetValue<string>());
        Assert.Equal("state" + p, ended["end"]!["status"]!.GetValue<string>());

        JsonObject failed = RunNamed(doc, "failed" + p);
        Assert.Equal("error" + p, failed["end"]!["error"]!.GetValue<string>());
        Assert.Equal("sumstatus" + p, failed["end"]!["summary_status"]!.GetValue<string>());
        Assert.Contains("error" + p, failed["detail"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal("phase" + p, RunNamed(doc, "phase-run")["state"]!.GetValue<string>());
        Assert.Equal("legacy" + p, RunNamed(doc, "legacy")["summary_status"]!.GetValue<string>());
        Assert.Equal("summary" + p, RunNamed(doc, "sumstate" + p)["state"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task A_run_directory_and_a_runs_root_whose_names_carry_the_text_are_cleaned_in_both_modesAsync(string payload)
    {
        // A directory name cannot hold NUL or a slash.
        string p = payload.Replace("\0", string.Empty, StringComparison.Ordinal).Replace('/', '-');
        string hostileRoot = Path.Combine(_root, "root" + p);
        string legacyId = "20261122-114000-legacy" + p;
        string statusId = "20261122-114500-status" + p;
        string legacyDir;
        try
        {
            legacyDir = StatusDocs.WriteRun(hostileRoot, legacyId, doc: null, summaryStatus: "success");
            JsonObject doc = StatusDocs.Make("x", phase: "done", launcherPid: 900, end: StatusDocs.End("success"));
            _ = doc["run"]!.AsObject().Remove("name");
            _ = StatusDocs.WriteRun(hostileRoot, statusId, doc);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            Assert.Skip("this file system does not allow such a directory name: " + ex.GetType().Name);
            return;
        }

        (int plainCode, string plain) = await RunAsync(_root, "--here", "--runs-root", hostileRoot);
        (int jsonCode, string json) = await RunAsync(_root, "--here", "--runs-root", hostileRoot, "--json");
        (int oneCode, string one) = await RunAsync(_root, "--run", legacyDir);

        Assert.Equal(0, plainCode);
        Assert.Equal(0, jsonCode);
        Assert.Equal(0, oneCode);
        _ = TerminalAssert.AssertPlain(plain, 2);
        _ = TerminalAssert.AssertPlain(one, 1);
        Assert.Empty(TerminalAssert.JsonProblems(json));
        JsonObject doc2 = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(2, doc2["runs"]!.AsArray().Count);
        Assert.Contains(doc2["runs"]!.AsArray(), r => r!["id"]?.GetValue<string>() == legacyId);
    }

    [Fact]
    public async Task A_lone_surrogate_escape_in_status_json_neither_crashes_the_command_nor_reaches_the_outputAsync()
    {
        _proc.Add(120, StatusDocs.LauncherTicks);
        string text = StatusDocs.Make("LONE", launcherPid: 120, worker: StatusDocs.Worker()).ToJsonString().Replace("LONE", "a\\ud800b", StringComparison.Ordinal);
        string dir = Write("20261122-115500-lone", doc: null);
        await File.WriteAllTextAsync(Path.Combine(dir, "status.json"), text, TestContext.Current.CancellationToken);

        (int plainCode, string plain) = await HereAsync();
        (int jsonCode, string json) = await HereAsync("--json");

        Assert.Equal(0, plainCode);
        Assert.Equal(0, jsonCode);
        _ = TerminalAssert.AssertPlain(plain, 1);
        Assert.Empty(TerminalAssert.JsonProblems(json));
        _ = Assert.Single(JsonNode.Parse(json)!["runs"]!.AsArray());
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task An_unknown_flag_is_echoed_cleaned_on_one_lineAsync(string payload)
    {
        // An argument cannot hold NUL.
        string p = payload.Replace("\0", string.Empty, StringComparison.Ordinal);

        (int code, string output) = await RunAsync(_root, "--here", "--bogus" + p);

        Assert.Equal(2, code);
        Assert.Empty(TerminalAssert.PlainProblems(output));
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _ = Assert.Single(lines);
        Assert.Contains("--bogus", lines[0], StringComparison.Ordinal);
        Assert.Contains("--run", lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task A_missing_run_dir_is_echoed_cleaned_on_one_lineAsync(string payload)
    {
        string p = payload.Replace("\0", string.Empty, StringComparison.Ordinal);

        (int code, string output) = await RunAsync(_root, "--run", "missing" + p);

        Assert.Equal(2, code);
        Assert.Empty(TerminalAssert.PlainProblems(output));
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _ = Assert.Single(lines);
        Assert.Contains("no run dir", lines[0], StringComparison.Ordinal);
        Assert.Contains("missing", lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task A_bad_since_value_is_echoed_cleaned_on_one_lineAsync(string payload)
    {
        string p = payload.Replace("\0", string.Empty, StringComparison.Ordinal);

        (int code, string output) = await RunAsync(_root, "--here", "--since", "5x" + p);

        Assert.Equal(2, code);
        Assert.Empty(TerminalAssert.PlainProblems(output));
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _ = Assert.Single(lines);
        Assert.Contains("5x", lines[0], StringComparison.Ordinal);
    }
}
