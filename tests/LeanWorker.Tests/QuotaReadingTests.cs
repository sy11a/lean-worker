using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// QuotaReading.FromJson reads the cached form that ToJson writes, and tolerates missing fields.
/// </summary>
public class QuotaReadingTests
{
    [Fact]
    public void FromJson_reads_back_what_ToJson_wrote()
    {
        DateTimeOffset readAt = new(2026, 10, 13, 8, 30, 0, TimeSpan.Zero);
        DateTimeOffset resets = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        QuotaReading original = new("zai-coding-plan", "max",
            [new("5h", 12.5m, resets, Detail: null), new("mcp-monthly", 0, ResetsAt: null, "12 of 4000 calls")], readAt);

        QuotaReading back = QuotaReading.FromJson(original.ToJson());

        Assert.Equal("zai-coding-plan", back.Provider);
        Assert.Equal("max", back.Level);
        Assert.Equal(readAt, back.ReadAt);
        Assert.Equal(2, back.Windows.Count);
        Assert.Equal(original.Windows[0], back.Windows[0]);
        Assert.Equal(original.Windows[1], back.Windows[1]);
    }

    [Fact]
    public void FromJson_defaults_every_missing_field()
    {
        QuotaReading q = QuotaReading.FromJson([]);

        Assert.Equal(string.Empty, q.Provider);
        Assert.Null(q.Level);
        Assert.Empty(q.Windows);
        Assert.Equal(DateTimeOffset.MinValue, q.ReadAt);
    }

    [Fact]
    public void FromJson_defaults_missing_window_fields_and_skips_non_object_windows()
    {
        QuotaReading q = QuotaReading.FromJson(JsonNode.Parse(/*lang=json,strict*/ """
            {"provider":"p","read_at":"2026-10-13T08:30:00+00:00","windows":[{}, 5, "x"]}
            """)!.AsObject());

        QuotaWindow w = Assert.Single(q.Windows);
        Assert.Equal(new QuotaWindow(string.Empty, 0, ResetsAt: null, Detail: null), w);
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T08:30:00+00:00", CultureInfo.InvariantCulture), q.ReadAt);
    }

    [Fact]
    public void FromJson_ignores_windows_that_are_not_an_array()
    {
        QuotaReading q = QuotaReading.FromJson(JsonNode.Parse(/*lang=json,strict*/ """{"provider":"p","windows":{"5h":1}}""")!.AsObject());

        Assert.Empty(q.Windows);
    }

    [Fact]
    public void FromJson_fails_on_an_unparseable_reset_time()
    {
        JsonObject o = JsonNode.Parse(/*lang=json,strict*/ """{"windows":[{"name":"5h","resets_at":"soon"}]}""")!.AsObject();

        _ = Assert.Throws<FormatException>(() => QuotaReading.FromJson(o));
    }
}
