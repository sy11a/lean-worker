using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The read budget of a scan (issue #143): every step that can block (a read of a file, of /proc) runs on a thread
/// with a deadline; a scan has one total budget and, once it is used up, each further step gets only the floor.
/// A step that does not answer is an unreadable row, later runs are still listed, and the run dir is checked for
/// being a link again after its files were read. The blocking file is a FIFO in the fake /proc where the launcher's
/// stat line should be: the reader opens it and waits for ever.
/// </summary>
public sealed class StatusReadBudgetTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(30);

    private readonly StatusIndexRig _rig = new();
    private readonly List<string> _fifos = [];

    public void Dispose() => _rig.Dispose();

    // A step that waits for the test to let it go (or for the bound), as a blocked file read would.
    private static bool Block(ManualResetEventSlim release) => release.Wait(_bound, TestContext.Current.CancellationToken);

    [Fact]
    public void A_step_that_answers_in_time_gives_its_value()
    {
        ReadBudget budget = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));

        Assert.True(budget.Run(() => 42, out int value));

        Assert.Equal(42, value);
    }

    [Fact]
    public void A_step_that_throws_is_an_io_error_with_the_exception_inside()
    {
        ReadBudget budget = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));

        IOException ex = Assert.Throws<IOException>(() => budget.Run<int>(() => throw new InvalidOperationException("secret"), out _));

        _ = Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void A_step_that_blocks_returns_false_when_the_budget_has_passed_and_the_thread_is_left_to_finish()
    {
        using ManualResetEventSlim release = new();
        ReadBudget budget = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));
        Stopwatch watch = Stopwatch.StartNew();

        bool answered = budget.Run(() =>
        {
            _ = Block(release);
            return 1;
        }, out int _);
        watch.Stop();
        release.Set();

        Assert.False(answered);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Once_the_budget_is_used_up_each_step_gets_only_the_floor()
    {
        using ManualResetEventSlim release = new();
        ReadBudget budget = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));
        _ = budget.Run(() => Block(release), out bool _);
        Stopwatch watch = Stopwatch.StartNew();

        bool second = budget.Run(() => Block(release), out bool _);
        bool third = budget.Run(() => Block(release), out bool _);
        watch.Stop();
        release.Set();

        Assert.False(second);
        Assert.False(third);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_next_allowance_is_what_is_left_of_the_budget_and_never_less_than_the_floor()
    {
        FakeClock clock = new(StatusDocs.Now);
        ReadBudget budget = new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250), clock);

        Assert.Equal(TimeSpan.FromSeconds(10), budget.Next());
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(TimeSpan.FromSeconds(1), budget.Next());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(250), budget.Next());
    }

    [Fact]
    public void A_listing_that_blocks_keeps_the_entries_found_so_far_and_says_it_is_incomplete()
    {
        using ManualResetEventSlim release = new();
        ReadBudget budget = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));

        (List<string> entries, bool complete) = budget.List(add =>
        {
            add("first");
            add("second");
            _ = Block(release);
            add("never");
        });
        release.Set();

        Assert.False(complete);
        Assert.Equal(["first", "second"], entries);
    }

    [Fact]
    public void A_listing_that_fails_keeps_what_it_found_and_is_incomplete_and_one_that_finishes_is_complete()
    {
        ReadBudget budget = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));

        (List<string> failed, bool failedComplete) = budget.List(add =>
        {
            add("a");
            throw new IOException("boom");
        });
        (List<string> done, bool doneComplete) = budget.List(add => add("a"));

        Assert.False(failedComplete);
        Assert.Equal(["a"], failed);
        Assert.True(doneComplete);
        Assert.Equal(["a"], done);
    }

    private async Task<StatusIndexRig.Added> BlockedRunAsync(string name, int pid, int minutesAgo)
    {
        StatusIndexRig.Added run = _rig.Add(name, _mine, startedAgo: TimeSpan.FromMinutes(minutesAgo), tweak: d => d["launcher"]!["pid"] = pid);
        string dir = Path.Combine(_rig.Proc.Root, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(dir);
        string fifo = Path.Combine(dir, "stat");
        await Fifo.MakeAsync(fifo);
        _fifos.Add(fifo);
        return run;
    }

    // Lets the reader threads that are blocked on the FIFOs go (they see the end of the file), so none is left behind.
    private async Task ReleaseAsync()
    {
        foreach (string fifo in _fifos)
        {
            await using FileStream? writer = await Fifo.WaitForReaderAsync(fifo, TimeSpan.FromSeconds(5));
        }

        _fifos.Clear();
    }

    [Fact]
    public async Task A_run_that_does_not_answer_is_an_unreadable_row_within_the_budget_and_the_runs_after_it_are_still_listedAsync()
    {
        _rig.ScanBudget = TimeSpan.FromSeconds(3);
        _rig.ReadFloor = TimeSpan.FromMilliseconds(100);
        _ = _rig.Add("ok-first", _mine, startedAgo: TimeSpan.FromMinutes(50));
        _ = await BlockedRunAsync("blocked-one", 777, 40);
        _ = await BlockedRunAsync("blocked-two", 778, 30);
        _ = _rig.Add("ok-last", _mine, startedAgo: TimeSpan.FromMinutes(20));
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            (int code, string json) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--json");
            watch.Stop();

            Assert.Equal(0, code);
            Dictionary<string, JsonObject> rows = StatusIndexRig.JsonRows(json).ToDictionary(r => r["run"]?["name"]?.GetValue<string>() ?? r["name"]!.GetValue<string>(), StringComparer.Ordinal);
            Assert.Equal(["blocked-one", "blocked-two", "ok-first", "ok-last"], [.. rows.Keys.Order(StringComparer.Ordinal)]);
            Assert.Equal("running", rows["ok-first"]["state"]!.GetValue<string>());
            Assert.Equal("running", rows["ok-last"]["state"]!.GetValue<string>());
            foreach (string blocked in new[] { "blocked-one", "blocked-two" })
            {
                Assert.Equal("unreadable", rows[blocked]["state"]!.GetValue<string>());
                Assert.Equal(StatusReader.BlockedDetail, rows[blocked]["detail"]!.GetValue<string>());
            }

            // The first blocked run took the budget (3 s); the second only the floor (0.1 s). A second full budget would be over 6 s.
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(5000));
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    [Fact]
    public async Task A_blocked_run_is_an_unreadable_row_in_the_plain_listing_and_the_output_stays_cleanAsync()
    {
        _rig.ScanBudget = TimeSpan.FromMilliseconds(500);
        _rig.ReadFloor = TimeSpan.FromMilliseconds(100);
        _ = await BlockedRunAsync("blocked-one", 777, 40);
        try
        {
            (int code, string plain) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"));

            Assert.Equal(0, code);
            string row = Assert.Single(TerminalAssert.AssertPlain(plain, 1)[1..]);
            Assert.StartsWith("unreadable", row, StringComparison.Ordinal);
            Assert.Contains("1 needs attention", plain, StringComparison.Ordinal);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    [Fact]
    public async Task A_run_dir_swapped_for_a_link_while_its_files_are_read_is_refused_after_the_readsAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        _rig.ScanBudget = TimeSpan.FromSeconds(20);
        StatusIndexRig.Added run = await BlockedRunAsync("swapped", 779, 10);
        string fifo = _fifos[0];
        Task<(int Code, string Output)> call = _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--json");
        await using (FileStream? writer = await Fifo.WaitForReaderAsync(fifo, TimeSpan.FromSeconds(10)))
        {
            Assert.NotNull(writer);

            // The reader has read status.json and waits for the launcher's stat line: the run dir is replaced now.
            string moved = Path.Combine(_rig.Outside(), Path.GetFileName(run.Dir));
            Directory.Move(run.Dir, moved);
            _ = Directory.CreateSymbolicLink(run.Dir, moved);
        }

        (int code, string json) = await call.WaitAsync(_bound, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        JsonObject row = Assert.Single(StatusIndexRig.JsonRows(json));
        Assert.Equal("unreadable", row["state"]!.GetValue<string>());
        Assert.Contains("became a symbolic link", row["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(row["run"]);
    }
}
