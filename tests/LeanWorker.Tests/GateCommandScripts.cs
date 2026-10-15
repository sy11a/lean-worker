using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Arrange and assert helpers for the <c>gate.commands</c> tests: one sh script per named command that
/// records that it ran and how often and follows a per-round sequence of exit codes and finding counts, the
/// profile that lists those commands, the launcher run, and compact checks of the JSON records.
/// </summary>
/// <remarks>
/// A round sequence is written <c>"exit:count,exit:count,..."</c> (round 1 first; later rounds repeat the
/// last entry), e.g. <c>"1:3,0:0"</c> finds 3 and then is clean. A negative count prints no report line. A
/// chain is written <c>"name=rounds name=rounds ..."</c> in profile order, e.g. <c>"a=1:2,0:0 b=0:0"</c>.
/// </remarks>
internal static class GateCommandScripts
{
    /// <summary>
    /// The marker file a command script touches when it runs: <c>&lt;dir&gt;/&lt;name&gt;-ran</c>.
    /// </summary>
    public static string Marker(string dir, string name) => Path.Combine(dir, name + "-ran");

    /// <summary>
    /// Writes <c>&lt;dir&gt;/&lt;name&gt;.sh</c>. On its Nth invocation the script touches its marker, runs
    /// <paramref name="prelude"/> (shell lines, may be empty), prints "&lt;name&gt; output", and then follows
    /// round N of <paramref name="rounds"/>: for a count of 0 or more it writes a report with that many
    /// results to <paramref name="reportExpr"/> (a shell expression; default a per-round file next to the
    /// script) and prints its "sarif: " line; it then exits with the round's exit code.
    /// </summary>
    public static async Task<string> WriteAsync(string dir, string name, string rounds, string prelude = "", string? reportExpr = null)
    {
        string[] arms = rounds.Split(',');
        IEnumerable<string> cases = arms.Select((arm, i) => CaseArm(i == arms.Length - 1 ? "*" : (i + 1).ToString(CultureInfo.InvariantCulture), arm));
        string counter = Path.Combine(dir, name + "-n");
        string body =
            $"#!/bin/sh\ntouch '{Marker(dir, name)}'\n" +
            $"N=$(cat '{counter}' 2>/dev/null || echo 0); N=$((N+1)); echo $N > '{counter}'\n" +
            prelude +
            $"echo '{name} output'\nR={reportExpr ?? ("'" + Path.Combine(dir, name + "-report") + "'$N.json")}\n" +
            $"case $N in\n{string.Concat(cases)}esac\n";
        string path = Path.Combine(dir, name + ".sh");
        await GateLoopTests.WriteExecutableAsync(path, body);
        return path;
    }

    // One `case` arm: "<label>) [write the report; print its line;] exit <code> ;;".
    private static string CaseArm(string label, string arm)
    {
        string[] parts = arm.Split(':');
        int count = int.Parse(parts[1], CultureInfo.InvariantCulture);
        string report = count < 0
            ? string.Empty
            : "printf '{\"runs\":[{\"results\":[" + string.Join(',', Enumerable.Repeat("0", count)) + "]}]}' > \"$R\"; echo \"sarif: $R\"; ";
        return label + ") " + report + "exit " + parts[0] + " ;;\n";
    }

    /// <summary>
    /// One <c>gate.commands</c> entry: <c>{"name": name, "command": ["sh", script, ...extraArgv]}</c>.
    /// </summary>
    public static JsonObject Entry(string name, string script, params string[] extraArgv)
    {
        return new()
        {
            ["name"] = name,
            ["command"] = new JsonArray([.. new[] { "sh", script }.Concat(extraArgv).Select(a => (JsonNode)a)]),
        };
    }

    /// <summary>
    /// A profile whose gate is the <c>commands</c> form with the given entries.
    /// </summary>
    public static JsonObject Profile(params JsonObject[] entries) =>
        new() { ["gate"] = new JsonObject { ["commands"] = new JsonArray([.. entries]) } };

    /// <summary>
    /// The profile's <c>gate</c> object, for adding shared keys (trust, env, caps).
    /// </summary>
    public static JsonObject Gate(JsonObject profile) => profile["gate"]!.AsObject();

    /// <summary>
    /// Writes one command script per entry of <paramref name="chain"/> into <paramref name="dir"/> and
    /// returns the commands-form profile that runs them in order.
    /// </summary>
    public static async Task<JsonObject> ProfileOfAsync(string dir, string chain)
    {
        List<JsonObject> entries = [];
        foreach (string command in chain.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] nameAndRounds = command.Split('=');
            entries.Add(Entry(nameAndRounds[0], await WriteAsync(dir, nameAndRounds[0], nameAndRounds[1])));
        }

        return Profile([.. entries]);
    }

    /// <summary>
    /// Runs the launcher with a worker that first executes <paramref name="workerShell"/> and then streams
    /// <paramref name="stream"/>; the run's working directory is tracked for cleanup, and
    /// <paramref name="prepare"/> runs inside it before the launch.
    /// </summary>
    public static async Task<(int Code, string Stdout)> RunAsync(
        TempDirs dirs, string root, string workerShell = "true", Action? prepare = null, Action<Options>? configure = null,
        string stream = RunAsyncGolden.SuccessStream, GateContext? gateContext = null)
    {
        return await RunAsyncGolden.RunAsync(root, workerShell + "\n" + stream, o =>
        {
            dirs.Track(Directory.GetCurrentDirectory());
            prepare?.Invoke();
            configure?.Invoke(o);
        }, gateContext: gateContext);
    }

    /// <summary>
    /// The context the launcher's chain loop hands round 2: chain "chain1", round 1 counted 3 findings,
    /// the original task "do nothing" named "golden", <paramref name="feedback"/> as the aggregate's text.
    /// </summary>
    public static GateContext RoundTwoContext(IReadOnlyList<GateSpec> specs, string feedback)
    {
        Gate.GateResult round1 = new("findings", 1, 3, ReportPath: null, feedback, Error: null, TimeSpan.Zero);
        return new GateContext("chain1", 2, [3], 0m, "do nothing", "golden", round1, specs);
    }

    public static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    /// <summary>
    /// The summary's <c>gate</c> section of a run directory.
    /// </summary>
    public static JsonObject Section(string runDir) => RunAsyncGolden.Summary(runDir)["gate"]!.AsObject();

    /// <summary>
    /// The summary's <c>gate.commands</c> names, in order.
    /// </summary>
    public static string[] CommandNames(JsonObject gateSection) =>
        [.. gateSection["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())];

    public static string[] Keys(JsonObject o) => [.. o.Select(kv => kv.Key)];

    /// <summary>
    /// Every property of <paramref name="expectedJson"/> has the same JSON value in <paramref name="actual"/>
    /// (a JSON null also matches an absent key); other properties of <paramref name="actual"/> are not checked.
    /// </summary>
    public static void AssertFields(JsonObject actual, string expectedJson)
    {
        foreach ((string key, JsonNode? expected) in JsonNode.Parse(expectedJson)!.AsObject())
        {
            Assert.True(JsonNode.DeepEquals(expected, actual[key]), $"{key}: expected {expected?.ToJsonString() ?? "null"}, got {actual[key]?.ToJsonString() ?? "null"}");
        }
    }
}
