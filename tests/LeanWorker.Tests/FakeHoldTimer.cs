namespace LeanWorker.Tests;

/// <summary>
/// The one-shot timer seam of the signal hold (<see cref="RunStatus.HoldTimer"/>) in a test's hand: it records every
/// timer the writer starts and fires one only when the test says so, so no real two seconds are waited. A timer that
/// was disposed can still be fired, as a callback already on its way would be.
/// </summary>
internal sealed class FakeHoldTimer
{
    private readonly List<Started> _started = [];

    /// <summary>
    /// One timer the writer started.
    /// </summary>
    internal sealed class Started(TimeSpan due, Action fire) : IDisposable
    {
        public TimeSpan Due { get; } = due;

        public bool Disposed { get; private set; }

        public void Fire() => fire();

        public void Dispose() => Disposed = true;
    }

    public IReadOnlyList<Started> All => _started;

    public Started Last => _started[^1];

    /// <summary>
    /// Wires the seam of <paramref name="status"/> to this fake.
    /// </summary>
    public FakeHoldTimer Attach(RunStatus status)
    {
        status.HoldTimer = (due, fire) => Start(due, fire);
        return this;
    }

    private Started Start(TimeSpan due, Action fire)
    {
        Started timer = new(due, fire);
        _started.Add(timer);
        return timer;
    }
}
