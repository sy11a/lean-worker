using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status` over a status.json whose text is the problem: duplicate keys (also ones that carry an
/// escape sequence), lone surrogate escapes in a key or a value, invalid UTF-8, nesting at and over the
/// depth limit, and text that is not one status object. Each makes THAT run an unreadable row with a
/// fixed reason (never the file's text), in plain and --json mode, next to a healthy run listed as
/// usual. The depth bound pinned: the whole document may be 16 levels deep counting the outer
/// object (16 is read, 17 is refused).
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusHostileContentTests : IDisposable
{
    private const int MaxDepth = 16;

    private readonly StatusHostileRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static string Text() => StatusHostileRig.Hostile().ToJsonString();

    private static string Replace(string text, string needle, string with)
    {
        Assert.Contains(needle, text, StringComparison.Ordinal);
        return text.Replace(needle, with, StringComparison.Ordinal);
    }

    private async Task<StatusHostileRig.Listing> ListTextAsync(string text)
    {
        await File.WriteAllTextAsync(_rig.HostilePath("status.json"), text, TestContext.Current.CancellationToken);
        return await _rig.ListAsync();
    }

    private async Task<StatusHostileRig.Listing> ListBytesAsync(byte[] bytes)
    {
        await File.WriteAllBytesAsync(_rig.HostilePath("status.json"), bytes, TestContext.Current.CancellationToken);
        return await _rig.ListAsync();
    }

    // "needle|text it becomes": a key twice at the top level, in the launcher, in the tokens (two
    // levels down) and in the spec, with different values and with the same one.
    public static TheoryData<string> DuplicateKeys
    {
        get
        {
            return
            [
                "\"phase\":\"worker\"|\"phase\":\"worker\",\"phase\":\"done\"",
                "\"seq\":7|\"seq\":7,\"seq\":8",
                "\"launcher\":{\"pid\":120,|\"launcher\":{\"pid\":120,\"pid\":121,",
                "\"live\":{\"api_calls\":12,|\"live\":{\"api_calls\":12,\"api_calls\":13,",
                "\"tokens\":{\"input\":3100,|\"tokens\":{\"input\":3100,\"input\":1,",
                "\"spec\":{\"profile\"|\"spec\":{\"model\":\"a\",\"model\":\"b\",\"profile\"",
                "\"seq\":7|\"seq\":7,\"seq\":7",
            ];
        }
    }

    [Theory]
    [MemberData(nameof(DuplicateKeys))]
    public async Task A_status_file_with_a_duplicate_key_is_an_unreadable_rowAsync(string change)
    {
        string[] parts = change.Split('|');

        StatusHostileRig.Listing l = await ListTextAsync(Replace(Text(), parts[0], parts[1]));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Theory]
    [InlineData("\"seq\":7|\"\\u001b[2J\":1,\"\\u001b[2J\":2,\"seq\":7")]
    [InlineData("\"launcher\":{\"pid\":120,|\"launcher\":{\"\\u001b]0;title\\u0007\":1,\"\\u001b]0;title\\u0007\":2,\"pid\":120,")]
    [InlineData("\"seq\":7|\"a\\u001b[31m\":{\"x\":1},\"a\\u001b[31m\":{\"x\":2},\"seq\":7")]
    [InlineData("\"seq\":7|\"\\u009b31m\\u202e\":1,\"\\u009b31m\\u202e\":2,\"seq\":7")]
    public async Task A_duplicate_key_that_carries_an_escape_sequence_reaches_neither_stdout_nor_stderrAsync(string change)
    {
        string[] parts = change.Split('|');

        StatusHostileRig.Listing l = await ListTextAsync(Replace(Text(), parts[0], parts[1]));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l, "[2J", "]0;title", "[31m", "title");
        foreach (string output in new[] { l.Plain, l.Json, l.Error })
        {
            Assert.DoesNotContain('\u001b', output);
            Assert.DoesNotContain('\u009b', output);
            Assert.DoesNotContain('\u202e', output);
        }
    }

    [Fact]
    public async Task The_reason_for_an_unreadable_row_is_the_same_whatever_text_the_file_holdsAsync()
    {
        string one = Replace(Text(), "\"seq\":7", "\"alpha-one\":1,\"alpha-one\":2,\"seq\":7");
        string two = Replace(Text(), "\"seq\":7", "\"beta-two-two\":1,\"beta-two-two\":2,\"seq\":7");

        StatusHostileRig.Listing first = await ListTextAsync(one);
        StatusHostileRig.Listing second = await ListTextAsync(two);

        JsonObject a = StatusHostileRig.AssertOnlyHostileUnreadable(first, "alpha-one");
        JsonObject b = StatusHostileRig.AssertOnlyHostileUnreadable(second, "beta-two-two");
        Assert.Equal(a["detail"]!.GetValue<string>(), b["detail"]!.GetValue<string>());
    }

    public static TheoryData<string> LoneSurrogates
    {
        get
        {
            return
            [
                "\"name\":\"hostile\"|\"name\":\"a\\ud800b\"",
                "\"name\":\"hostile\"|\"name\":\"a\\udc00b\"",
                "\"name\":\"hostile\"|\"name\":\"a\\udc00\\ud800b\"",
                "\"name\":\"hostile\"|\"name\":\"ab\\ud83d\"",
                "\"model\":\"MiniMax-M3\"|\"model\":\"M\\ud800\"",
                "\"seq\":7|\"a\\ud800b\":1,\"seq\":7",
                "\"seq\":7|\"\\udfff\":1,\"seq\":7",
                "\"seq\":7|\"extra\":[\"x\",{\"y\":\"\\ud800\"}],\"seq\":7",
            ];
        }
    }

    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public async Task A_lone_surrogate_escape_in_a_key_or_a_value_is_an_unreadable_rowAsync(string change)
    {
        string[] parts = change.Split('|');

        StatusHostileRig.Listing l = await ListTextAsync(Replace(Text(), parts[0], parts[1]));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_surrogate_pair_written_as_two_escapes_is_text_like_any_other_and_the_run_is_listedAsync()
    {
        StatusHostileRig.Listing l = await ListTextAsync(Replace(Text(), "\"name\":\"hostile\"", "\"name\":\"a\\ud83d\\ude00b\""));

        JsonObject row = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
        Assert.Equal("running", row["state"]!.GetValue<string>());
        Assert.Contains("a\U0001F600b", l.Plain, StringComparison.Ordinal);
    }

    private static byte[] After(string text, string needle, params byte[] bytes)
    {
        Assert.Contains(needle, text, StringComparison.Ordinal);
        int at = text.IndexOf(needle, StringComparison.Ordinal) + needle.Length;
        return [.. Encoding.UTF8.GetBytes(text[..at]), .. bytes, .. Encoding.UTF8.GetBytes(text[at..])];
    }

    // Bytes put inside a value ("name":"host...) or inside a key ("se...q"): a stray continuation byte,
    // an overlong form, a truncated sequence, an encoded surrogate and bytes that start no sequence.
    public static TheoryData<string, string> InvalidUtf8
    {
        get
        {
            return new()
            {
                { "\"name\":\"host", "C3-28" },
                { "\"name\":\"host", "FF" },
                { "\"name\":\"host", "C0-AF" },
                { "\"name\":\"host", "E2-82" },
                { "\"name\":\"host", "ED-A0-80" },
                { "\"se", "FE-FF" },
                { "\"se", "80" },
                { "\"model\":\"Mini", "F0-28-8C-28" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public async Task A_status_file_with_invalid_UTF_8_in_a_key_or_a_value_is_an_unreadable_rowAsync(string needle, string hex)
    {
        byte[] bad = Convert.FromHexString(hex.Replace("-", string.Empty, StringComparison.Ordinal));

        StatusHostileRig.Listing l = await ListBytesAsync(After(Text(), needle, bad));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_file_of_bytes_that_are_no_text_at_all_is_an_unreadable_rowAsync()
    {
        StatusHostileRig.Listing l = await ListBytesAsync([.. Enumerable.Range(0, 256).Select(i => (byte)i)]);

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    private static string Nested(int levels, bool objects)
    {
        // A value `levels` containers deep.
        return objects
            ? string.Concat(Enumerable.Repeat("{\"a\":", levels - 1)) + "{}" + new string('}', levels - 1)
            : new string('[', levels) + new string(']', levels);
    }

    /// <summary>
    /// The valid document with one more field holding a value so that the whole document is
    /// <paramref name="depth"/> containers deep, the outer object counted.
    /// </summary>
    private static string WithDepth(int depth, bool objects) =>
        Replace(Text(), "\"seq\":7", "\"deep\":" + Nested(depth - 1, objects) + ",\"seq\":7");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_document_16_levels_deep_is_readAsync(bool objects)
    {
        StatusHostileRig.Listing l = await ListTextAsync(WithDepth(MaxDepth, objects));

        JsonObject row = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
        Assert.Equal("running", row["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(17, false)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    [InlineData(1000, false)]
    [InlineData(1000, true)]
    [InlineData(100000, false)]
    [InlineData(100000, true)]
    public async Task A_document_over_16_levels_deep_is_an_unreadable_rowAsync(int depth, bool objects)
    {
        StatusHostileRig.Listing l = await ListTextAsync(WithDepth(depth, objects));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_document_that_is_only_open_brackets_is_an_unreadable_row_and_the_command_returnsAsync()
    {
        StatusHostileRig.Listing l = await ListTextAsync(new string('[', 500_000));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"schema\":\"lean-worker.status/1\"}]")]
    [InlineData("\"lean-worker.status/1\"")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"lean-worker.status/2\"}")]
    [InlineData("{\"schema\":5}")]
    [InlineData("{\"schema\":null}")]
    [InlineData("{\"schema\":\"lean-worker.status/1\",")]
    [InlineData("not json at all")]
    [InlineData("{\"schema\":\"lean-worker.status/1\"} trailing")]
    [InlineData("{\"schema\":\"lean-worker.status/1\",}")]
    [InlineData("{\"schema\":\"lean-worker.status/1\"} /* comment */")]
    [InlineData("{'schema':'lean-worker.status/1'}")]
    public async Task A_file_that_is_not_one_status_object_is_an_unreadable_rowAsync(string text)
    {
        StatusHostileRig.Listing l = await ListTextAsync(text);

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_valid_status_file_with_a_byte_order_mark_is_still_readAsync()
    {
        StatusHostileRig.Listing l = await ListBytesAsync([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(StatusHostileRig.Hostile().ToJsonString())]);

        JsonObject row = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
        Assert.Equal("running", row["state"]!.GetValue<string>());
    }
}
