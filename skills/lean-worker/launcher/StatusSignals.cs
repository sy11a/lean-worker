// The record-only signal handler of a launch (docs/research/live-status/README.md, slice 5): on
// SIGTERM, SIGINT and SIGHUP the status file records the signal, and the signal's default action
// then proceeds as without the handler (the context is never cancelled), so the launcher ends
// exactly as it did before. Whether the worker group should be stopped too belongs to issue #121.
//
// A signal the launcher inherited as ignored (nohup, trap '' HUP) gets no handler: it stays
// ignored, nothing is recorded, and the worker inherits the dispositions it had without the
// writer. When the ignored set cannot be read, no handler is registered at all. Observed on the
// .NET 11 runtime the launcher rolls forward to: an inherited ignored SIGHUP stays in SigIgn, but
// the runtime's own SIGTERM handler is installed before Main, so an ignored SIGTERM is not in
// SigIgn; a handler for it runs and the launcher survives, which is why RunStatus drops a
// recorded end as soon as the launcher writes again.

using System.Runtime.InteropServices;

namespace LeanWorker;

internal sealed class StatusSignals : IDisposable
{
    private static readonly PosixSignal[] _signals = [PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP];

    private readonly List<PosixSignalRegistration> _registrations;

    private StatusSignals(List<PosixSignalRegistration> registrations) => _registrations = registrations;

    /// <summary>
    /// Registers <paramref name="record"/> for each of SIGTERM, SIGINT and SIGHUP that
    /// <paramref name="ignored"/> (the <c>SigIgn</c> mask, <see cref="ProcFs.IgnoredSignals"/>) does
    /// not hold; for none when the mask is null. The handler gets the signal and must return without
    /// throwing. The context's Cancel stays false, so the default action follows.
    /// </summary>
    public static StatusSignals Register(Action<PosixSignal> record, ulong? ignored)
    {
        List<PosixSignalRegistration> registrations = [];
        if (ignored is not { } mask)
        {
            return new StatusSignals(registrations);
        }

        try
        {
            foreach (PosixSignal signal in _signals.Where(s => !Ignored(mask, s)))
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
    /// Whether <paramref name="mask"/> holds <paramref name="signal"/>: Linux numbers SIGHUP 1,
    /// SIGINT 2 and SIGTERM 15, bit <c>n - 1</c>. A signal without a number here counts as ignored.
    /// </summary>
    internal static bool Ignored(ulong mask, PosixSignal signal)
    {
        int number = signal switch
        {
            PosixSignal.SIGHUP => 1,
            PosixSignal.SIGINT => 2,
            PosixSignal.SIGTERM => 15,
            _ => 0,
        };
        return number is 0 || (mask & (1UL << (number - 1))) != 0;
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
