// `status --watch` (docs/research/live-status/README.md, sections 4.2 and 4.3). On a terminal: the
// table of StatusTable redrawn in place every --interval on the alternate screen until q or a stop
// signal, rows that ended while it was open in bold until `a`, the bell with --bell. With output
// redirected: one line per change of a run's state, no escape at all. The scope is read again
// every interval (new runs appear); only the first read reaps, as the one-shot call does. The
// terminal is restored on every way out: q, a stop signal, the token, an exception.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class StatusWatch(StatusReader reader, StatusHost host, TextWriter output, StatusWatch.Settings settings)
{
    /// <summary>
    /// Alternate screen on, cursor hidden.
    /// </summary>
    internal const string Enter = "\u001b[?1049h\u001b[?25l";

    /// <summary>
    /// Attributes reset, cursor shown, alternate screen off.
    /// </summary>
    internal const string Leave = StatusTable.Reset + "\u001b[?25h\u001b[?1049l";

    private const string BeginFrame = "\u001b[?2026h\u001b[H";
    private const string EndLine = "\u001b[K";
    private const string EndFrame = "\u001b[J\u001b[?2026l";
    private const char BellChar = '\a';

    /// <summary>
    /// How often keys are looked at while the table waits for its next refresh.
    /// </summary>
    private static readonly TimeSpan _keySlice = TimeSpan.FromMilliseconds(100);

    private static readonly CultureInfo _ic = CultureInfo.InvariantCulture;

    // Per run (its key): the state text last seen and whether it had ended.
    private readonly Dictionary<string, (string State, bool Ended)> _seen = new(StringComparer.Ordinal);

    // The keys of runs that ended while the table was open, not yet acknowledged.
    private readonly HashSet<string> _unacknowledged = new(StringComparer.Ordinal);

    private int _signals;
    private volatile bool _resized;

    /// <summary>
    /// The options of one watch: the refresh period, --started-after, --bell, and whether STATE is
    /// coloured (NO_COLOR unset).
    /// </summary>
    internal sealed record Settings(TimeSpan Interval, DateTimeOffset? StartedAfter, bool Bell, bool Color);

    private enum Pause
    {
        Refresh,
        Quit,
    }

    /// <summary>
    /// The watch over the scope <paramref name="read"/> reads, from <paramref name="first"/>, the
    /// first read; 0 when it is left.
    /// </summary>
    public async Task<int> RunAsync(StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read)
    {
        return host.Terminal.OutputRedirected
            ? await LinesAsync(first, read).ConfigureAwait(false)
            : await TableAsync(first, read).ConfigureAwait(false);
    }

    private async Task<int> TableAsync(StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(host.Cancel);
        using IDisposable signals = host.Terminal.OnSignals(() => Stop(stop), () => _resized = true);
        try
        {
            await output.WriteAsync(Enter).ConfigureAwait(false);
            StatusScope.Listing listing = first;
            bool firstFrame = true;
            while (!stop.IsCancellationRequested)
            {
                await DrawAsync(listing, firstFrame, stop.Token).ConfigureAwait(false);
                firstFrame = false;
                if (await PauseAsync(stop.Token).ConfigureAwait(false) is Pause.Quit)
                {
                    break;
                }

                listing = await read().ConfigureAwait(false) ?? listing with { Rows = [] };
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped while a frame was written: the terminal is restored below.
        }
        finally
        {
            Restore();
        }

        return 0;
    }

    /// <summary>
    /// A stop signal: the first one ends the loop (and cancels the signal's default action, so the
    /// finally block restores the terminal); a second one restores the terminal at once and lets
    /// the default action end the process.
    /// </summary>
    private bool Stop(CancellationTokenSource stop)
    {
        if (Interlocked.Increment(ref _signals) > 1)
        {
            Restore();
            return false;
        }

        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The watch is already over.
        }

        return true;
    }

    private void Restore()
    {
        try
        {
            output.Write(Leave);
            output.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing left to restore on a closed output.
        }
    }

    private async Task DrawAsync(StatusScope.Listing listing, bool firstFrame, CancellationToken stop)
    {
        DateTimeOffset now = reader.Clock.GetUtcNow();
        StatusListing.Shown shown = StatusListing.Select(listing, reader, now, settings.StartedAfter);
        List<(StatusRow Row, int Depth)> nested = StatusLayout.Nest(StatusText.Order(shown.Rows), shown.View.ByRunId);
        HashSet<StatusRow> unacknowledged = new(ReferenceEqualityComparer.Instance);
        int rings = 0;
        List<StatusRow> rows = [];
        foreach ((StatusRow head, int depth) in nested)
        {
            string key = StatusListing.UnitKey(head, shown.View);
            rings += Track(key, head, firstFrame) ? 1 : 0;
            StatusRow row = head with { Depth = depth };
            rows.Add(row);
            if (_unacknowledged.Contains(key))
            {
                _ = unacknowledged.Add(row);
            }
        }

        StatusTable.FrameData data = new(listing.Scope, rows, unacknowledged, settings.Color, listing.OwnerColumn);
        StringBuilder frame = new(BeginFrame);
        _ = frame.AppendJoin(EndLine + "\r\n", StatusTable.Frame(data, now, host.Terminal.Width, host.Terminal.Height));
        _ = frame.Append(EndLine).Append(EndFrame);
        if (settings.Bell && rings > 0)
        {
            _ = frame.Append(BellChar, rings);
        }

        await output.WriteAsync(frame.ToString()).ConfigureAwait(false);
        await output.FlushAsync(stop).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a run's state; true when it ended since the last frame (not on the first frame: what
    /// had ended before the table opened is no news).
    /// </summary>
    private bool Track(string key, StatusRow head, bool firstFrame)
    {
        bool known = _seen.TryGetValue(key, out (string State, bool Ended) before);
        _seen[key] = (head.State, head.Ended);
        if (!head.Ended || firstFrame || (known && before.Ended))
        {
            return false;
        }

        return _unacknowledged.Add(key);
    }

    /// <summary>
    /// Waits for the next refresh, reading keys meanwhile: q quits, a acknowledges every ended row
    /// (and redraws), a resize redraws at once; the token quits. The interval is measured on the
    /// clock's monotonic timestamp, so a step of the wall clock neither stretches nor cuts it.
    /// </summary>
    private async Task<Pause> PauseAsync(CancellationToken stop)
    {
        long start = reader.Clock.GetTimestamp();
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < settings.Interval; elapsed = reader.Clock.GetElapsedTime(start))
        {
            if (host.Terminal.ReadKey() is { } key)
            {
                if (key.KeyChar is 'q' or 'Q' || (key.Key is ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                {
                    return Pause.Quit;
                }

                if (key.KeyChar is 'a' or 'A')
                {
                    _unacknowledged.Clear();
                    return Pause.Refresh;
                }
            }

            if (_resized)
            {
                _resized = false;
                return Pause.Refresh;
            }

            if (!await SleepAsync(Min(_keySlice, settings.Interval - elapsed), stop).ConfigureAwait(false))
            {
                return Pause.Quit;
            }
        }

        return Pause.Refresh;
    }

    /// <summary>
    /// The line mode: the runs as the first read lists them, then one line per change of a run's
    /// state, until the token is cancelled (or the process is stopped) or the output is closed.
    /// </summary>
    private async Task<int> LinesAsync(StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read)
    {
        StatusScope.Listing listing = first;
        try
        {
            while (true)
            {
                foreach (string line in Changes(listing))
                {
                    await output.WriteLineAsync(line).ConfigureAwait(false);
                }

                await output.FlushAsync(host.Cancel).ConfigureAwait(false);
                if (!await SleepAsync(settings.Interval, host.Cancel).ConfigureAwait(false))
                {
                    return 0;
                }

                listing = await read().ConfigureAwait(false) ?? listing with { Rows = [] };
            }
        }
        catch (IOException)
        {
            // The reader of the lines went away.
            return 0;
        }
        catch (OperationCanceledException) when (host.Cancel.IsCancellationRequested)
        {
            return 0;
        }
    }

    /// <summary>
    /// The lines of one read: a run seen for the first time with its state, a run whose state
    /// changed with the old and the new one; each with its counters, or its detail when it needs
    /// attention. Every value from a file is cleaned.
    /// </summary>
    private List<string> Changes(StatusScope.Listing listing)
    {
        DateTimeOffset now = reader.Clock.GetUtcNow();
        StatusListing.Shown shown = StatusListing.Select(listing, reader, now, settings.StartedAfter);
        string stamp = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", _ic);
        List<string> lines = [];
        foreach (StatusRow r in StatusText.Order(shown.Rows))
        {
            string key = StatusListing.UnitKey(r, shown.View);
            string state = TerminalText.Clean(StatusText.StateText(r, r.Status?["live"] as JsonObject), StatusText.CellLimits[0]);
            bool known = _seen.TryGetValue(key, out (string State, bool Ended) before);
            _seen[key] = (state, r.Ended);
            if (known && string.Equals(before.State, state, StringComparison.Ordinal))
            {
                continue;
            }

            string change = known ? TerminalText.Clean(before.State, StatusText.CellLimits[0]) + " -> " + state : state;
            lines.Add($"{stamp} {TerminalText.Clean(r.Name, StatusText.CellLimits[1])} {change} {Tail(r, now)}");
        }

        return lines;
    }

    // The counters of a run (calls, cost, elapsed), or its detail in parentheses.
    private static string Tail(StatusRow r, DateTimeOffset now)
    {
        if (r.Attention || r.Status is not { } doc)
        {
            return "(" + TerminalText.Clean(r.Detail, StatusText.CellLimits[^1]) + ")";
        }

        JsonObject? live = doc["live"] as JsonObject;
        string calls = Json.Num(live?["api_calls"]).ToString(_ic) + " calls";
        string cost = StatusText.Money(Json.Dec(live, "cost_usd"));
        return TerminalText.Clean($"{calls} {cost} {StatusText.Duration(StatusText.Elapsed(r, doc, live, now))}", StatusText.CellLimits[^1]);
    }

    /// <summary>
    /// Sleeps; false when the token ended the sleep.
    /// </summary>
    private async Task<bool> SleepAsync(TimeSpan pause, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            if (pause > TimeSpan.Zero)
            {
                await host.Sleep(pause, token).ConfigureAwait(false);
            }

            return !token.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
