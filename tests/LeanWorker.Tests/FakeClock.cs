namespace LeanWorker.Tests;

/// <summary>
/// A clock a test moves by hand: wall time and the monotonic timestamp advance together, in UTC, so
/// code under test never reads the real clock. Safe to use from several threads.
/// </summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now = start;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _timestamp;
        }
    }

    /// <summary>
    /// Time that passed on the monotonic timestamp since the clock was made (a wall step does not count).
    /// </summary>
    public TimeSpan Monotonic => GetElapsedTime(0);

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
            _timestamp += by.Ticks;
        }
    }

    /// <summary>
    /// The wall clock steps by <paramref name="by"/> (back with a negative value) and the monotonic timestamp does not
    /// move: what a clock change by an operator or by time synchronisation does.
    /// </summary>
    public void StepWall(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }
    }
}
