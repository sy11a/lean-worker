// The record-only signal handler of a launch (docs/research/live-status/README.md, slice 5): on
// SIGTERM, SIGINT and SIGHUP the status file records the signal, and the signal's default action
// then proceeds as without the handler (the context is never cancelled), so the launcher ends
// exactly as it did before. Whether the worker group should be stopped too belongs to issue #121.

using System.Runtime.InteropServices;

namespace LeanWorker;

internal sealed class StatusSignals : IDisposable
{
    private static readonly PosixSignal[] _signals = [PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP];

    private readonly List<PosixSignalRegistration> _registrations;

    private StatusSignals(List<PosixSignalRegistration> registrations) => _registrations = registrations;

    /// <summary>
    /// Registers <paramref name="record"/> for SIGTERM, SIGINT and SIGHUP; it gets the signal and
    /// must return without throwing. The context's Cancel stays false, so the default action follows.
    /// </summary>
    public static StatusSignals Register(Action<PosixSignal> record)
    {
        List<PosixSignalRegistration> registrations = [];
        try
        {
            foreach (PosixSignal signal in _signals)
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context => record(context.Signal)));
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // A signal the platform does not let the launcher watch is simply not recorded.
        }

        return new StatusSignals(registrations);
    }

    /// <summary>
    /// The name a status file records for a signal: SIGTERM, SIGINT, SIGHUP.
    /// </summary>
    public static string Name(PosixSignal signal)
    {
        return signal switch
        {
            PosixSignal.SIGTERM => "SIGTERM",
            PosixSignal.SIGINT => "SIGINT",
            PosixSignal.SIGHUP => "SIGHUP",
            _ => "signal",
        };
    }

    public void Dispose()
    {
        foreach (PosixSignalRegistration registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
    }
}
