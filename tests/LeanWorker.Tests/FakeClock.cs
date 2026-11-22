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

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
            _timestamp += by.Ticks;
        }
    }
}
