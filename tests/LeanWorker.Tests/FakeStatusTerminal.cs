using System.Collections.Concurrent;

namespace LeanWorker.Tests;

/// <summary>
/// A terminal a test controls: redirected or not, its size, the keys waiting to be read and the stop and resize
/// signals it can fire. Nothing of System.Console is touched.
/// </summary>
internal sealed class FakeStatusTerminal : IStatusTerminal
{
    private readonly ConcurrentQueue<ConsoleKeyInfo> _keys = new();
    private Func<bool>? _stop;
    private Action? _resize;

    public bool OutputRedirected { get; set; }

    public int Width { get; set; } = 100;

    public int Height { get; set; } = 30;

    /// <summary>
    /// How many times the watch registered its signal handlers, and how many registrations were disposed.
    /// </summary>
    public int Registered { get; private set; }

    public int Unregistered { get; private set; }

    /// <summary>
    /// How many keys were read (a read that found none does not count).
    /// </summary>
    public int KeysRead { get; private set; }

    /// <summary>
    /// When set, <see cref="ReadKey"/> throws it (a terminal that fails under the watch).
    /// </summary>
    public Exception? ReadFails { get; set; }

    public ConsoleKeyInfo? ReadKey()
    {
        if (ReadFails is { } fails)
        {
            throw fails;
        }

        if (!_keys.TryDequeue(out ConsoleKeyInfo key))
        {
            return null;
        }

        KeysRead++;
        return key;
    }

    public IDisposable OnSignals(Func<bool> stop, Action resize)
    {
        _stop = stop;
        _resize = resize;
        Registered++;
        return new Registration(this);
    }

    /// <summary>
    /// Presses a letter key (no modifier).
    /// </summary>
    public void Press(char c) => _keys.Enqueue(new ConsoleKeyInfo(c, (ConsoleKey)char.ToUpperInvariant(c), shift: false, alt: false, control: false));

    /// <summary>
    /// Presses Ctrl+C as a key (a terminal in raw mode delivers it so).
    /// </summary>
    public void PressControlC() => _keys.Enqueue(new ConsoleKeyInfo('\u0003', ConsoleKey.C, shift: false, alt: false, control: true));

    /// <summary>
    /// Delivers a stop signal (Ctrl+C, SIGTERM or SIGHUP); whether the default action is cancelled.
    /// </summary>
    public bool FireStop() => (_stop ?? throw new InvalidOperationException("no signal handlers registered"))();

    public void FireResize() => (_resize ?? throw new InvalidOperationException("no signal handlers registered"))();

    private sealed class Registration(FakeStatusTerminal owner) : IDisposable
    {
        public void Dispose() => owner.Unregistered++;
    }
}
