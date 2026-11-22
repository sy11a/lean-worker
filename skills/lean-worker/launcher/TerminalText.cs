// The one sanitiser for text the `status` command prints from files and directory names: the status
// files are written in a directory the worker can write, so a value in them must not reach the
// operator's terminal (or the orchestrator's context) as an escape sequence or a forged line.

using System.Text;

namespace LeanWorker;

internal static class TerminalText
{
    /// <summary>
    /// The character a control or bidi character is replaced by.
    /// </summary>
    public const char Replacement = '?';

    /// <summary>
    /// The mark at the end of a cut value.
    /// </summary>
    public const char CutMark = '~';

    /// <summary>
    /// <paramref name="text"/> safe to print on one line: every C0 control (tab, CR and LF included),
    /// DEL, every C1 control, the line and paragraph separators, the bidi controls, the invisible
    /// format characters and a lone surrogate become <see cref="Replacement"/>, and a value longer than <paramref name="max"/>
    /// is cut to <paramref name="max"/> characters, the last one <see cref="CutMark"/>.
    /// </summary>
    public static string Clean(string? text, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        bool cut = text.Length > max;
        int keep = cut ? max - 1 : text.Length;
        // Never split a surrogate pair at the cut.
        if (cut && keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        StringBuilder sb = new(keep + 1);
        for (int i = 0; i < keep; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < keep && char.IsLowSurrogate(text[i + 1]))
            {
                _ = sb.Append(c).Append(text[i + 1]);
                i++;
            }
            else
            {
                _ = sb.Append(Unsafe(c) ? Replacement : c);
            }
        }

        return cut ? sb.Append(CutMark).ToString() : sb.ToString();
    }

    /// <summary>
    /// Whether a character must not be printed as it is: a C0 control (U+0000 to U+001F), DEL
    /// (U+007F), a C1 control (U+0080 to U+009F), the line and paragraph separators (U+2028,
    /// U+2029), a bidi control (U+061C, U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069), an
    /// invisible format character a name can be spoofed with (U+200B to U+200D, U+2060, U+FEFF) or
    /// a lone surrogate.
    /// </summary>
    internal static bool Unsafe(char c)
    {
        int code = c;
        return code is <= 0x1F or (>= 0x7F and <= 0x9F) or 0x2028 or 0x2029 or 0x061C or (>= 0x200B and <= 0x200F)
            or (>= 0x202A and <= 0x202E) or 0x2060 or (>= 0x2066 and <= 0x2069) or 0xFEFF
            || char.IsSurrogate(c);
    }
}
