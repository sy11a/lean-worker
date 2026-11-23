using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The handler's write is bounded (issue #146 fix round 1, point 2): the wait for the lock and the write together stay
/// within about a second, so a signal's default action is never held up for longer; a handler that gave up leaves the
/// file as it was. A stalled write is made with the one seam the writer offers, its clock: the time of the end record is
/// read inside the write, so a clock that does not answer stands for a disk that does not. (A FIFO cannot stand in: the
/// write goes to a temp file with a random name and is renamed over status.json.)
/// </summary>
public sealed class RunStatusSignalBoundTests : IDisposable
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(2.5);

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly BlockingClock _clock = new(StatusDocs.Now, TestContext.Current.CancellationToken);
    private readonly string _runDir;
    private readonly RunStatus _status;

    public RunStatusSignalBoundTests()
    {
        _proc.Add(4000, 7432110);
        _runDir = _dirs.Create("lw-bound");
        _status = new RunStatus(new RunStatusSpec(_runDir, "bound", "bound", 1, Profile: null, "opencode", "anthropic", "m", 3m, WrapUpUsd: null, 60,
            StatusDocs.Now, Worktree: null), _proc.Fs, _clock, 4000);
        _status.Phase(RunPhase.Worker);
    }

    public void Dispose()
    {
        _clock.Release();
        _clock.Dispose();
        _proc.Dispose();
        _dirs.Dispose();
    }

    /// <summary>
    /// A clock whose next read of the time does not return until it is released (or 30 s pass).
    /// </summary>
    private sealed class BlockingClock(DateTimeOffset now, CancellationToken token) : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _armed;

        public ManualResetEventSlim Entered { get; } = new();

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _armed, 0) is 1)
            {
                Entered.Set();
                _ = _release.Wait(TimeSpan.FromSeconds(30), token);
            }

            return now;
        }

        public void Dispose()
        {
            _release.Dispose();
            Entered.Dispose();
        }
    }

    private string StatusPath => Path.Combine(_runDir, "status.json");

    private static object LockOf(RunStatus status) =>
        typeof(RunStatus).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(status)!;

    private static void HoldLock(object gate, ManualResetEventSlim held, ManualResetEventSlim release)
    {
        lock (gate)
        {
            held.Set();
            _ = release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    // Waits for a write in progress to be over: the lock is free again then (the wait is bounded).
    private async Task LockIsFreeAsync() =>
        _ = await Task.Run(() => _status.Seq, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

    [Fact]
    public async Task With_the_lock_held_by_a_status_write_the_handler_returns_within_about_a_second_and_nothing_lands_afterwardsAsync()
    {
        byte[] before = await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken);
        long seq = _status.Seq;
        object gate = LockOf(_status);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() => HoldLock(gate, held, release)) { IsBackground = true, Name = "test-lock-holder" };
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Stopwatch watch = Stopwatch.StartNew();

            bool recorded = _status.Signalled(PosixSignal.SIGTERM);
            watch.Stop();

            Assert.False(recorded);
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(800), _bound);
            Assert.Equal(before, await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
        }

        // The lock is free now: a late write of the abandoned handler would land here, in the next moment.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(before, await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken));
        Assert.Equal(seq, _status.Seq);
    }

    [Fact]
    public async Task A_handler_whose_own_write_blocks_gives_up_within_about_a_second_and_the_file_is_untouched_meanwhileAsync()
    {
        byte[] before = await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken);
        _clock.Arm();
        try
        {
            Stopwatch watch = Stopwatch.StartNew();

            bool recorded = _status.Signalled(PosixSignal.SIGTERM);
            watch.Stop();

            // The handler was inside the write (it got the lock and went on to the clock), and it still came back.
            Assert.True(_clock.Entered.IsSet, "the write never reached the stalled step");
            Assert.False(recorded);
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(800), _bound);
            Assert.Equal(before, await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            _clock.Release();
            await LockIsFreeAsync();
        }
    }

    [Fact]
    public async Task A_write_that_was_blocked_when_the_handler_gave_up_does_not_land_after_it_is_releasedAsync()
    {
        byte[] before = await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken);
        _clock.Arm();

        bool recorded = _status.Signalled(PosixSignal.SIGTERM);
        _clock.Release();
        await LockIsFreeAsync();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.False(recorded);
        Assert.Equal(before, await File.ReadAllBytesAsync(StatusPath, TestContext.Current.CancellationToken));
    }
}
