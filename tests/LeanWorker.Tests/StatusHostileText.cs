namespace LeanWorker.Tests;

/// <summary>
/// One hostile string for the `status` tests, built from character codes so that no control or bidi character is in
/// the source: escape sequences (a screen clear, a cursor move, a reset, bold, an OSC title ended by BEL, an OSC link ended by
/// ST), the C1 controls CSI and OSC, the next-line control, the bidi controls, a zero-width space, the line and paragraph
/// separators, carriage return and line feed, and bells.
/// </summary>
internal static class StatusHostileText
{
    private const char Esc = '\u001b';
    private const char Bell = '\a';

    public static readonly string Value = string.Concat(
        "evil", Esc, "[2J", Esc, "[0m", Esc, "[1m", Esc, "[31;1H", Esc, "]0;pwned", Bell, Esc, "]8;;link", Esc, "\\",
        Code(0x9B), "2J", Code(0x9D), "0;x", Bell, Code(0x202E), Code(0x2066), Code(0x200B), "\r\n", "forged\n", Code(0x85), Code(0x2028), Code(0x2029), Bell);

    /// <summary>
    /// A shorter one for a value that is echoed in a message: digits first, so it passes for a number at a glance.
    /// </summary>
    public static readonly string Number = "5" + Esc + "[2J" + Esc + "]0;x" + Bell + Code(0x202E) + "\r\nSECRET";

    private static string Code(int c) => ((char)c).ToString();
}
