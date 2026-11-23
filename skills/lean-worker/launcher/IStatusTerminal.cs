namespace LeanWorker;

/// <summary>
/// The terminal `status --watch` draws on, apart from its output writer: whether output and input
/// are redirected, the window's size, the key waiting to be read, and the signals that stop the
/// watch or resize the window. <see cref="ConsoleTerminal"/> is the process's own; a test passes its own.
/// </summary>
internal interface IStatusTerminal
{
    /// <summary>
    /// Whether standard output is not a terminal (then --watch prints one line per change, no escapes).
    /// </summary>
    bool OutputRedirected { get; }

    /// <summary>
    /// The window's width in columns; read every frame.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// The window's height in rows; read every frame.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// The key waiting on standard input, read without echo; null when none is waiting or input is
    /// not a terminal.
    /// </summary>
    ConsoleKeyInfo? ReadKey();

    /// <summary>
    /// Registers the handlers for the stop signals (Ctrl+C, SIGTERM, SIGHUP) and the window's
    /// resize until the result is disposed. <paramref name="stop"/> returns whether the signal's
    /// default action (ending the process) is to be cancelled.
    /// </summary>
    IDisposable OnSignals(Func<bool> stop, Action resize);
}
