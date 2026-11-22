using System.Globalization;
using System.Text;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// What the `status` output must never carry, written out from the list the sanitiser promises (and
/// not taken from the sanitiser): the C0 controls, DEL, the C1 controls, U+2028, U+2029, the bidi
/// controls (U+061C, U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069), the invisible format
/// characters (U+200B to U+200D, U+2060, U+FEFF) and a lone surrogate.
/// </summary>
internal static class TerminalAssert
{
    /// <summary>
    /// Whether a UTF-16 code unit is one the plain output must not carry.
    /// </summary>
    public static bool IsListedUnsafe(int code)
    {
        return code is <= 0x1F or (>= 0x7F and <= 0x9F) or 0x2028 or 0x2029 or 0x061C or 0x200B or 0x200C or 0x200D or 0x200E or 0x200F
            or (>= 0x202A and <= 0x202E) or 0x2060 or (>= 0x2066 and <= 0x2069) or 0xFEFF or (>= 0xD800 and <= 0xDFFF);
    }

    /// <summary>
    /// The characters of a plain output that must not be there: everything listed except the line
    /// feed that ends a line and a surrogate pair that is whole.
    /// </summary>
    public static List<string> PlainProblems(string text)
    {
        List<string> problems = [];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n')
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (IsListedUnsafe(c))
            {
                problems.Add(Describe(c, i));
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks a plain output (no listed character, no 0x1B, 0x7F or byte below 0x20 but the line
    /// feeds, in its UTF-8 bytes too) and that it is one header line plus <paramref name="runs"/> lines.
    /// </summary>
    public static string[] AssertPlain(string output, int runs)
    {
        Assert.Empty(PlainProblems(output));
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(output), b => b is (< 0x20 and not 0x0A) or 0x7F);
        string[] lines = output.Split('\n');
        Assert.Equal(string.Empty, lines[^1]);
        lines = lines[..^1];
        Assert.Equal(runs + 1, lines.Length);
        Assert.StartsWith("lean-worker status:", lines[0], StringComparison.Ordinal);
        return lines;
    }

    /// <summary>
    /// What a --json output must not carry: a raw ESC, a raw C1 control, a raw U+2028 or U+2029
    /// anywhere, and any raw control character inside a string.
    /// </summary>
    public static List<string> JsonProblems(string json)
    {
        List<string> problems = [];
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c is '\u001b' or (>= '\u0080' and <= '\u009f') or '\U00002028' or '\U00002029')
            {
                problems.Add("raw " + Describe(c, i));
            }
            else if (inString && c < ' ')
            {
                problems.Add("raw control in a string " + Describe(c, i));
            }

            if (!inString)
            {
                inString = c is '"';
            }
            else if (escaped)
            {
                escaped = false;
            }
            else if (c is '\\')
            {
                escaped = true;
            }
            else if (c is '"')
            {
                inString = false;
            }
        }

        return problems;
    }

    private static string Describe(char c, int index) =>
        "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + " at " + index.ToString(CultureInfo.InvariantCulture);
}
