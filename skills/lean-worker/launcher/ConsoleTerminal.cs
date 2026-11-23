// The process's own terminal for `status --watch`: System.Console and POSIX signal registrations,
// host APIs only (docs/research/live-status/README.md, section 4.3).

using System.Runtime.InteropServices;

namespace LeanWorker;

internal sealed class ConsoleTerminal : IStatusTerminal
{
    /// <summary>
    /// The size assumed when the window's cannot be read.
    /// </summary>
    private const int FallbackWidth = 80;

    private const int FallbackHeight = 24;

    public bool OutputRedirected => Console.IsOutputRedirected;

    public int Width => Size(() => Console.WindowWidth, FallbackWidth);

    public int Height => Size(() => Console.WindowHeight, FallbackHeight);

    public ConsoleKeyInfo? ReadKey()
    {
        try
        {
            return !Console.IsInputRedirected && Console.KeyAvailable ? Console.ReadKey(intercept: true) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public IDisposable OnSignals(Func<bool> stop, Action resize) => new Registrations(stop, resize);

    private static int Size(Func<int> read, int fallback)
    {
        try
        {
            int size = read();
            return size > 0 ? size : fallback;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// The handlers of one watch: Ctrl+C, SIGTERM and SIGHUP stop it (each cancels the default
    /// action when the watch says so); SIGWINCH, where there is one, redraws.
    /// </summary>
    private sealed class Registrations(Func<bool> stop, Action resize) : IDisposable
    {
        private readonly PosixSignalRegistration _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = stop());
        private readonly PosixSignalRegistration _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = stop());
        private readonly PosixSignalRegistration _hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = stop());
        private readonly PosixSignalRegistration? _resize = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => resize());

        public void Dispose()
        {
            _interrupt.Dispose();
            _terminate.Dispose();
            _hangUp.Dispose();
            _resize?.Dispose();
        }
    }
}
