// One process's stat line from /proc, as the live status needs it: the state, the group, the
// session and the start time (clock ticks since boot), which together with the boot id tell a live
// process from a recycled number.

using System.Globalization;

namespace LeanWorker;

/// <summary>
/// The fields of a /proc/&lt;pid&gt;/stat line the live status reads: the state (field 3), the
/// process group (field 5), the session (field 6) and the start time in clock ticks since boot
/// (field 22).
/// </summary>
internal readonly record struct ProcStat(char State, int Pgrp, int Session, long StartTicks)
{
    /// <summary>
    /// The number of fields the line must have after the command name: field 3 up to field 22.
    /// </summary>
    private const int FieldsNeeded = 20;

    /// <summary>
    /// Whether the process has exited: a zombie (Z) or a dead entry (X).
    /// </summary>
    public bool Exited => State is 'Z' or 'X';

    /// <summary>
    /// Parses a stat line. The command name sits in parentheses and may hold spaces and
    /// parentheses of its own, so the fields are counted after the LAST ')'. Null when the line
    /// does not have the shape of a stat line.
    /// </summary>
    public static ProcStat? Parse(string line)
    {
        int close = line.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        string[] fields = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < FieldsNeeded || fields[0].Length is not 1)
        {
            return null;
        }

        CultureInfo ic = CultureInfo.InvariantCulture;
        if (!int.TryParse(fields[2], NumberStyles.AllowLeadingSign, ic, out int pgrp)
            || !int.TryParse(fields[3], NumberStyles.AllowLeadingSign, ic, out int session)
            || !long.TryParse(fields[19], NumberStyles.None, ic, out long start))
        {
            return null;
        }

        return new ProcStat(fields[0][0], pgrp, session, start);
    }
}
