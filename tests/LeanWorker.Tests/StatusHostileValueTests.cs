using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status` over status files that parse but whose values are of the wrong type or out of range (the
/// writer's own file is trusted for display only, and the worker can write it). Whatever a field
/// holds, the command lists the run in both modes, exits 0, never prints LEAN-WORKER LAUNCH FAILED
/// and lists the healthy run beside it as usual; a huge duration is shown clamped to at most ten years.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class StatusHostileValueTests : IDisposable
{
    private const int DeadLauncher = 999;

    // One wrong-typed field each: the fields the reader and the plain and --json outputs read.
    private static readonly Dictionary<string, Action<JsonObject>> _wrongTypes = new(StringComparer.Ordinal)
    {
        ["live-string"] = d => d["live"] = "oops",
        ["live-array"] = d => d["live"] = new JsonArray(1, 2),
        ["live-number"] = d => d["live"] = 5,
        ["turns-object"] = d => d["live"]!["turns"] = new JsonObject { ["a"] = 1 },
        ["turns-string"] = d => d["live"]!["turns"] = "many",
        ["pid-string"] = d => d["launcher"]!["pid"] = "120",
        ["pid-object"] = d => d["launcher"]!["pid"] = new JsonObject(),
        ["end-array"] = d => d["end"] = new JsonArray(),
        ["end-string"] = d => d["end"] = "done",
        ["end-number"] = d => d["end"] = 3,
        ["phase-number"] = d => d["phase"] = 5,
        ["phase-object"] = d => d["phase"] = new JsonObject { ["x"] = 1 },
        ["phase-null"] = d => d["phase"] = null,
        ["run-null"] = d => d["run"] = null,
        ["run-string"] = d => d["run"] = "x",
        ["run-array"] = d => d["run"] = new JsonArray("a"),
        ["launcher-array"] = d => d["launcher"] = new JsonArray(1),
        ["launcher-string"] = d => d["launcher"] = "100",
        ["launcher-null"] = d => d["launcher"] = null,
        ["boot-number"] = d => d["launcher"]!["boot_id"] = 5,
        ["worker-string"] = d => d["worker"] = "w",
        ["worker-array"] = d => d["worker"] = new JsonArray(1, 2),
        ["spec-string"] = d => d["spec"] = "s",
        ["spec-array"] = d => d["spec"] = new JsonArray(),
        ["spec-null"] = d => d["spec"] = null,
        ["tokens-array"] = d => d["live"]!["tokens"] = new JsonArray(1),
        ["tokens-string"] = d => d["live"]!["tokens"] = "lots",
        ["last-tool-string"] = d => d["live"]!["last_tool"] = "bash",
        ["last-tool-array"] = d => d["live"]!["last_tool"] = new JsonArray("bash"),
        ["last-tool-fields"] = d => d["live"]!["last_tool"] = new JsonObject { ["name"] = 5, ["at"] = new JsonObject(), ["state"] = new JsonArray() },
        ["api-calls-string"] = d => d["live"]!["api_calls"] = "twelve",
        ["api-calls-object"] = d => d["live"]!["api_calls"] = new JsonObject(),
        ["cost-string"] = d => d["live"]!["cost_usd"] = "free",
        ["cost-object"] = d => d["live"]!["cost_usd"] = new JsonObject(),
        ["budget-object"] = d => d["spec"]!["budget_usd"] = new JsonObject(),
        ["budget-string"] = d => d["spec"]!["budget_usd"] = "3",
        ["started-number"] = d => d["spec"]!["started_at"] = 12345,
        ["updated-object"] = d => d["updated_at"] = new JsonObject(),
        ["updated-string"] = d => d["updated_at"] = "yesterday",
        ["last-event-bool"] = d => d["live"]!["last_event_at"] = true,
        ["gate-round-string"] = d => d["live"]!["gate_round"] = "one",
        ["wrapped-up-string"] = d => d["live"]!["wrapped_up"] = "yes",
        ["owner-string"] = d => d["owner"] = "me",
        ["schema-array"] = d => d["schema"] = new JsonArray("lean-worker.status/1"),
    };

    // Each is "key and value as the writer writes them": the number put in its place is the test's.
    private static readonly string[] _numberFields =
    [
        "\"elapsed_ms\":221000", "\"seq\":7", "\"pid\":120", "\"start_ticks\":5000", "\"api_calls\":12", "\"input\":3100",
        "\"cache_write\":0", "\"cache_read\":399000", "\"output\":9870", "\"thinking\":3200", "\"cost_usd\":0.4123", "\"budget_usd\":3.0",
        "\"wrap_up_usd\":2.4", "\"timeout_minutes\":60", "\"round\":1", "\"pid\":300", "\"pgid\":300", "\"sid\":300", "\"start_ticks\":6000",
    ];

    private static readonly string[] _numbers =
    [
        "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809", "18446744073709551616",
        "1e15", "1e300", "1e308", "1e400", "-1e400", "-1", "-0.5", "0.5", "1e29", "79228162457643370000000000000", "2147483648", "-2147483649",
    ];

    private readonly StatusHostileRig _rig = new();

    public static TheoryData<string> WrongTypes => [.. _wrongTypes.Keys];

    public static TheoryData<string, string> Numbers
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string field in _numberFields)
            {
                foreach (string number in _numbers)
                {
                    data.Add(field, number);
                }
            }

            return data;
        }
    }

    public void Dispose() => _rig.Dispose();

    [GeneratedRegex(@"\b(?<hours>\d+)h\d\dm\b", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HoursPattern();

    private static string WithNumber(string text, string field, string number)
    {
        Assert.Contains(field, text, StringComparison.Ordinal);
        return text.Replace(field, field[..(field.IndexOf(':', StringComparison.Ordinal) + 1)] + number, StringComparison.Ordinal);
    }

    private static string HostileLine(string plain) =>
        plain.Split('\n').Single(line => line.Contains("hostile", StringComparison.Ordinal) && !line.StartsWith("lean-worker status:", StringComparison.Ordinal));

    private static long Hours(string plain)
    {
        Match hours = HoursPattern().Match(HostileLine(plain));
        Assert.True(hours.Success, HostileLine(plain));
        return long.Parse(hours.Groups["hours"].Value, CultureInfo.InvariantCulture);
    }

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public async Task A_field_of_the_wrong_type_never_fails_the_listing_and_the_healthy_run_is_listed_as_usualAsync(string what)
    {
        JsonObject doc = StatusHostileRig.Hostile();
        _wrongTypes[what](doc);
        _ = StatusDocs.WriteRun(_rig.Root, StatusHostileRig.HostileId, doc);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
    }

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public async Task A_field_of_the_wrong_type_never_fails_the_listing_of_a_run_whose_launcher_is_goneAsync(string what)
    {
        JsonObject doc = StatusHostileRig.Hostile();
        doc["launcher"]!["pid"] = DeadLauncher;
        _wrongTypes[what](doc);
        _ = StatusDocs.WriteRun(_rig.Root, StatusHostileRig.HostileId, doc);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task A_number_at_or_beyond_the_range_of_its_type_never_fails_the_listing_and_the_healthy_run_is_listed_as_usualAsync(string field, string number)
    {
        string text = WithNumber(StatusHostileRig.Hostile().ToJsonString(), field, number);
        await File.WriteAllTextAsync(_rig.HostilePath("status.json"), text, TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task A_number_at_or_beyond_the_range_in_a_run_whose_launcher_is_gone_never_fails_the_listingAsync(string field, string number)
    {
        JsonObject doc = StatusHostileRig.Hostile();
        doc["launcher"]!["pid"] = DeadLauncher;
        string text = WithNumber(doc.ToJsonString(), field is "\"pid\":120" ? "\"pid\":999" : field, number);
        await File.WriteAllTextAsync(_rig.HostilePath("status.json"), text, TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
    }

    [Theory]
    [InlineData("1e15")]
    [InlineData("1e300")]
    [InlineData("1e400")]
    [InlineData("-1")]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775808")]
    [InlineData("315360000000")]
    [InlineData("315360000001")]
    public async Task A_valid_file_with_a_huge_or_negative_duration_is_shown_with_a_duration_of_at_most_ten_yearsAsync(string elapsed)
    {
        string text = WithNumber(StatusHostileRig.Hostile().ToJsonString(), "\"elapsed_ms\":221000", elapsed);
        await File.WriteAllTextAsync(_rig.HostilePath("status.json"), text, TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        JsonObject row = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
        Assert.Equal("running", row["state"]!.GetValue<string>());
        Assert.True(!HoursPattern().IsMatch(HostileLine(l.Plain)) || Hours(l.Plain) <= 3650 * 24, HostileLine(l.Plain));
    }

    [Theory]
    [InlineData("1e15")]
    [InlineData("9223372036854775807")]
    public async Task A_duration_far_over_ten_years_is_shown_as_a_clamped_count_of_hours_not_as_a_small_oneAsync(string elapsed)
    {
        string text = WithNumber(StatusHostileRig.Hostile().ToJsonString(), "\"elapsed_ms\":221000", elapsed);
        await File.WriteAllTextAsync(_rig.HostilePath("status.json"), text, TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertHealthyStillListed(l);
        Assert.InRange(Hours(l.Plain), 1000, 3650 * 24);
    }
}
