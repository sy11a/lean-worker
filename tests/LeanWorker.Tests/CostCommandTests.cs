using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The "cost" subcommand over a Claude transcript (a file or a session id under CLAUDE_CONFIG_DIR) and over an
/// opencode session read through a stub "opencode db" on PATH. Prices come from a fixture file.
/// </summary>
[Collection("launcher-process-state")]
public sealed class CostCommandTests : IDisposable
{
    // 1 USD per million input tokens, 2 per million output tokens.
    private const string Prices = /*lang=json,strict*/ """
        {"providers":{"testprov":{"billing":"metered"}},"models":{"testprov/m-test":{"input":1,"output":2}}}
        """;

    // m1 is reported twice (its usage grows; the merged record wins), m2 once; lines without usage, a broken line
    // and a usage on a non-assistant record count nothing.
    private const string Transcript = """
        {"type":"assistant","message":{"id":"m1","model":"m-test","usage":{"input_tokens":1000000,"output_tokens":0}}}
        {"type":"user","message":{"content":"no tokens here"}}
        {"type":"assistant","message":{"id":"m1","model":"m-test","usage":{"input_tokens":1000000,"output_tokens":500000}}}
        {"type":"assistant","message":{"id":"m2","model":"m-test","usage":{"input_tokens":0,"output_tokens":1000000}}}
        {"usage": broken
        {"type":"user","usage":{"input_tokens":5}}
        """;

    private readonly CommandSandbox _box = new();

    private string PricesFile => _box.WriteFile("fixture-prices.json", Prices);

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Cost_without_a_session_fails()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot])));

        Assert.Equal("cost needs --claude <session-id|file> or --opencode <session-id>", ex.Message);
    }

    [Fact]
    public void Cost_of_a_claude_transcript_file_counts_each_message_once_at_list_price()
    {
        string transcript = _box.WriteFile("t.jsonl", Transcript);

        int code = _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--prices", PricesFile, "--claude", transcript, "--provider", "testprov"]));

        Assert.Equal(0, code);
        Assert.Equal(
            [
                $"{transcript}: 2 API calls, $4.0000 at list price",
                "  testprov/m-test: 2 calls, 2,500,000 tokens, $4.0000 (metered)",
            ],
            _box.Lines);
    }

    [Fact]
    public void Cost_finds_a_claude_session_id_under_the_claude_config_dir_and_defaults_to_anthropic()
    {
        string transcript = _box.WriteFile(Path.Combine("claude", "projects", "-some-project", "abc-123.jsonl"),
            """{"type":"assistant","message":{"id":"m1","model":"no-such-model","usage":{"input_tokens":10,"output_tokens":10}}}""");

        _ = _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--prices", PricesFile, "--claude", "abc-123"]));

        string[] lines = _box.Lines;
        Assert.StartsWith($"{transcript}: 1 API calls, $", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  anthropic/no-such-model: 1 calls, 20 tokens, $", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("(subscription)", lines[1], StringComparison.Ordinal);
        Assert.Equal("  note: no price for anthropic/no-such-model; priced as dearest", lines[2]);
    }

    [Fact]
    public void Cost_of_an_unknown_claude_session_id_fails()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--claude", "missing-id"])));

        Assert.Equal($"no transcript missing-id.jsonl under {Path.Combine(_box.ClaudeConfigDir, "projects")}", ex.Message);
    }

    [Fact]
    public void Cost_rejects_an_opencode_session_id_of_the_wrong_shape()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--opencode", "x'; drop table message; --"])));

        Assert.Equal("not an opencode session id: x'; drop table message; --", ex.Message);
    }

    [Fact]
    public void Cost_of_an_opencode_session_fails_when_opencode_is_not_on_path()
    {
        _box.PathIsBinDirOnly();

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--opencode", "ses_abc"])));

        Assert.Equal("'opencode' is not on PATH.", ex.Message);
    }

    [Fact]
    public void Cost_of_an_opencode_session_reports_a_failing_opencode_db()
    {
        _box.Stub("opencode", "echo 'database is locked' >&2\nexit 3");

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--opencode", "ses_abc"])));

        Assert.Equal("opencode db failed: database is locked", ex.Message);
    }

    [Fact]
    public void Cost_of_an_opencode_session_prices_each_assistant_message_with_usage()
    {
        // a1 and a2 cost 2 USD each; the row without ids is priced as the dearest model at zero tokens; rows without
        // tokens, with non-object tokens, and non-object rows are skipped.
        string rows = _box.WriteFile("rows.json", """
            [
              {"id":"a1","provider":"testprov","model":"m-test","tokens":"{\"input\":1000000,\"output\":0,\"reasoning\":500000,\"cache\":{\"read\":0,\"write\":0}}"},
              {"id":"a2","provider":"testprov","model":"m-test","tokens":"{\"input\":0,\"output\":1000000}"},
              {"tokens":"{\"input\":0}"},
              {"id":"a3","provider":"testprov","model":"m-test"},
              {"id":"a4","provider":"testprov","model":"m-test","tokens":"[1]"},
              5
            ]
            """);
        _box.Stub("opencode", $"cat '{rows}'");

        int code = _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--prices", PricesFile, "--opencode", "ses_abc123"]));

        Assert.Equal(0, code);
        string[] lines = _box.Lines;
        Assert.Equal("opencode session ses_abc123: 3 API calls, $4.0000 at list price", lines[0]);
        Assert.Equal("  testprov/m-test: 2 calls, 2,500,000 tokens, $4.0000 (metered)", lines[1]);
        Assert.StartsWith("  /: 1 calls, 0 tokens, $0.0000", lines[2], StringComparison.Ordinal);
        Assert.Equal("  note: no price for /; priced as dearest", lines[3]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Cost_of_an_opencode_session_without_usage_fails(string output)
    {
        _box.Stub("opencode", $"echo '{output}'");

        LaunchException ex = Assert.Throws<LaunchException>(() => _box.Run(() => Commands.Cost(["--runs-root", _box.RunsRoot, "--opencode", "ses_empty"])));

        Assert.Equal("no assistant messages with usage in opencode session ses_empty", ex.Message);
    }
}
