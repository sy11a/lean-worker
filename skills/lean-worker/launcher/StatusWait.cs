// `status --wait` (docs/research/live-status/README.md, sections 4.2 and 4.5): block until every run
// in scope has ended, then print the listing of those runs and exit with what they ended as. The
// scope is fixed when the wait starts (after --expect runs appeared); a gate chain is one unit,
// ended when its launcher is gone, not when a round is. It reads only: the first read is the
// one-shot call's (with its reaping), every later read reaps nothing, and nothing is signalled,
// killed or written.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class StatusWait(StatusReader reader, StatusHost host, TextWriter output, StatusWait.Settings settings)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(120);
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Every run in scope ended success (a gate chain: clean).
    /// </summary>
    public const int AllSuccess = 0;

    /// <summary>
    /// A run ended not success: failed, orphaned, lost, gone, or a gate chain that did not end clean.
    /// </summary>
    public const int SomeFailed = 1;

    /// <summary>
    /// None failed, at least one wrapped up near its budget.
    /// </summary>
    public const int SomeWrappedUp = 3;

    /// <summary>
    /// No run matched the scope: never a silent 0.
    /// </summary>
    public const int NothingMatched = 6;

    /// <summary>
    /// --timeout passed with runs not ended.
    /// </summary>
    public const int TimedOut = 7;

    /// <summary>
    /// The rest ended, but runs stayed unknown or unreadable past --grace, or fewer than --expect appeared.
    /// </summary>
    public const int Unresolved = 8;

    private static readonly CultureInfo _ic = CultureInfo.InvariantCulture;

    /// <summary>
    /// The options of one wait.
    /// </summary>
    internal sealed record Settings(TimeSpan Interval, TimeSpan Timeout, TimeSpan Grace, DateTimeOffset? StartedAfter, int? Expect, bool Any);

    /// <summary>
    /// What one ended run counts as for the exit code.
    /// </summary>
    internal enum Verdict
    {
        Success,
        WrappedUp,
        Failed,
    }

    /// <summary>
    /// One unit waited on: a run, or a gate chain keyed by its first round; its row (the chain's
    /// row), its rounds, whether it ended (then the row is kept as it was), and since when its
    /// launcher's state could not be told.
    /// </summary>
    private sealed class Unit(string key, StatusRow row, List<StatusRow> rounds)
    {
        public string Key { get; } = key;

        public StatusRow Row { get; set; } = row;

        public List<StatusRow> Rounds { get; set; } = rounds;

        public bool Ended { get; set; }

        public DateTimeOffset? UnsureSince { get; set; }
    }

    private DateTimeOffset Now => reader.Clock.GetUtcNow();

    /// <summary>
    /// The wait over the scope <paramref name="read"/> reads (with reap: whether the read may reap),
    /// starting from <paramref name="first"/>, the first read; the exit code.
    /// </summary>
    public async Task<int> RunAsync(StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read)
    {
        DateTimeOffset start = Now;
        StatusScope.Listing listing = first;
        while (true)
        {
            DateTimeOffset now = Now;
            StatusListing.Shown shown = StatusListing.Select(listing, reader, now, settings.StartedAfter);
            List<StatusRow> candidates = [.. shown.Rows.Where(r => r.State is not StatusStates.Legacy && (settings.StartedAfter is not null || !r.Ended))];
            if (settings.Expect is not { } expect || candidates.Count >= expect)
            {
                int legacy = shown.Rows.Count(r => r.State is StatusStates.Legacy);
                return await WaitOnAsync(listing, [.. candidates.Select(r => NewUnit(r, shown.View))], legacy, start, read).ConfigureAwait(false);
            }

            if (now - start >= settings.Grace || now - start >= settings.Timeout)
            {
                List<Unit> found = [.. candidates.Select(r => NewUnit(r, shown.View))];
                bool late = now - start >= settings.Grace;
                string text = late
                    ? string.Create(_ic, $"only {found.Count} of {expect} expected runs appeared within {StatusText.Duration(settings.Grace)}")
                    : string.Create(_ic, $"timed out after {StatusText.Duration(settings.Timeout)} with {found.Count} of {expect} expected runs");
                return await FinishAsync(listing, found, late ? Unresolved : TimedOut, text).ConfigureAwait(false);
            }

            await host.Sleep(Pause(start), host.Cancel).ConfigureAwait(false);
            listing = await read().ConfigureAwait(false) ?? listing with { Rows = [] };
        }
    }

    /// <summary>
    /// The wait itself, over the units fixed now.
    /// </summary>
    private async Task<int> WaitOnAsync(StatusScope.Listing listing, List<Unit> units, int legacy, DateTimeOffset start, Func<Task<StatusScope.Listing?>> read)
    {
        if (units.Count is 0)
        {
            string after = settings.StartedAfter is { } t ? " started at or after " + RunStatus.Stamp(t) : string.Empty;
            await output.WriteLineAsync($"lean-worker status: no run in {TerminalText.Clean(listing.Scope, StatusText.ScopeLimit)}{after} to wait on (exit {NothingMatched})")
                .ConfigureAwait(false);
            return NothingMatched;
        }

        string skipped = legacy > 0 ? string.Create(_ic, $"; {legacy} legacy runs not waited on (no launcher recorded)") : string.Empty;
        await output.WriteLineAsync(string.Create(_ic,
            $"lean-worker status: waiting on {units.Count} {(units.Count is 1 ? "run" : "runs")} matched at {Now:HH:mm:ss}Z; runs launched later are not included{skipped}"))
            .ConfigureAwait(false);
        await output.FlushAsync(host.Cancel).ConfigureAwait(false);
        while (true)
        {
            DateTimeOffset now = Now;
            foreach (Unit u in units)
            {
                Track(u, now);
            }

            if (Done(units, now) is { } code)
            {
                return await FinishAsync(listing, units, code, Outcome(units, code)).ConfigureAwait(false);
            }

            if (now - start >= settings.Timeout)
            {
                int open = units.Count(u => !u.Ended);
                return await FinishAsync(listing, units, TimedOut,
                    string.Create(_ic, $"timed out after {StatusText.Duration(settings.Timeout)} with {open} of {units.Count} runs not ended")).ConfigureAwait(false);
            }

            await host.Sleep(Pause(start), host.Cancel).ConfigureAwait(false);
            listing = await read().ConfigureAwait(false) ?? listing with { Rows = [] };
            Refresh(units, StatusLayout.Chains(listing.Rows, reader, Now));
        }
    }

    /// <summary>
    /// The exit code when the wait is over, null while it is not: with --any as soon as a run ended
    /// (or nothing is left to hold the wait); else when every unit ended or stopped holding it.
    /// </summary>
    private int? Done(List<Unit> units, DateTimeOffset now)
    {
        List<Unit> ended = [.. units.Where(u => u.Ended)];
        bool holding = units.Exists(u => !u.Ended && !GivenUp(u, now));
        if (holding && !(settings.Any && ended.Count > 0))
        {
            return null;
        }

        List<Verdict> verdicts = [.. ended.Select(u => Judge(u.Row))];
        if (verdicts.Contains(Verdict.Failed))
        {
            return SomeFailed;
        }

        if (ended.Count < units.Count && !(settings.Any && ended.Count > 0))
        {
            return Unresolved;
        }

        return verdicts.Contains(Verdict.WrappedUp) ? SomeWrappedUp : AllSuccess;
    }

    private string Outcome(List<Unit> units, int code)
    {
        int ended = units.Count(u => u.Ended);
        return code switch
        {
            SomeFailed => string.Create(_ic, $"{units.Count(u => u.Ended && Judge(u.Row) is Verdict.Failed)} of {units.Count} runs ended not success"),
            Unresolved => string.Create(_ic,
                $"{units.Count - ended} of {units.Count} runs stayed unknown or unreadable past the grace of {StatusText.Duration(settings.Grace)}"),
            SomeWrappedUp => string.Create(_ic, $"{ended} of {units.Count} runs ended, {units.Count(u => u.Ended && Judge(u.Row) is Verdict.WrappedUp)} wrapped up near the budget"),
            _ => string.Create(_ic, $"{ended} of {units.Count} runs ended success"),
        };
    }

    /// <summary>
    /// What an ended run counts as: wrapped-up; success when its final state is success and, when
    /// its end records an exit code, that code is 0 (a gate chain that ended stuck or in an error
    /// records success with 4 or 5); anything else is a failure (orphaned, lost and gone included).
    /// </summary>
    internal static Verdict Judge(StatusRow row)
    {
        if (row.State is "wrapped-up")
        {
            return Verdict.WrappedUp;
        }

        bool clean = row.Status?["end"] is not JsonObject end || StatusProbe.NumOrNull(end["exit_code"]) is null or 0;
        return row.State is "success" && clean ? Verdict.Success : Verdict.Failed;
    }

    /// <summary>
    /// Whether a unit no longer holds the wait: its launcher's state could not be told for --grace.
    /// </summary>
    private bool GivenUp(Unit u, DateTimeOffset now) => u.UnsureSince is { } since && now - since >= settings.Grace;

    /// <summary>
    /// The unit's state after a read: ended once its launcher is provably gone, for a gate chain
    /// once every round read has (then it is kept as it is); live while a round runs; else unsure
    /// since the first read that could not tell.
    /// </summary>
    private static void Track(Unit u, DateTimeOffset now)
    {
        if (u.Ended)
        {
            return;
        }

        if (u.Row.Ended && u.Rounds.TrueForAll(r => r.Ended))
        {
            u.Ended = true;
            u.UnsureSince = null;
        }
        else if (u.Row.Live || u.Rounds.Exists(r => r.Live))
        {
            u.UnsureSince = null;
        }
        else
        {
            u.UnsureSince ??= now;
        }
    }

    /// <summary>
    /// The units' rows from a new read: a unit not listed any more cannot be told (unknown).
    /// </summary>
    private static void Refresh(List<Unit> units, StatusLayout.View view)
    {
        Dictionary<string, StatusRow> byKey = new(StringComparer.Ordinal);
        foreach (StatusRow head in view.Rows)
        {
            _ = byKey.TryAdd(StatusListing.UnitKey(head, view), head);
        }

        foreach (Unit u in units.Where(u => !u.Ended))
        {
            if (byKey.TryGetValue(u.Key, out StatusRow? head))
            {
                u.Row = head;
                u.Rounds = view.Rounds[head];
            }
            else
            {
                u.Row = u.Row with
                {
                    State = StatusStates.Unknown,
                    Liveness = Liveness.Unknown,
                    Detail = "no longer listed in the scope",
                    Attention = true,
                    Quiet = null,
                };
                u.Rounds = [u.Row];
            }
        }
    }

    private static Unit NewUnit(StatusRow head, StatusLayout.View view) => new(StatusListing.UnitKey(head, view), head, view.Rounds[head]);

    /// <summary>
    /// The pause before the next read: the interval, or what is left of --timeout when that is less.
    /// </summary>
    private TimeSpan Pause(DateTimeOffset start)
    {
        TimeSpan left = start + settings.Timeout - Now;
        TimeSpan pause = left < settings.Interval ? left : settings.Interval;
        return pause > TimeSpan.Zero ? pause : TimeSpan.Zero;
    }

    /// <summary>
    /// The listing of the units waited on and the last line with the outcome and the exit code.
    /// </summary>
    private async Task<int> FinishAsync(StatusScope.Listing listing, List<Unit> units, int code, string outcome)
    {
        DateTimeOffset now = Now;
        Dictionary<string, StatusRow> byRunId = new(StringComparer.Ordinal);
        foreach (Unit u in units)
        {
            foreach (StatusRow round in u.Rounds)
            {
                _ = byRunId.TryAdd(round.RunId, u.Row);
            }
        }

        List<StatusRow> rows = [.. units.Select(u => u.Row)];
        List<(StatusRow Row, int Depth)> nested = StatusLayout.Nest(StatusText.Order(rows), byRunId);
        await output.WriteLineAsync(StatusText.Header(listing.Scope, rows, hidden: 0, since: null)).ConfigureAwait(false);
        await StatusListing.PrintLinesAsync(output, [.. nested.Select(n => n.Row with { Depth = n.Depth })], now, listing.OwnerColumn).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(_ic, $"lean-worker status: wait over: {outcome} (exit {code})")).ConfigureAwait(false);
        return code;
    }
}
