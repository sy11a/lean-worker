// The time one `status` scan may spend waiting on files (issue #143): the run dirs and the pointer
// index are writable by any process of the user, so any lstat, stat, open or read in them can block
// (a FIFO, a leased file, a FUSE mount). Every such step runs on a thread of its own and the scan
// waits for it only until a deadline: the whole scan has one budget (Total, 10 s by default), and
// once it is used up every further step still gets Floor (250 ms by default), so one blocked file
// costs the budget once and later blocked files cost little each. A thread that is given up on is
// a background thread: it ends when its call returns (closing what it opened) or with the process.

namespace LeanWorker;

internal sealed class ReadBudget
{
    public static readonly TimeSpan DefaultTotal = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultFloor = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time;
    private readonly long _start;

    /// <summary>
    /// A budget of <paramref name="total"/> for the scan and <paramref name="floor"/> for each step
    /// once it is used up, measured with <paramref name="time"/> (the system's monotonic clock when null).
    /// </summary>
    public ReadBudget(TimeSpan total, TimeSpan floor, TimeProvider? time = null)
    {
        Total = total;
        Floor = floor;
        _time = time ?? TimeProvider.System;
        _start = _time.GetTimestamp();
    }

    public TimeSpan Total { get; }

    public TimeSpan Floor { get; }

    /// <summary>
    /// A fresh budget with the default limits.
    /// </summary>
    public static ReadBudget Default() => new(DefaultTotal, DefaultFloor);

    /// <summary>
    /// How long the next step may take: what is left of the budget, at least <see cref="Floor"/>.
    /// </summary>
    public TimeSpan Next()
    {
        TimeSpan left = Total - _time.GetElapsedTime(_start);
        return left > Floor ? left : Floor;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own and waits for it until <see cref="Next"/>
    /// has passed. True with its result when it returned in time (an exception it threw is thrown
    /// here as the inner exception of an <see cref="IOException"/>); false when it did not, and the
    /// thread is left to finish on its own.
    /// </summary>
    public bool Run<T>(Func<T> work, out T? result)
    {
        Pending<T> pending = new(work);
        Thread thread = new(static p => ((Pending<T>)p!).Execute()) { IsBackground = true, Name = "status-read" };
        thread.Start(pending);
        return pending.Take(Next(), out result);
    }

    /// <summary>
    /// A directory listing under the deadline: <paramref name="walk"/> hands every entry it keeps
    /// to the function it gets, and the entries found until the deadline are kept when it does not
    /// finish (Complete is false then, and also when the listing failed).
    /// </summary>
    public (List<string> Entries, bool Complete) List(Action<Action<string>> walk)
    {
        List<string> found = [];
        bool complete;
        try
        {
            complete = Run(() =>
            {
                walk(entry =>
                {
                    lock (found)
                    {
                        found.Add(entry);
                    }
                });
                return true;
            }, out bool _);
        }
        catch (IOException)
        {
            complete = false;
        }

        lock (found)
        {
            return ([.. found], complete);
        }
    }

    private sealed class Pending<T>(Func<T> work)
    {
        private readonly object _gate = new();
        private bool _done;
        private T? _value;
        private Exception? _error;

        public void Execute()
        {
            T? value = default;
            Exception? error = null;
            try
            {
                value = work();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException and not StackOverflowException)
            {
                error = ex;
            }

            lock (_gate)
            {
                _value = value;
                _error = error;
                _done = true;
                Monitor.PulseAll(_gate);
            }
        }

        public bool Take(TimeSpan timeout, out T? result)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            lock (_gate)
            {
                while (!_done)
                {
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0)
                    {
                        result = default;
                        return false;
                    }

                    _ = Monitor.Wait(_gate, TimeSpan.FromMilliseconds(left));
                }

                if (_error is not null)
                {
                    throw new IOException("a status read failed", _error);
                }

                result = _value;
                return true;
            }
        }
    }
}
