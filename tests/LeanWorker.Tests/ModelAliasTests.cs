using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

[Collection("launcher-process-state")]
public class ModelAliasTests
{
    private static PriceBook Book(string models, string providers = "{}") =>
        PriceBook.FromJson(JsonNode.Parse($$"""{"providers":{{providers}},"rates":{"USD":1},"models":{{models}}}""")!.AsObject());

    private static string Keys(params string[] ids) =>
        "{" + string.Join(',', ids.Select(id => $"\"anthropic/{id}\":{{\"input\":1,\"output\":1}}")) + "}";

    // A price book whose newest sonnet (9-9) is higher than any shipped one, so the result never depends on prices.json.
    private const string RunBook = """
        {"rates":{"USD":1},
         "models":{"anthropic/claude-sonnet-9-9":{"input":1,"output":2},"anthropic/claude-sonnet-9":{"input":1,"output":2}},
         "modelTraits":{"claude-sonnet-9-9":{"note":"via alias"}}}
        """;

    private const string Sonnet99Stream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-sonnet-9-9","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
        """;

    private const string SameIdStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
        """;

    private static async Task<(string Stdout, JsonObject Summary, string Root)> RunAliasAsync(string script, Action<Options>? configure = null)
    {
        string root = RunAsyncGolden.NewRoot();
        string prices = Path.Combine(root, "alias-prices.json");
        await File.WriteAllTextAsync(prices, RunBook, TestContext.Current.CancellationToken);
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, script, o =>
        {
            o.Model = "sonnet";
            o.PricesFile = prices;
            configure?.Invoke(o);
        });
        return (stdout, RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout)), root);
    }

    // ---- A: PriceBook.ResolveAlias ----

    [Fact]
    public void ResolveAlias_returns_the_newest_of_several_keys()
    {
        PriceBook b = Book(Keys("claude-sonnet-4-5", "claude-sonnet-5", "claude-sonnet-4", "claude-opus-6"));
        Assert.Equal("claude-sonnet-5", b.ResolveAlias("anthropic", "sonnet"));
        Assert.Equal("claude-opus-6", b.ResolveAlias("anthropic", "opus"));
    }

    [Fact]
    public void ResolveAlias_a_longer_version_wins_a_tie_on_the_common_prefix()
    {
        Assert.Equal("claude-sonnet-5-5", Book(Keys("claude-sonnet-5", "claude-sonnet-5-5")).ResolveAlias("anthropic", "sonnet"));
        Assert.Equal("claude-haiku-4-5-20251001", Book(Keys("claude-haiku-4-5", "claude-haiku-4-5-20251001")).ResolveAlias("anthropic", "haiku"));
    }

    [Fact]
    public void ResolveAlias_compares_integers_not_text() =>
        Assert.Equal("claude-sonnet-10", Book(Keys("claude-sonnet-9", "claude-sonnet-10")).ResolveAlias("anthropic", "sonnet"));

    [Fact]
    public void ResolveAlias_returns_the_dated_id_when_it_is_the_newest() =>
        Assert.Equal("claude-haiku-4-5-20251001", Book(Keys("claude-haiku-3-5", "claude-haiku-4-5-20251001")).ResolveAlias("anthropic", "haiku"));

    [Theory]
    [InlineData("Sonnet")]
    [InlineData("SONNET")]
    [InlineData("sonnet[1m]")]
    [InlineData("Sonnet[1M]")]
    public void ResolveAlias_is_case_insensitive_and_drops_the_bracket_suffix(string alias) =>
        Assert.Equal("claude-sonnet-5-5", Book(Keys("claude-sonnet-5", "claude-sonnet-5-5")).ResolveAlias("anthropic", alias));

    [Fact]
    public void ResolveAlias_leaves_a_non_anthropic_provider_unchanged()
    {
        PriceBook b = Book(Keys("claude-sonnet-5"), """{"minimax":{}}""");
        Assert.Equal("sonnet", b.ResolveAlias("minimax", "sonnet"));
    }

    [Fact]
    public void ResolveAlias_follows_a_provider_priced_as_anthropic()
    {
        PriceBook b = Book(Keys("claude-sonnet-5"), """{"gw":{"priceAs":"anthropic"}}""");
        Assert.Equal("claude-sonnet-5", b.ResolveAlias("gw", "sonnet"));
    }

    [Fact]
    public void ResolveAlias_returns_the_input_when_no_key_matches()
    {
        PriceBook b = Book(Keys("claude-opus-5-5"));
        Assert.Equal("sonnet", b.ResolveAlias("anthropic", "sonnet"));
        Assert.Equal("sonnet[1m]", b.ResolveAlias("anthropic", "sonnet[1m]"));
    }

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-haiku-4-5-20251001")]
    [InlineData("MiniMax-M3")]
    [InlineData("sonnet-x")]
    public void ResolveAlias_returns_a_non_alias_unchanged(string model) =>
        Assert.Equal(model, Book(Keys("claude-sonnet-5", "claude-sonnet-5-5")).ResolveAlias("anthropic", model));

    [Fact]
    public void ResolveAlias_skips_a_key_with_a_non_integer_part()
    {
        PriceBook b = Book(Keys("claude-sonnet-4", "claude-sonnet-5-beta", "claude-sonnet-latest", "claude-sonnet-6-x"));
        Assert.Equal("claude-sonnet-4", b.ResolveAlias("anthropic", "sonnet"));
    }

    [Fact]
    public void IsAlias_knows_the_four_families_with_an_optional_suffix()
    {
        Assert.True(PriceBook.IsAlias("sonnet"));
        Assert.True(PriceBook.IsAlias("OPUS"));
        Assert.True(PriceBook.IsAlias("haiku[1m]"));
        Assert.True(PriceBook.IsAlias("fable"));
        Assert.False(PriceBook.IsAlias("claude-sonnet-5"));
        Assert.False(PriceBook.IsAlias("sonnets"));
        Assert.False(PriceBook.IsAlias("MiniMax-M3"));
    }

    // ---- B: used before launch ----

    [Fact]
    public async Task Alias_run_has_the_alias_note_no_priced_as_note_and_the_traits_of_the_resolved_idAsync()
    {
        (_, JsonObject s, _) = await RunAliasAsync(Sonnet99Stream);
        string[] notes = RunAsyncGolden.Notes_(s);

        Assert.Contains("model alias sonnet -> claude-sonnet-9-9 (price book)", notes, StringComparer.Ordinal);
        Assert.DoesNotContain(notes, n => n.Contains("priced as", StringComparison.Ordinal));
        Assert.Equal("claude-sonnet-9-9", Json.Str(s, "model_traits"));
    }

    [Fact]
    public async Task Alias_run_does_not_fail_under_unknownModel_errorAsync()
    {
        (_, JsonObject s, _) = await RunAliasAsync(Sonnet99Stream, o =>
        {
            string prices = o.PricesFile!;
            File.WriteAllText(prices, RunBook.Replace("{\"rates\"", "{\"unknownModel\":\"error\",\"rates\"", StringComparison.Ordinal));
        });
        Assert.Equal("success", Json.Str(s, "status"));
    }

    [Fact]
    public async Task Alias_is_still_what_is_passed_to_claudeAsync()
    {
        (string stdout, _, _) = await RunAliasAsync(Sonnet99Stream);
        string command = await File.ReadAllTextAsync(Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "command.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("--model sonnet ", command, StringComparison.Ordinal);
    }

    // ---- C: runtime guard ----

    [Fact]
    public async Task Alias_with_the_opencode_runtime_is_a_launch_error_naming_the_aliasAsync()
    {
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAliasAsync("exit 0", o => o.Runtime = "opencode"));
        Assert.Contains("model alias sonnet works only with the claude runtime; name the model id", ex.Message, StringComparison.Ordinal);
    }

    // ---- D: the record names the model that ran ----

    [Fact]
    public async Task Record_model_is_the_reported_id_and_model_requested_the_aliasAsync()
    {
        (string stdout, JsonObject s, string root) = await RunAliasAsync(Sonnet99Stream);

        Assert.Equal("claude-sonnet-9-9", Json.Str(s, "model"));
        Assert.Equal("sonnet", Json.Str(s, "model_requested"));
        RunAsyncGolden.AssertLedgerHasOneRowEqualTo(root, s);
        Assert.Contains("anthropic/claude-sonnet-9-9 (alias sonnet)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Record_has_no_model_requested_when_the_reported_id_equals_the_requested_oneAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, SameIdStream);
        JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));

        Assert.Equal("claude-haiku-4-5", Json.Str(s, "model"));
        Assert.False(s.ContainsKey("model_requested"));
        Assert.DoesNotContain("(alias", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Record_has_no_model_requested_when_nothing_was_reportedAsync()
    {
        (string stdout, JsonObject s, _) = await RunAliasAsync("exit 0");

        Assert.Equal("sonnet", Json.Str(s, "model"));
        Assert.False(s.ContainsKey("model_requested"));
        Assert.DoesNotContain("(alias", stdout, StringComparison.Ordinal);
    }

    private const string DatedHaikuStream = """
        printf '%s\n' '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5-20251001","usage":{"input_tokens":1000,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}}'
        printf '%s\n' '{"type":"result","subtype":"success","terminal_reason":"completed","is_error":false,"result":"DONE","session_id":"s1","total_cost_usd":0.01,"num_turns":1,"permission_denials":[]}'
        """;

    [Fact]
    public async Task Non_alias_model_reported_with_a_dated_id_has_no_model_requested_and_no_alias_textAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        (_, string stdout) = await RunAsyncGolden.RunAsync(root, DatedHaikuStream, o => o.Model = "claude-haiku-4-5");
        JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));

        Assert.Equal("claude-haiku-4-5", Json.Str(s, "model"));
        Assert.False(s.ContainsKey("model_requested"));
        Assert.DoesNotContain("(alias", stdout, StringComparison.Ordinal);
    }

    // ---- chains ----

    [Fact]
    public async Task Chain_with_an_alias_entry_picks_the_alias_and_records_the_reported_idAsync()
    {
        string root = RunAsyncGolden.NewRoot();
        string prices = Path.Combine(root, "alias-prices.json");
        await File.WriteAllTextAsync(prices, RunBook, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "profiles.json"),
            """{"profiles":{"chained":{"model":["sonnet","anthropic/claude-haiku-4-5"]}}}""", TestContext.Current.CancellationToken);

        (_, string stdout) = await RunAsyncGolden.RunAsync(root, Sonnet99Stream, o =>
        {
            o.Model = null;
            o.Profile = "chained";
            o.PricesFile = prices;
        });
        JsonObject s = RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout));

        Assert.Equal("claude-sonnet-9-9", Json.Str(s, "model"));
        Assert.Equal("sonnet", Json.Str(s, "model_requested"));
        Assert.Contains("model alias sonnet -> claude-sonnet-9-9 (price book)", RunAsyncGolden.Notes_(s), StringComparer.Ordinal);
    }

    [Fact]
    public void NextInChain_returns_a_trailing_alias_as_the_next_model()
    {
        List<string> chain = ["minimax/MiniMax-M3", "anthropic/claude-haiku-4-5", "sonnet"];
        Assert.Equal("sonnet", Launcher.NextInChain(chain, "anthropic", "claude-haiku-4-5"));
        Assert.Null(Launcher.NextInChain(chain, "anthropic", "sonnet"));
    }

    // ---- IsAlias edges ----

    [Theory]
    [InlineData("sonnet[1m]", true)]
    [InlineData("sonnet[1m", false)]
    [InlineData("sonnet[x]y", false)]
    [InlineData("Sonnet", true)]
    [InlineData("sonnet-5", false)]
    public void IsAlias_edges(string model, bool expected) =>
        Assert.Equal(expected, PriceBook.IsAlias(model));

    // ---- opencode with a model literally named like an alias ----

    [Fact]
    public async Task Opencode_with_a_non_anthropic_model_named_opus_is_not_an_alias_errorAsync()
    {
        string? message = null;
        try
        {
            _ = await RunAsyncGolden.RunAsync(RunAsyncGolden.NewRoot(), "exit 0", o =>
            {
                o.Model = "minimax/opus";
                o.Runtime = "opencode";
            });
        }
        catch (LaunchException ex)
        {
            message = ex.Message;
        }

        Assert.DoesNotContain("model alias", message ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- signed version parts ----

    [Fact]
    public void ResolveAlias_skips_a_key_with_a_signed_version_part()
    {
        PriceBook b = Book(Keys("claude-sonnet-4", "claude-sonnet-+9", "claude-sonnet-5--1"));
        Assert.Equal("claude-sonnet-4", b.ResolveAlias("anthropic", "sonnet"));
    }

    // ---- E: defaults ----

    [Fact]
    public async Task Default_model_without_a_profile_or_option_is_the_sonnet_aliasAsync()
    {
        (_, JsonObject s, _) = await RunAliasAsync(Sonnet99Stream, o => o.Model = null);
        Assert.Equal("sonnet", Json.Str(s, "model_requested"));
        Assert.Equal("claude-sonnet-9-9", Json.Str(s, "model"));
    }

    [Theory]
    [InlineData("profiles.json")]
    [InlineData("profiles-chains.json")]
    public void Templates_name_family_aliases_not_versioned_claude_ids(string file)
    {
        string text = File.ReadAllText(Path.Combine(TemplatesDir(), file));
        Assert.DoesNotContain("claude-sonnet-5\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-opus-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-haiku-", text, StringComparison.Ordinal);
    }

    private static string TemplatesDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "templates")))
        {
            d = d.Parent;
        }

        return Path.Combine(d!.FullName, "skills", "lean-worker", "templates");
    }
}
