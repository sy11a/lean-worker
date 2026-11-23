using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

// Pins entries of the SHIPPED prices.json (the copy the build puts next to the launcher, the file Load reads
// first), not a fixture book: removing or changing an entry there must fail these tests.
public class ShippedPriceBookTests
{
    private static PriceBook Shipped() =>
        PriceBook.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prices.json")))!.AsObject());

    private static Usage OneMillionOfEachClass(string model) =>
        new("m", model, Input: 1_000_000, Output: 1_000_000, Reasoning: 0, CacheRead: 1_000_000, CacheWrite5m: 1_000_000, CacheWrite1h: 1_000_000);

    [Fact]
    public void Fable_5_2_has_its_own_entry_and_a_dated_id_matches_it()
    {
        PriceBook book = Shipped();
        Assert.Equal("anthropic/claude-fable-5-2", book.Find("anthropic", "claude-fable-5-2")?.Key);
        Assert.Equal("anthropic/claude-fable-5-2", book.Find("anthropic", "claude-fable-5-2-20261001")?.Key);
    }

    [Fact]
    public void Fable_5_2_costs_list_price_for_every_token_class()
    {
        ModelPrice? price = Shipped().Find("anthropic", "claude-fable-5-2");
        Assert.NotNull(price);
        Assert.Equal(8 + 40 + 0.2m + 10 + 16, price.Cost(OneMillionOfEachClass("claude-fable-5-2")));
    }

    [Fact]
    public void Fable_5_2_is_not_priced_as_dearest()
    {
        ModelPrice price = Shipped().Resolve("anthropic", "claude-fable-5-2", out string? note);
        Assert.Null(note);
        Assert.Equal("anthropic/claude-fable-5-2", price.Key);
    }

    [Theory]
    [InlineData("fable")]
    [InlineData("Fable")]
    [InlineData("fable[1m]")]
    public void The_fable_alias_resolves_to_5_2(string alias) =>
        Assert.Equal("claude-fable-5-2", Shipped().ResolveAlias("anthropic", alias));

    [Fact]
    public void Fable_5_1_is_still_priced_by_its_own_entry()
    {
        ModelPrice? price = Shipped().Find("anthropic", "claude-fable-5-1");
        Assert.NotNull(price);
        Assert.Equal("anthropic/claude-fable-5-1", price.Key);
        Assert.Equal(10 + 50 + 0.25m + 12.5m + 20, price.Cost(OneMillionOfEachClass("claude-fable-5-1")));
    }
}
