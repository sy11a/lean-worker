using System.Globalization;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// How many columns the watch table counts a character for, and what it draws (the review of #147, finding 9): the
/// symbols, dingbats and emoji of U+2300 to U+2BFF count 2 columns (some terminals draw them as two-column emoji; counting
/// two is safe), so a name of them never makes a row longer than the window; a character a terminal draws in no column
/// of its own (categories Mn, Me and Cf, also beyond the BMP) is drawn as <c>?</c>, one column, so the columns stay in
/// line; ASCII, Latin and Cyrillic names are untouched; and the one-shot listing, which does not fit to a width, prints
/// every name as it did. Characters are written as 8-digit escapes, so none of them is in this source.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusTableWidthTests : IDisposable
{
    private static readonly int[] _widths = [54, 60, 80, 100, 121, 160];

    private readonly StatusLiveRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // What a test counts as drawn in one column or two: the wide ranges the table knows (U+2300 to U+2BFF among them),
    // and a surrogate pair, which is an emoji or a wide letter.
    private static int Drawn(string text)
    {
        int columns = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                columns += 2;
            }
            else
            {
                columns += StatusTableRows.Columns(text[i].ToString()) is 2 ? 2 : 1;
            }
        }

        return columns;
    }

    private static bool DrawsNothing(char c)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;
    }

    private static string TooLong(int width, string line) =>
        string.Create(CultureInfo.InvariantCulture, $"width {width}: '{StatusTableRows.Plain(line)}' is {StatusTableRows.Columns(line)} columns");

    // The review's examples: a watch, a high-voltage sign, a check mark and a cross mark, which terminals draw as emoji of
    // two columns, then a star, an alarm clock and a black large square.
    [Theory]
    [InlineData("\U0000231A")]
    [InlineData("\U000026A1")]
    [InlineData("\U00002705")]
    [InlineData("\U0000274C")]
    [InlineData("\U00002B50")]
    [InlineData("\U000023F0")]
    [InlineData("\U00002B1B")]
    public void A_name_of_28_wide_symbols_never_makes_a_row_longer_than_the_window(string symbol)
    {
        string name = string.Concat(Enumerable.Repeat(symbol, 28));

        foreach (int width in _widths)
        {
            foreach (bool owner in new[] { false, true })
            {
                List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([StatusTableRows.Running(name), StatusTableRows.Ended(name)], owner: owner), width);

                Assert.Equal(4, frame.Count);
                Assert.All(frame, line => Assert.True(StatusTableRows.Columns(line) <= width, TooLong(width, line)));
                Assert.Contains(symbol, frame[2], StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("\U0000231A")]
    [InlineData("\U000026A1")]
    [InlineData("\U00002705")]
    [InlineData("\U0000274C")]
    public void A_cell_of_wide_symbols_has_exactly_the_width_it_is_given_whatever_that_width_is(string symbol)
    {
        string name = string.Concat(Enumerable.Repeat(symbol, 28));

        for (int width = 1; width <= 40; width++)
        {
            Assert.Equal(width, Drawn(StatusTable.Fit(name, width, right: false)));
            Assert.Equal(width, Drawn(StatusTable.Fit(name, width, right: true)));
        }
    }

    [Fact]
    public void A_cut_wide_name_ends_in_the_cut_mark_and_two_symbols_in_four_columns_are_not_cut()
    {
        const string watch = "\U0000231A";

        Assert.Equal(watch + watch + "~", StatusTable.Fit(watch + watch + watch, 5, right: false));
        Assert.Equal(watch + watch, StatusTable.Fit(watch + watch, 4, right: false));
        Assert.Equal("a~ ", StatusTable.Fit("a" + watch + "b", 3, right: false));
        Assert.Equal("  \U00002705", StatusTable.Fit("\U00002705", 4, right: true));
    }

    [Theory]
    [InlineData("\U00002300")]
    [InlineData("\U00002BFF")]
    [InlineData("\U00002605")]
    public void The_whole_range_from_U2300_to_U2BFF_is_two_columns_and_its_neighbours_are_one(string inside)
    {
        Assert.Equal(2, Drawn(inside));
        Assert.Equal(3, Drawn("a" + inside));
        Assert.Equal("\U000022FF ", StatusTable.Fit("\U000022FF", 2, right: false));
        Assert.Equal("\U00002C00 ", StatusTable.Fit("\U00002C00", 2, right: false));
        Assert.Equal(inside, StatusTable.Fit(inside, 2, right: false));
    }

    // Mn, Me and Cf: a combining acute, an enclosing circle, a soft hyphen, a variation selector, a zero-width joiner; beyond
    // the BMP a musical combining mark, a variation selector of the supplement and a tag character.
    [Theory]
    [InlineData("e\U00000301x", "e?x     ")]
    [InlineData("\U000020DD", "?       ")]
    [InlineData("a\U000000ADb", "a?b     ")]
    [InlineData("\U0000FE0F", "?       ")]
    [InlineData("a\U0000200Db", "a?b     ")]
    [InlineData("a\U0001D167b", "a?b     ")]
    [InlineData("\U000E0100", "?       ")]
    [InlineData("a\U000E0001b", "a?b     ")]
    [InlineData("\U00002764\U0000FE0F", "\U00002764?     ")]
    public void A_character_a_terminal_draws_in_no_column_of_its_own_is_drawn_as_a_question_mark_in_one_column(string name, string cell)
    {
        Assert.Equal(cell, StatusTable.Fit(name, 8, right: false));
        Assert.Equal(8, Drawn(StatusTable.Fit(name, 8, right: false)));
    }

    [Fact]
    public void A_name_of_28_combining_sequences_keeps_every_row_inside_the_window_and_draws_none_of_the_marks()
    {
        string name = string.Concat(Enumerable.Repeat("e\U00000301", 28));

        foreach (int width in _widths)
        {
            List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([StatusTableRows.Running(name)]), width);

            Assert.All(frame, line => Assert.True(StatusTableRows.Columns(line) <= width, TooLong(width, line)));
            Assert.All(frame, line => Assert.DoesNotContain(line, c => DrawsNothing(c)));
            Assert.Contains("e?e?", frame[2], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("fix-save-w1")]
    [InlineData("docs-install-w1")]
    [InlineData("caf\U000000E9-d\U000000E9j\U000000E0")]
    [InlineData("\U000000DCn\U000000EFc\U000000F6d\U000000E9")]
    [InlineData("\U0000041F\U00000440\U00000438\U00000432\U00000435\U00000442-\U0000043C\U00000438\U00000440")]
    [InlineData("\U00000416\U00000443\U00000440\U0000043D\U00000430\U0000043B-w1")]
    public void ASCII_Latin_and_Cyrillic_names_are_drawn_as_they_are(string name)
    {
        Assert.Equal(name.PadRight(30), StatusTable.Fit(name, 30, right: false));
        Assert.Equal(name.PadLeft(30), StatusTable.Fit(name, 30, right: true));

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([StatusTableRows.Running(name)]), 100);

        Assert.Contains(name, frame[2], StringComparison.Ordinal);
        Assert.DoesNotContain('?', frame[2]);
    }

    [Fact]
    public void CJK_names_still_count_two_columns_a_character()
    {
        const string nihon = "\U000065E5\U0000672C";
        const string nihongo = nihon + "\U00008A9E";

        Assert.Equal(nihongo + "  ", StatusTable.Fit(nihongo, 8, right: false));
        Assert.Equal(nihon + "~", StatusTable.Fit(nihongo, 5, right: false));
    }

    [Fact]
    public async Task The_one_shot_listing_prints_every_name_as_it_always_didAsync()
    {
        string[] names =
        [
            "ascii-name",
            "caf\U000000E9-d\U000000E9j\U000000E0",
            "\U0000041F\U00000440\U00000438\U00000432\U00000435\U00000442-\U0000043C\U00000438\U00000440",
            "e\U00000301-\U0000231A-\U000026A1-\U00002705-\U0000274C",
            "a\U0001D167b-\U000000AD-\U0000FE0F-\U000E0100",
        ];
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            _ = _rig.Add("run-" + i.ToString(CultureInfo.InvariantCulture), id: string.Create(CultureInfo.InvariantCulture, $"20261122-1155{i:00}-name-{i}"),
                tweak: d => d["run"]!["name"] = name);
        }

        // Each call of the rig's command writes to the same writer, so these two go through the index rig's own.
        (int code, string plain) = await _rig.Idx.CallAsync("--owner", StatusLiveRig.Key);
        (int jsonCode, string json) = await _rig.Idx.CallAsync("--owner", StatusLiveRig.Key, "--json");

        Assert.Equal(0, code);
        Assert.Equal(0, jsonCode);
        foreach (string name in names)
        {
            Assert.Contains(StatusIndexRig.Lines(plain), l => l.Contains(name, StringComparison.Ordinal));
        }

        Assert.Equal([.. names.Order(StringComparer.Ordinal)], [.. StatusIndexRig.JsonNames(json).Order(StringComparer.Ordinal)]);
    }
}
