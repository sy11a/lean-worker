using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The sanitiser of the `status` output, against the list it promises: every C0 control, DEL, every C1
/// control, U+2028, U+2029, the bidi controls and a lone surrogate become `?`; a cell longer than its
/// limit is cut with a trailing `~` and never splits a surrogate pair; ordinary text passes unchanged.
/// </summary>
public sealed class TerminalTextTests
{
    private const int Wide = 1000;

    private const string Smile = "\U0001F600";

    private static readonly int[] _unsafeList =
    [
        .. Enumerable.Range(0, 0x10000).Where(code => TerminalAssert.IsListedUnsafe(code) && code is not (>= 0xD800 and <= 0xDFFF)),
    ];

    public static TheoryData<int> UnsafeCodes => [.. _unsafeList];

    [Fact]
    public void The_unsafe_theory_covers_every_range_end()
    {
        int[] codes = _unsafeList;

        int[] ends = [0x00, 0x1F, 0x7F, 0x80, 0x9F, 0x2028, 0x2029, 0x200E, 0x200F, 0x202A, 0x202E, 0x2066, 0x2069];
        Assert.All(ends, e => Assert.Contains(e, codes));
        Assert.Equal(32 + 33 + 2 + 2 + 5 + 4, codes.Length);
    }

    [Theory]
    [MemberData(nameof(UnsafeCodes))]
    public void A_control_or_bidi_character_becomes_a_question_mark(int code)
    {
        string c = ((char)code).ToString();

        Assert.Equal("a?b", TerminalText.Clean("a" + c + "b", Wide));
        Assert.Equal("?", TerminalText.Clean(c, Wide));
        Assert.Equal("??", TerminalText.Clean(c + c, Wide));
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x7E)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0xE9)]
    [InlineData(0x200B)]
    [InlineData(0x200D)]
    [InlineData(0x2027)]
    [InlineData(0x202F)]
    [InlineData(0x2065)]
    [InlineData(0x206A)]
    [InlineData(0x3042)]
    [InlineData(0xFEFF)]
    public void A_character_next_to_a_listed_range_is_left_alone(int code)
    {
        string c = ((char)code).ToString();

        Assert.Equal("a" + c + "b", TerminalText.Clean("a" + c + "b", Wide));
    }

    [Fact]
    public void Every_single_code_unit_is_either_kept_or_a_question_mark_as_the_list_says()
    {
        for (int code = 0; code <= 0xFFFF; code++)
        {
            string expected = TerminalAssert.IsListedUnsafe(code) ? "?" : ((char)code).ToString();

            Assert.Equal(expected, TerminalText.Clean(((char)code).ToString(), 4));
        }
    }

    [Theory]
    [InlineData("café")]
    [InlineData("ÀÉÎÕÜ ñ ß ø å")]
    [InlineData("日本語のテキスト")]
    [InlineData("中文，繁體")]
    [InlineData("Привет, мир")]
    [InlineData("Ελληνικά עברית")]
    [InlineData("a \U0001F600 b \U0001F1E9\U0001F1EA")]
    [InlineData("plain: [x] {y} ~ ? / \\ \" ' $ %")]
    [InlineData("  leading and trailing spaces  ")]
    public void Ordinary_text_passes_unchanged(string text) =>
        Assert.Equal(text, TerminalText.Clean(text, Wide));

    [Theory]
    [InlineData("abcde", 5, "abcde")]
    [InlineData("abcd", 5, "abcd")]
    [InlineData("abcdef", 5, "abcd~")]
    [InlineData("abcdefghijklmnop", 5, "abcd~")]
    [InlineData("a", 1, "a")]
    [InlineData("ab", 1, "~")]
    [InlineData("ab", 2, "ab")]
    [InlineData("abc", 2, "a~")]
    [InlineData("日本語日本語", 4, "日本語~")]
    public void A_value_over_its_limit_is_cut_with_a_trailing_tilde(string text, int max, string expected) =>
        Assert.Equal(expected, TerminalText.Clean(text, max));

    [Fact]
    public void A_cut_value_is_exactly_the_limit_long_and_a_shorter_one_is_not_padded()
    {
        for (int max = 1; max <= 40; max++)
        {
            Assert.Equal(max, TerminalText.Clean(new string('x', max + 1), max).Length);
            Assert.Equal(new string('x', max), TerminalText.Clean(new string('x', max), max));
            Assert.Equal(new string('x', max - 1), TerminalText.Clean(new string('x', max - 1), max));
        }
    }

    [Fact]
    public void A_character_replaced_inside_the_kept_part_is_replaced_and_one_past_the_cut_is_gone()
    {
        Assert.Equal("a?cd~", TerminalText.Clean("a\u001bcdefg", 5));
        Assert.Equal("abcd~", TerminalText.Clean("abcdef\u001b[2J", 5));
        Assert.Equal("abcd?", TerminalText.Clean("abcd\u001b", 5));
    }

    [Theory]
    [InlineData("abc" + Smile + "def", 5, "abc~")]
    [InlineData("abc" + Smile + "def", 6, "abc" + Smile + "~")]
    [InlineData("ab" + Smile + "c", 4, "ab~")]
    [InlineData("ab" + Smile, 4, "ab" + Smile)]
    [InlineData(Smile + "x", 2, "~")]
    [InlineData(Smile + "x", 3, Smile + "x")]
    public void A_surrogate_pair_at_the_cut_is_dropped_whole_never_split(string text, int max, string expected) =>
        Assert.Equal(expected, TerminalText.Clean(text, max));

    [Fact]
    public void However_it_is_cut_the_result_has_no_lone_surrogate_and_is_not_longer_than_the_limit()
    {
        foreach (string prefix in new[] { string.Empty, "a", "ab", "abc" })
        {
            string text = prefix + string.Concat(Enumerable.Repeat(Smile, 6)) + "tail";
            for (int max = 1; max <= text.Length + 2; max++)
            {
                string result = TerminalText.Clean(text, max);

                Assert.True(result.Length <= max);
                Assert.Empty(TerminalAssert.PlainProblems(result));
            }
        }
    }

    // Not InlineData: a lone surrogate in an attribute argument is turned into U+FFFD by the UTF-8 metadata.
    public static TheoryData<string, string> LoneSurrogates
    {
        get
        {
            const char high = '\ud83d';
            const char low = '\ude00';
            return new()
            {
                { "a" + high + "z", "a?z" },
                { "a" + low + "z", "a?z" },
                { "ab" + high, "ab?" },
                { high.ToString(), "?" },
                { low.ToString(), "?" },
                { $"{low}{high}", "??" },
                { $"{high}{high}{low}", "?" + Smile },
                { $"{high}x{low}", "?x?" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public void A_lone_high_or_low_surrogate_becomes_a_question_mark(string text, string expected) =>
        Assert.Equal(expected, TerminalText.Clean(text, Wide));

    [Fact]
    public void Empty_and_null_input_give_an_empty_string()
    {
        Assert.Equal(string.Empty, TerminalText.Clean(string.Empty, 5));
        Assert.Equal(string.Empty, TerminalText.Clean(text: null, max: 5));
    }

    [Fact]
    public void An_escape_sequence_a_line_break_and_a_bidi_override_leave_one_harmless_line()
    {
        const string hostile = "a\u001b[2J\u001b]0;title\u0007\r\nforged\U0000202Eb";

        string clean = TerminalText.Clean(hostile, Wide);

        Assert.Equal("a?[2J?]0;title???forged?b", clean);
    }
}
