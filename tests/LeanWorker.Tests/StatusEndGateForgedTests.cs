using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A forged <c>end.gate</c> only makes a verdict stricter (docs/research/live-status/README.md, section 5; the follow-up review of #147): on an
/// honest single run that finished clean, <c>"continue"</c> turns exit 0 into 8 (7 when --timeout is shorter) and any other
/// value (another string, a number, a boolean, null, an object, an array, a very long string, control characters) is
/// ignored; on a failed run no value changes the verdict (always 1, at once, never a 0 and never an 8). What the commands print stays clean whatever the value.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusEndGateForgedTests : IDisposable
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(2);

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    public static TheoryData<string> Values() =>
        ["continue", "other", "upper", "padded", "leading", "empty", "number", "bool", "null", "object", "array", "long", "esc", "nul", "bidi"];

    // The JSON text of the forged value a test name stands for.
    private static string Json(string name)
    {
        return name switch
        {
            "continue" => StatusEndGateScenes.Continue,
            "other" => "\"stop\"",
            "upper" => "\"CONTINUE\"",
            "padded" => "\"continue \"",
            "leading" => "\" continue\"",
            "empty" => "\"\"",
            "number" => "5",
            "bool" => "true",
            "null" => "null",
            "object" => /*lang=json,strict*/ """{"gate":"continue"}""",
            "array" => /*lang=json,strict*/ """["continue"]""",
            "long" => "\"" + new string('x', 100_000) + "\"",
            "esc" => "\"continue\\u001b[31m\\u0007\\u009b\"",
            "nul" => "\"continue\\u0000\"",
            _ => "\"continue\\u202e\\u2028\"",
        };
    }

    private async Task<StatusLiveRig.Result> WaitAfterEndAsync(string status, string value, params string[] more)
    {
        StatusLiveRig.Live run = _rig.Add("g-single", startedAgo: TimeSpan.FromMinutes(10));
        _rig.At(Seconds(10), () => _rig.End(run, status, gateJson: Json(value)));
        List<string> args = ["--owner", StatusLiveRig.Key, "--wait", "--grace", "2m"];
        if (!more.Contains("--timeout", StringComparer.Ordinal))
        {
            args.AddRange(["--timeout", "30m"]);
        }

        args.AddRange(more);
        StatusLiveRig.Result r = await _rig.CallAsync([.. args]);
        return r;
    }

    [Theory]
    [MemberData(nameof(Values))]
    public async Task On_an_honest_clean_run_only_continue_turns_exit_0_into_8_and_every_other_value_is_ignoredAsync(string value)
    {
        StatusLiveRig.Result r = await WaitAfterEndAsync("success", value);

        Assert.Null(r.Error);
        Assert.Empty(TerminalAssert.PlainProblems(r.Output));
        if (value is "continue")
        {
            Assert.Equal(8, r.Code);
            Assert.InRange(_rig.Elapsed, Seconds(10) + _grace, Seconds(12) + _grace);
        }
        else
        {
            Assert.Equal(0, r.Code);
            Assert.InRange(_rig.Elapsed, Seconds(10), Seconds(12));
        }
    }

    [Fact]
    public async Task On_an_honest_clean_run_continue_gives_exit_7_when_the_timeout_is_shorter_than_the_graceAsync()
    {
        StatusLiveRig.Result r = await WaitAfterEndAsync("success", "continue", "--timeout", "90s");

        Assert.Equal(7, r.Code);
        Assert.Equal(Seconds(90), _rig.Elapsed);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public async Task On_a_failed_run_whatever_end_gate_says_the_wait_gives_exit_1_at_onceAsync(string value)
    {
        StatusLiveRig.Result r = await WaitAfterEndAsync("error", value);

        Assert.Null(r.Error);
        // A failed end keeps its own verdict: the field neither turns it into a 0 nor takes it away (no 8).
        Assert.Equal(1, r.Code);
        Assert.InRange(_rig.Elapsed, Seconds(10), Seconds(12));
        Assert.Empty(TerminalAssert.PlainProblems(r.Output));
    }

    [Theory]
    [InlineData("success", 8, true)]
    [InlineData("error", 1, false)]
    public async Task With_any_a_forged_continue_holds_a_clean_end_and_never_ends_the_wait_as_a_0_aloneAsync(string status, int code, bool held)
    {
        StatusLiveRig.Result r = await WaitAfterEndAsync(status, "continue", "--any");

        Assert.NotEqual(0, r.Code);
        Assert.Equal(code, r.Code);
        TimeSpan hold = held ? _grace : TimeSpan.Zero;
        Assert.InRange(_rig.Elapsed, Seconds(10) + hold, Seconds(12) + hold);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public async Task What_the_listing_prints_stays_clean_whatever_the_forged_valueAsync(string value)
    {
        StatusLiveRig.Live run = _rig.Add("g-single");
        _rig.End(run, gateJson: Json(value));

        StatusLiveRig.Result plain = await _rig.CallAsync("--owner", StatusLiveRig.Key);
        StatusLiveRig.Result json = await _rig.CallAsync("--owner", StatusLiveRig.Key, "--json");

        Assert.Empty(TerminalAssert.PlainProblems(plain.Output));
        // The plain output is part of the JSON call's too (one writer): only what the second call added is the JSON.
        string document = json.Output[plain.Output.Length..];
        Assert.Empty(TerminalAssert.JsonProblems(document));
        JsonObject row = StatusIndexRig.JsonRows(document).Single();
        Assert.Equal(value is "continue" ? "unknown" : "success", row["state"]!.GetValue<string>());
    }
}
