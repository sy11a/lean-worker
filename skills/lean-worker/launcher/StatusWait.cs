// `status --wait` (docs/research/live-status/README.md, sections 4.2 and 4.5): block until every run
// in scope has ended, then print the listing of those runs and exit with what they ended as. The
// scope is fixed when the wait starts (after --expect runs appeared); a gate chain is one unit,
// ended when its launcher is gone, not when a round is, and judged by every row that names its
// first round's launcher or chain, also the rounds the layout set apart; a round whose end says its
// gate went on to a round the read does not hold does not end it. It reads only: the first
// read is the one-shot call's (with its reaping), every later read reaps nothing, and nothing is
// signalled, killed or written. Every span it waits (--timeout, --grace, --interval) is measured
// on the clock's monotonic timestamp; the wall clock only gives the times shown and the times
// compared with the files'.

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

    /// <summary>
    /// The host's token stopped the wait before it was over: 128 + SIGINT, the code an interrupted
    /// `status --wait` (and the line-mode watcher) already ends with when the signal's default action
    /// ends the process.
    /// </summary>
    public const int Interrupted = 130;

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
    /// row), its rounds, the rows joined to it (keyed by <see cref="StatusListing.RowKey"/>: rows
    /// that name its first round's launcher or chain but that the layout set apart), whether it
    /// ended (then its rows are kept as they were), and since when (the clock's timestamp) it could
    /// not be told.
    /// </summary>
    private sealed class Unit(string key, StatusRow row, List<StatusRow> rounds)
    {
        public string Key { get; } = key;

        public StatusRow Row { get; set; } = row;

        public List<StatusRow> Rounds { get; set; } = rounds;

        /// <summary>
        /// The run id of the first round: a chain's id.
        /// </summary>
        public string FirstRunId { get; } = rounds.Count > 0 ? rounds[0].RunId : row.RunId;

        /// <summary>
        /// The launcher the first round names (the pointer is written once at launch).
        /// </summary>
        public string? FirstLauncher { get; } = StatusLayout.NamedLauncher(rounds.Count > 0 ? rounds[0] : row);

        /// <summary>
        /// The runs folder of the first round: its run id names a chain only there.
        /// </summary>
        public string FirstFolder { get; } = StatusLayout.RunsFolder(rounds.Count > 0 ? rounds[0] : row);

        public Dictionary<string, StatusRow> Joined { get; } = new(StringComparer.Ordinal);

        public bool Ended { get; set; }

        public long? UnsureSince { get; set; }

        /// <summary>
        /// Whether the unit's own rows (its row and its rounds) have all ended.
        /// </summary>
        public bool OwnEnded => Row.Ended && Rounds.TrueForAll(r => r.Ended);

        /// <summary>
        /// Whether one of the unit's own rows is live.
        /// </summary>
        public bool OwnLive => Row.Live || Rounds.Exists(r => r.Live);
    }

    private DateTimeOffset Now => reader.Clock.GetUtcNow();

    private long Stamp => reader.Clock.GetTimestamp();

    /// <summary>
    /// The wait over the scope <paramref name="read"/> reads (with reap: whether the read may reap),
    /// starting from <paramref name="first"/>, the first read; the exit code. A wait the host's
    /// token stops ends at once with <see cref="Interrupted"/>, never with an exception.
    /// </summary>
    public async Task<int> RunAsync(StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read)
    {
        try
        {
            return await ScopeAsync(first, read).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (host.Cancel.IsCancellationRequested)
        {
            await output.WriteLineAsync(string.Create(_ic, $"lean-worker status: wait stopped before it was over (exit {Interrupted})")).ConfigureAwait(false);
            return Interrupted;
        }
    }

    /// <summary>
    /// The scope: the runs in it when the wait starts (after --expect runs appeared), then the wait
    /// on them. A legacy row with a summary.json (a finished run of an older launcher) is not waited
    /// on; a legacy row without one (a run that has not written its status yet, or whose status
    /// writes failed) is, and holds the wait through --grace as an unknown run does.
    /// </summary>
    private async Task<int> ScopeAsync(StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read)
    {
        long start = Stamp;
        StatusScope.Listing listing = first;
        while (true)
        {
            StatusListing.Shown shown = StatusListing.Select(listing, reader, Now, settings.StartedAfter);
            List<StatusRow> candidates = [.. shown.Rows.Where(r => !FinishedLegacy(r) && (settings.StartedAfter is not null || !r.Ended))];
            if (settings.Expect is not { } expect || candidates.Count >= expect)
            {
                int legacy = shown.Rows.Count(r => FinishedLegacy(r));
                List<Unit> units = [.. candidates.Select(r => NewUnit(r, shown.View))];
                Join(units, shown.View);
                return await WaitOnAsync(listing, units, legacy, start, read).ConfigureAwait(false);
            }

            TimeSpan elapsed = reader.Clock.GetElapsedTime(start);
            if (elapsed >= settings.Grace || elapsed >= settings.Timeout)
            {
                List<Unit> found = [.. candidates.Select(r => NewUnit(r, shown.View))];
                bool late = elapsed >= settings.Grace;
                string text = late
                    ? string.Create(_ic, $"only {found.Count} of {expect} expected runs appeared within {StatusText.Duration(settings.Grace)}")
                    : string.Create(_ic, $"timed out after {StatusText.Duration(settings.Timeout)} with {found.Count} of {expect} expected runs");
                return await FinishAsync(listing, found, late ? Unresolved : TimedOut, text).ConfigureAwait(false);
            }

            await SleepAsync(start).ConfigureAwait(false);
            listing = await read().ConfigureAwait(false) ?? listing with { Rows = [] };
        }
    }

    // A legacy row whose run left a summary.json: a finished run of an older launcher.
    private static bool FinishedLegacy(StatusRow r) => r.State is StatusStates.Legacy && r.SummaryStatus is not null;

    /// <summary>
    /// The wait itself, over the units fixed now.
    /// </summary>
    private async Task<int> WaitOnAsync(StatusScope.Listing listing, List<Unit> units, int legacy, long start, Func<Task<StatusScope.Listing?>> read)
    {
        if (units.Count is 0)
        {
            string after = settings.StartedAfter is { } t ? " started at or after " + RunStatus.Stamp(t) : string.Empty;
            await output.WriteLineAsync($"lean-worker status: no run in {TerminalText.Clean(listing.Scope, StatusText.ScopeLimit)}{after} to wait on (exit {NothingMatched})")
                .ConfigureAwait(false);
            return NothingMatched;
        }

        string skipped = legacy > 0 ? string.Create(_ic, $"; {legacy} legacy runs not waited on (finished, no launcher recorded)") : string.Empty;
        await output.WriteLineAsync(string.Create(_ic,
            $"lean-worker status: waiting on {units.Count} {(units.Count is 1 ? "run" : "runs")} matched at {Now:HH:mm:ss}Z; runs launched later are not included{skipped}"))
            .ConfigureAwait(false);
        await output.FlushAsync(host.Cancel).ConfigureAwait(false);
        while (true)
        {
            long now = Stamp;
            foreach (Unit u in units)
            {
                Track(u, now);
            }

            if (Done(units, now) is { } code)
            {
                return await FinishAsync(listing, units, code, Outcome(units, code)).ConfigureAwait(false);
            }

            if (reader.Clock.GetElapsedTime(start) >= settings.Timeout)
            {
                int open = units.Count(u => !u.Ended);
                return await FinishAsync(listing, units, TimedOut,
                    string.Create(_ic, $"timed out after {StatusText.Duration(settings.Timeout)} with {open} of {units.Count} runs not ended")).ConfigureAwait(false);
            }

            await SleepAsync(start).ConfigureAwait(false);
            listing = await read().ConfigureAwait(false) ?? listing with { Rows = [] };
            Refresh(units, StatusLayout.Chains(listing.Rows, reader, Now));
        }
    }

    /// <summary>
    /// The exit code when the wait is over, null while it is not: with --any as soon as a run ended
    /// (or nothing is left to hold the wait); else when every unit ended or stopped holding it.
    /// </summary>
    private int? Done(List<Unit> units, long now)
    {
        List<Unit> ended = [.. units.Where(u => u.Ended)];
        bool holding = units.Exists(u => !u.Ended && !GivenUp(u, now));
        if (holding && !(settings.Any && ended.Count > 0))
        {
            return null;
        }

        List<Verdict> verdicts = [.. ended.Select(u => JudgeUnit(u))];
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
            SomeFailed => string.Create(_ic, $"{units.Count(u => u.Ended && JudgeUnit(u) is Verdict.Failed)} of {units.Count} runs ended not success"),
            Unresolved => string.Create(_ic,
                $"{units.Count - ended} of {units.Count} runs stayed unknown or unreadable past the grace of {StatusText.Duration(settings.Grace)}"),
            SomeWrappedUp => string.Create(_ic, $"{ended} of {units.Count} runs ended, {units.Count(u => u.Ended && JudgeUnit(u) is Verdict.WrappedUp)} wrapped up near the budget"),
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
    /// What an ended unit counts as: its row's verdict, and a failure when any row joined to it is
    /// not a success. A joined row can only make the verdict stricter (it adds a condition, never
    /// lifts one): a forged one can turn a 0 into a 1 here, or, while it does not end, into an 8
    /// after the grace (<see cref="Track"/>), never a 1 or an 8 into a 0.
    /// </summary>
    private static Verdict JudgeUnit(Unit u) =>
        u.Joined.Values.Any(r => Judge(r) is not Verdict.Success) ? Verdict.Failed : Judge(u.Row);

    /// <summary>
    /// Whether a unit no longer holds the wait: its state could not be told for --grace.
    /// </summary>
    private bool GivenUp(Unit u, long now) => u.UnsureSince is { } since && reader.Clock.GetElapsedTime(since, now) >= settings.Grace;

    /// <summary>
    /// The unit's state after a read: ended once its own rows (a chain: every round read) and every
    /// row joined to it have ended (then it is kept as it is); live while one of its own rows runs;
    /// else unsure since the first read that could not tell. A joined row never holds the wait as
    /// live: one that has not ended when the unit's own rows have makes the unit unsure, so it ends
    /// as given up (exit 8) after the grace. A round, own or joined, whose end says its gate went on
    /// to another round that no row of the read is reads unknown (<see cref="StatusLayout.AwaitsRound"/>),
    /// so the unit does not end on it in any scope: it is unsure and gives 8 after the grace (7 when
    /// --timeout passes first), and under --any it neither ends the wait nor counts as a 0. That
    /// mark is set only on an end that reads as a success and only takes that end away (never makes
    /// a row live, ended or success), so a forged <c>gate</c> can turn a 0 into an 8, never anything
    /// into a 0; a failed or wrapped-up end keeps its 1 or 3 whatever its <c>gate</c> says.
    /// </summary>
    private static void Track(Unit u, long now)
    {
        if (u.Ended)
        {
            return;
        }

        if (u.OwnEnded && u.Joined.Values.All(r => r.Ended))
        {
            u.Ended = true;
            u.UnsureSince = null;
        }
        else if (u.OwnLive)
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
                u.Row = NotListed(u.Row);
                u.Rounds = [u.Row];
            }
        }

        Join(units, view);
    }

    /// <summary>
    /// Joins to every unit not ended the rows of <paramref name="view"/> that are not its own and
    /// name its first round: the same launcher (pointer, else status file) or the first round's run
    /// id as their chain id in the first round's runs folder. A row once joined stays joined while
    /// the read lists it (by its key), judged by its new state even when it no longer names the
    /// chain (its run dir was removed: gone, still shown under the name it had). A row joined before
    /// and not listed any more cannot be
    /// told (unknown), unless it had ended (then it is kept as it was). The rows are indexed once
    /// per read, not once per unit.
    /// </summary>
    private static void Join(List<Unit> units, StatusLayout.View view)
    {
        List<StatusRow> all = [.. view.Rounds.Values.SelectMany(rounds => rounds)];
        StatusLayout.Names names = new(all);
        Dictionary<string, StatusRow> byKey = new(StringComparer.Ordinal);
        foreach (StatusRow r in all)
        {
            _ = byKey.TryAdd(StatusListing.RowKey(r), r);
        }

        foreach (Unit u in units.Where(u => !u.Ended))
        {
            HashSet<string> own = new(u.Rounds.Append(u.Row).Select(r => StatusListing.RowKey(r)), StringComparer.Ordinal);
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<StatusRow> again = [.. u.Joined.Keys.Select(k => byKey.GetValueOrDefault(k)).OfType<StatusRow>()];
            foreach (StatusRow r in names.Naming(u.FirstLauncher, u.FirstFolder, u.FirstRunId).Concat(again))
            {
                string key = StatusListing.RowKey(r);
                if (!own.Contains(key) && seen.Add(key))
                {
                    // A joined row whose status file is gone keeps the name it was shown with.
                    u.Joined[key] = r.Status is null && u.Joined.TryGetValue(key, out StatusRow? before) ? r with { Name = before.Name } : r;
                }
            }

            foreach (string key in u.Joined.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                if (own.Contains(key))
                {
                    _ = u.Joined.Remove(key);
                }
                else if (!u.Joined[key].Ended)
                {
                    u.Joined[key] = NotListed(u.Joined[key]);
                }
            }
        }
    }

    // A row the scope does not list any more: it cannot be told.
    private static StatusRow NotListed(StatusRow row)
    {
        return row with
        {
            State = StatusStates.Unknown,
            Liveness = Liveness.Unknown,
            Detail = "no longer listed in the scope",
            Attention = true,
            Quiet = null,
        };
    }

    private static Unit NewUnit(StatusRow head, StatusLayout.View view) => new(StatusListing.UnitKey(head, view), head, view.Rounds[head]);

    /// <summary>
    /// The pause before the next read: the interval, or what is left of --timeout when that is
    /// less; a wait the host's token stopped goes no further.
    /// </summary>
    private async Task SleepAsync(long start)
    {
        TimeSpan left = settings.Timeout - reader.Clock.GetElapsedTime(start);
        TimeSpan pause = left < settings.Interval ? left : settings.Interval;
        await host.Sleep(pause > TimeSpan.Zero ? pause : TimeSpan.Zero, host.Cancel).ConfigureAwait(false);
        host.Cancel.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The listing of the units waited on (with the rows joined to them that no unit shows) and the
    /// last line with the outcome and the exit code.
    /// </summary>
    private async Task<int> FinishAsync(StatusScope.Listing listing, List<Unit> units, int code, string outcome)
    {
        DateTimeOffset now = Now;
        Dictionary<string, StatusRow> byRunId = new(StringComparer.Ordinal);
        HashSet<string> shownKeys = new(StringComparer.Ordinal);
        foreach (Unit u in units)
        {
            foreach (StatusRow round in u.Rounds)
            {
                _ = byRunId.TryAdd(round.RunId, u.Row);
                _ = shownKeys.Add(StatusListing.RowKey(round));
            }

            _ = shownKeys.Add(u.Key);
            _ = shownKeys.Add(StatusListing.RowKey(u.Row));
        }

        List<StatusRow> rows = [.. units.Select(u => u.Row)];
        foreach (StatusRow joined in units.SelectMany(u => u.Joined).Where(j => shownKeys.Add(j.Key)).Select(j => j.Value))
        {
            rows.Add(joined);
            _ = byRunId.TryAdd(joined.RunId, joined);
        }

        List<(StatusRow Row, int Depth)> nested = StatusLayout.Nest(StatusText.Order(rows), byRunId);
        await output.WriteLineAsync(StatusText.Header(listing.Scope, rows, hidden: 0, since: null)).ConfigureAwait(false);
        await StatusListing.PrintLinesAsync(output, [.. nested.Select(n => n.Row with { Depth = n.Depth })], now, listing.OwnerColumn).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(_ic, $"lean-worker status: wait over: {outcome} (exit {code})")).ConfigureAwait(false);
        return code;
    }
}
