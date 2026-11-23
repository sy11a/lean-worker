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

    private const string Haiku55 = "claude-haiku-5-5";

    private static ModelPrice Entry(string model)
    {
        ModelPrice? price = Shipped().Find("anthropic", model);
        Assert.NotNull(price);
        return price;
    }

    private static Usage Tokens(string model, long input = 0, long output = 0, long cacheRead = 0, long write5m = 0, long write1h = 0) =>
        new("m", model, Input: input, Output: output, Reasoning: 0, CacheRead: cacheRead, CacheWrite5m: write5m, CacheWrite1h: write1h);

    // Tokens of exactly one class.
    private static Usage OnlyClass(string model, string tokenClass, long tokens)
    {
        return tokenClass switch
        {
            "input" => Tokens(model, input: tokens),
            "output" => Tokens(model, output: tokens),
            "cacheRead" => Tokens(model, cacheRead: tokens),
            "write5m" => Tokens(model, write5m: tokens),
            "write1h" => Tokens(model, write1h: tokens),
            _ => throw new ArgumentOutOfRangeException(nameof(tokenClass), tokenClass, message: "unknown token class"),
        };
    }

    // Published list prices, USD per million tokens (2026-11-23): model, class, price. Haiku 5.5 rows are the low tier.
    public static TheoryData<string, string, decimal> ListPrices
    {
        get
        {
            return new TheoryData<string, string, decimal>
            {
                { "claude-sonnet-5-5", "input", 2m },
                { "claude-sonnet-5-5", "output", 10m },
                { "claude-sonnet-5-5", "cacheRead", 0.10m },
                { "claude-sonnet-5-5", "write5m", 2.50m },
                { "claude-sonnet-5-5", "write1h", 4m },
                { "claude-sonnet-5", "input", 2m },
                { "claude-sonnet-5", "output", 10m },
                { "claude-sonnet-5", "cacheRead", 0.20m },
                { "claude-sonnet-5", "write5m", 2.50m },
                { "claude-sonnet-5", "write1h", 4m },
                { Haiku55, "input", 0.10m },
                { Haiku55, "output", 0.50m },
                { Haiku55, "cacheRead", 0.01m },
                { Haiku55, "write5m", 0.125m },
                { Haiku55, "write1h", 0.20m },
                { "claude-haiku-4-5", "input", 1m },
                { "claude-haiku-4-5", "output", 5m },
                { "claude-haiku-4-5", "cacheRead", 0.10m },
                { "claude-haiku-4-5", "write5m", 1.25m },
                { "claude-haiku-4-5", "write1h", 2m },
            };
        }
    }

    [Theory]
    [MemberData(nameof(ListPrices))]
    public void Every_listed_anthropic_entry_costs_its_published_price_per_token_class(string model, string tokenClass, decimal pricePerMillion) =>
        Assert.Equal(pricePerMillion * 1_000m / 1_000_000m, Entry(model).Cost(OnlyClass(model, tokenClass, 1_000)));

    [Fact]
    public void Sonnet_5_5_has_its_own_entry_and_sonnet_5_keeps_its_own()
    {
        PriceBook book = Shipped();
        Assert.Equal("anthropic/claude-sonnet-5-5", book.Find("anthropic", "claude-sonnet-5-5")?.Key);
        Assert.Equal("anthropic/claude-sonnet-5-5", book.Find("anthropic", "claude-sonnet-5-5-20261101")?.Key);
        Assert.Equal("anthropic/claude-sonnet-5", book.Find("anthropic", "claude-sonnet-5")?.Key);
        Assert.Equal("anthropic/claude-sonnet-5", book.Find("anthropic", "claude-sonnet-5-20260901")?.Key);
    }

    [Fact]
    public void Sonnet_5_5_cache_read_costs_ten_cents_and_sonnet_5_twenty()
    {
        Assert.Equal(0.10m, Entry("claude-sonnet-5-5").Cost(Tokens("claude-sonnet-5-5", cacheRead: 1_000_000)));
        Assert.Equal(0.20m, Entry("claude-sonnet-5").Cost(Tokens("claude-sonnet-5", cacheRead: 1_000_000)));
    }

    [Fact]
    public void Sonnet_5_5_costs_list_price_for_every_token_class()
    {
        Assert.Equal(2 + 10 + 0.10m + 2.5m + 4, Entry("claude-sonnet-5-5").Cost(OneMillionOfEachClass("claude-sonnet-5-5")));
        Assert.Equal(2 + 10 + 0.20m + 2.5m + 4, Entry("claude-sonnet-5").Cost(OneMillionOfEachClass("claude-sonnet-5")));
    }

    [Fact]
    public void Haiku_5_5_has_its_own_entry_and_a_dated_id_matches_it()
    {
        PriceBook book = Shipped();
        Assert.Equal("anthropic/claude-haiku-5-5", book.Find("anthropic", Haiku55)?.Key);
        Assert.Equal("anthropic/claude-haiku-5-5", book.Find("anthropic", "claude-haiku-5-5-20261101")?.Key);
    }

    [Fact]
    public void Haiku_5_5_is_not_priced_as_dearest()
    {
        ModelPrice price = Shipped().Resolve("anthropic", Haiku55, out string? note);
        Assert.Null(note);
        Assert.Equal("anthropic/claude-haiku-5-5", price.Key);
    }

    [Fact]
    public void Haiku_4_5_is_still_priced_by_its_own_entry()
    {
        PriceBook book = Shipped();
        ModelPrice price = Entry("claude-haiku-4-5");
        Assert.Equal("anthropic/claude-haiku-4-5", price.Key);
        Assert.Equal("anthropic/claude-haiku-4-5", book.Find("anthropic", "claude-haiku-4-5-20251001")?.Key);
        Assert.Equal(1 + 5 + 0.10m + 1.25m + 2, price.Cost(OneMillionOfEachClass("claude-haiku-4-5")));
    }

    [Fact]
    public void Haiku_5_5_prices_a_prompt_of_exactly_100000_tokens_at_the_low_tier()
    {
        // Prompt 40,000 + 50,000 + 6,000 + 4,000 = 100,000, not over the threshold. Output 10,000.
        Usage usage = Tokens(Haiku55, input: 40_000, output: 10_000, cacheRead: 50_000, write5m: 6_000, write1h: 4_000);
        Assert.Equal(100_000, usage.Context);
        const decimal expected = ((40_000 * 0.10m) + (10_000 * 0.50m) + (50_000 * 0.01m) + (6_000 * 0.125m) + (4_000 * 0.20m)) / 1_000_000m;
        Assert.Equal(0.01105m, expected);
        Assert.Equal(expected, Entry(Haiku55).Cost(usage));
    }

    [Fact]
    public void Haiku_5_5_prices_a_prompt_of_100001_tokens_entirely_at_the_high_tier()
    {
        // The same request with one more input token: prompt 100,001.
        Usage usage = Tokens(Haiku55, input: 40_001, output: 10_000, cacheRead: 50_000, write5m: 6_000, write1h: 4_000);
        Assert.Equal(100_001, usage.Context);
        const decimal expected = ((40_001 * 0.50m) + (10_000 * 2.50m) + (50_000 * 0.05m) + (6_000 * 0.625m) + (4_000 * 1m)) / 1_000_000m;
        Assert.Equal(0.0552505m, expected);
        Assert.Equal(expected, Entry(Haiku55).Cost(usage));
    }

    // One class per request, so a wrong fallback in a single class shows. Each request has a prompt of 100,001.
    public static TheoryData<string, decimal> HighTierPromptPrices
    {
        get
        {
            return new TheoryData<string, decimal>
            {
                { "input", 0.50m },
                { "cacheRead", 0.05m },
                { "write5m", 0.625m },
                { "write1h", 1m },
            };
        }
    }

    [Theory]
    [MemberData(nameof(HighTierPromptPrices))]
    public void Haiku_5_5_high_tier_prices_each_prompt_class_at_its_published_price(string tokenClass, decimal pricePerMillion)
    {
        Usage usage = OnlyClass(Haiku55, tokenClass, 100_001);
        Assert.Equal(100_001, usage.Context);
        Assert.Equal(pricePerMillion * 100_001m / 1_000_000m, Entry(Haiku55).Cost(usage));
    }

    [Fact]
    public void Haiku_5_5_high_tier_prices_output_at_2_50()
    {
        // Prompt 100,001 puts the request in the high tier; a million output tokens cost 2.50 there.
        Usage usage = Tokens(Haiku55, input: 100_001, output: 1_000_000);
        Assert.Equal((100_001 * 0.50m / 1_000_000m) + 2.50m, Entry(Haiku55).Cost(usage));
    }

    [Fact]
    public void Haiku_5_5_threshold_counts_cache_reads_and_writes_not_only_input()
    {
        ModelPrice price = Entry(Haiku55);
        Assert.Equal(((1 * 0.50m) + (100_000 * 0.05m)) / 1_000_000m, price.Cost(Tokens(Haiku55, input: 1, cacheRead: 100_000)));
        Assert.Equal(((1 * 0.50m) + (100_000 * 0.625m)) / 1_000_000m, price.Cost(Tokens(Haiku55, input: 1, write5m: 100_000)));
        Assert.Equal(((1 * 0.50m) + (100_000 * 1m)) / 1_000_000m, price.Cost(Tokens(Haiku55, input: 1, write1h: 100_000)));
    }

    [Fact]
    public void Haiku_5_5_threshold_does_not_count_output()
    {
        // Prompt 100,000 with a million output tokens stays at the low tier.
        Usage usage = Tokens(Haiku55, input: 100_000, output: 1_000_000);
        Assert.Equal(((100_000 * 0.10m) + (1_000_000 * 0.50m)) / 1_000_000m, Entry(Haiku55).Cost(usage));
    }

    [Theory]
    [InlineData("haiku", "claude-haiku-5-5")]
    [InlineData("Haiku", "claude-haiku-5-5")]
    [InlineData("haiku[1m]", "claude-haiku-5-5")]
    [InlineData("sonnet", "claude-sonnet-5-5")]
    [InlineData("Sonnet", "claude-sonnet-5-5")]
    [InlineData("sonnet[1m]", "claude-sonnet-5-5")]
    public void The_haiku_and_sonnet_aliases_resolve_to_5_5(string alias, string expected) =>
        Assert.Equal(expected, Shipped().ResolveAlias("anthropic", alias));
}
