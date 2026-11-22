using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The tool name status.json stores comes from the worker's stream: at most 64 characters of
/// [A-Za-z0-9_.:/-], anything else replaced by `?`.
/// </summary>
public sealed class RunStatusToolNameTests : IDisposable
{
    private readonly StatusRig _rig = new(new OpencodeRuntime());

    public void Dispose() => _rig.Dispose();

    private string Stored(string tool)
    {
        _rig.Status.OnLine(StreamActivity.ToolStart(tool, "call-1"), usage: false);
        return _rig.Live()["last_tool"]!["name"]!.GetValue<string>();
    }

    [Theory]
    [InlineData("Bash", "Bash")]
    [InlineData("mcp__server__tool.name:v1/sub-path", "mcp__server__tool.name:v1/sub-path")]
    [InlineData("my tool", "my?tool")]
    [InlineData("   ", "???")]
    [InlineData("a\u001b[2Jb", "a??2Jb")]
    [InlineData("a\u001b]0;title\u0007b", "a??0?title?b")]
    [InlineData("tab\tnew\nline\r", "tab?new?line?")]
    [InlineData("nul\0del\u007f", "nul?del?")]
    [InlineData("c1\u0085\u009bx", "c1??x")]
    [InlineData("line\U00002028sep\U00002029", "line?sep?")]
    [InlineData("bidi\U0000202E\U00002066\U0000200F", "bidi???")]
    [InlineData("café", "caf?")]
    [InlineData("日本語", "???")]
    [InlineData("a~b@c\\d", "a?b?c?d")]
    public void A_tool_name_keeps_the_allowed_characters_and_replaces_every_other_one_with_a_question_mark(string tool, string expected) =>
        Assert.Equal(expected, Stored(tool));

    [Fact]
    public void A_name_of_64_characters_is_kept_whole_and_one_of_65_is_cut_to_64()
    {
        string at = new('a', 64);

        Assert.Equal(at, Stored(at));
        Assert.Equal(at, Stored(at + "a"));
        Assert.Equal(at, Stored(at + "\u001b[2J"));
    }

    [Fact]
    public void The_replacement_counts_toward_the_64_characters()
    {
        string stored = Stored(new string(' ', 80));

        Assert.Equal(new string('?', 64), stored);
    }

    [Fact]
    public void An_empty_name_is_stored_as_a_question_mark() =>
        Assert.Equal("?", Stored(string.Empty));

    [Theory]
    [InlineData("Bash")]
    [InlineData("a b")]
    [InlineData("\u001b[2J\u001b]0;title\u0007\r\nforged")]
    [InlineData("\U0001F600 emoji pair")]
    [InlineData("é")]
    [InlineData("")]
    public void Whatever_the_name_the_stored_one_is_one_to_64_characters_of_the_allowed_set_and_question_marks(string tool)
    {
        string stored = Stored(tool);

        Assert.InRange(stored.Length, 1, 64);
        Assert.All(stored, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '/' or '-' or '?'));
    }

    [Fact]
    public void A_tool_end_that_names_the_tool_is_stored_cleaned_too()
    {
        _rig.Status.OnLine(StreamActivity.ToolEnd("bad name\u001b[2J", "call-2", StreamActivity.Completed), usage: false);

        string stored = _rig.Live()["last_tool"]!["name"]!.GetValue<string>();

        Assert.Equal("bad?name??2J", stored);
    }

    [Fact]
    public void The_name_in_the_file_is_the_cleaned_one_not_a_copy_of_the_raw_text()
    {
        _ = Stored("evil\u001b[2J name");

        string raw = File.ReadAllText(Path.Combine(_rig.RunDir, RunStatus.FileName));

        Assert.DoesNotContain("evil\\u001b", raw, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', raw);
    }
}
