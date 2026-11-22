using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `--since` hides only what is over and old: live, orphaned, unknown and unreadable rows are listed
/// whatever their age, the window filters ended rows with a final state (lost included) and legacy
/// rows; and the header says "1 needs attention" and "N need attention" for any other N.
/// </summary>
public sealed class StatusCommandWindowTests : IDisposable
{
    private static readonly TimeSpan _old = TimeSpan.FromDays(3);
    private static readonly TimeSpan _recent = TimeSpan.FromMinutes(10);

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly FakeClock _clock = new(StatusDocs.Now);
    private readonly string _root;

    public StatusCommandWindowTests() => _root = _dirs.Create("lw-status-window");

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private static DateTimeOffset Ago(TimeSpan by) => StatusDocs.Now - by;

    private static string Id(DateTimeOffset at, string name) => at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + name;

    private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    private async Task<(int Code, string Output)> HereAsync(params string[] args)
    {
        await using StringWriter output = new();
        string[] all = ["--here", "--runs-root", _root, .. args];
        int code = await StatusCommand.RunAsync(all, output, new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot), _root);
        return (code, output.ToString());
    }

    private static string[] Names(string json) =>
        [.. JsonNode.Parse(json)!["runs"]!.AsArray().Select(r => (r!["run"]?["name"] ?? r["name"])!.GetValue<string>())];

    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private void Ended(string name, string status, TimeSpan age)
    {
        _ = StatusDocs.WriteRun(_root, Id(Ago(age).AddMinutes(-10), name),
            StatusDocs.Make(name, phase: "done", launcherPid: 900, end: StatusDocs.End(status, Ago(age)), updatedAt: Ago(age), startedAt: Ago(age).AddMinutes(-10)));
    }

    private void Legacy(string name, TimeSpan age) => _ = StatusDocs.WriteRun(_root, Id(Ago(age), name), doc: null);

    private void Lost(string name, TimeSpan age)
    {
        _ = StatusDocs.WriteRun(_root, Id(Ago(age), name),
            StatusDocs.Make(name, boot: "boot-OLD", launcherPid: 903, worker: StatusDocs.Worker(), updatedAt: Ago(age), startedAt: Ago(age)));
    }

    /// <summary>
    /// The leader of the recorded group is alive with the recorded start ticks.
    /// </summary>
    private void OrphanedByLeader(string name, TimeSpan age)
    {
        _proc.Add(310, StatusDocs.WorkerTicks);
        _ = StatusDocs.WriteRun(_root, Id(Ago(age), name),
            StatusDocs.Make(name, launcherPid: 904, worker: StatusDocs.Worker(310, 310, 310), updatedAt: Ago(age), startedAt: Ago(age)));
    }

    /// <summary>
    /// The leader is gone and a member of the recorded group and session is not.
    /// </summary>
    private void OrphanedByMembers(string name, TimeSpan age)
    {
        _proc.Add(321, 1, pgrp: 320, session: 320);
        _ = StatusDocs.WriteRun(_root, Id(Ago(age), name),
            StatusDocs.Make(name, launcherPid: 905, worker: StatusDocs.Worker(320, 320, 320), updatedAt: Ago(age), startedAt: Ago(age)));
    }

    private void Unreadable(string name, TimeSpan age)
    {
        string dir = StatusDocs.WriteRun(_root, Id(Ago(age), name), doc: null);
        File.WriteAllText(Path.Combine(dir, "status.json"), "{ torn");
    }

    /// <summary>
    /// The launcher's /proc entry cannot be parsed, so whether the run lives is not known.
    /// </summary>
    private void Unknown(string name, TimeSpan age)
    {
        _proc.AddRaw(906, "not a stat line");
        _ = StatusDocs.WriteRun(_root, Id(Ago(age), name),
            StatusDocs.Make(name, launcherPid: 906, worker: StatusDocs.Worker(), updatedAt: Ago(age), startedAt: Ago(age)));
    }

    private void Live(string name, int launcherPid, TimeSpan age)
    {
        _proc.Add(launcherPid, StatusDocs.LauncherTicks);
        _ = StatusDocs.WriteRun(_root, Id(Ago(age), name),
            StatusDocs.Make(name, launcherPid: launcherPid, worker: StatusDocs.Worker(), updatedAt: Ago(TimeSpan.FromSeconds(5)), lastEventAt: Ago(TimeSpan.FromSeconds(5)), startedAt: Ago(age)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Since_lists_orphaned_and_unreadable_rows_at_any_age_and_hides_only_old_ended_lost_and_legacy_rowsAsync(bool old)
    {
        TimeSpan age = old ? _old : _recent;
        OrphanedByLeader("orphaned-leader", age);
        OrphanedByMembers("orphaned-members", age);
        Unreadable("unreadable", age);
        Ended("ended-success", "success", age);
        Ended("ended-failed", "error", age);
        Lost("lost", age);
        Legacy("legacy", age);

        (int code, string json) = await HereAsync("--since", "1h", "--json");
        (_, string plain) = await HereAsync("--since", "1h");

        Assert.Equal(0, code);
        string[] listed = Sorted(Names(json));
        if (old)
        {
            Assert.Equal(["orphaned-leader", "orphaned-members", "unreadable"], listed);
            Assert.Contains("4 older than 1h not shown", Lines(plain)[0], StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(["ended-failed", "ended-success", "legacy", "lost", "orphaned-leader", "orphaned-members", "unreadable"], listed);
            Assert.DoesNotContain("not shown", Lines(plain)[0], StringComparison.Ordinal);
        }

        Assert.Equal(listed.Length + 1, Lines(plain).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Since_lists_an_unknown_row_at_any_age_and_hides_old_ended_and_legacy_rowsAsync(bool old)
    {
        TimeSpan age = old ? _old : _recent;
        Unknown("unknown", age);
        Ended("ended-success", "success", age);
        Legacy("legacy", age);

        (_, string json) = await HereAsync("--since", "1h", "--json");

        JsonObject unknown = JsonNode.Parse(json)!["runs"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["run"]?["name"]?.GetValue<string>() is "unknown");
        Assert.Equal("unknown", unknown["state"]!.GetValue<string>());
        Assert.Equal(old ? ["unknown"] : ["ended-success", "legacy", "unknown"], Sorted(Names(json)));
    }

    [Fact]
    public async Task A_live_row_is_listed_at_any_age_beside_old_rows_that_are_hiddenAsync()
    {
        Live("live-old", 130, _old);
        Ended("ended-old", "success", _old);
        Ended("ended-recent", "success", _recent);

        (_, string json) = await HereAsync("--since", "1h", "--json");

        Assert.Equal(["ended-recent", "live-old"], Sorted(Names(json)));
    }

    [Fact]
    public async Task The_default_window_keeps_an_old_orphaned_run_tooAsync()
    {
        OrphanedByLeader("orphaned-leader", TimeSpan.FromDays(30));
        Unreadable("unreadable", TimeSpan.FromDays(30));
        Ended("ended-old", "error", TimeSpan.FromDays(30));

        (_, string json) = await HereAsync("--json");

        Assert.Equal(["orphaned-leader", "unreadable"], Sorted(Names(json)));
    }

    [Theory]
    [InlineData(0, "0 need attention")]
    [InlineData(1, "1 needs attention")]
    [InlineData(2, "2 need attention")]
    [InlineData(3, "3 need attention")]
    [InlineData(11, "11 need attention")]
    public async Task The_header_says_needs_for_one_row_and_need_for_any_other_countAsync(int failed, string expected)
    {
        Live("live", 131, TimeSpan.FromMinutes(5));
        for (int i = 0; i < failed; i++)
        {
            Ended("failed-" + i.ToString(CultureInfo.InvariantCulture), "error", TimeSpan.FromMinutes(20 + i));
        }

        (_, string plain) = await HereAsync();

        string header = Lines(plain)[0];
        Assert.Equal($"lean-worker status: {_root}, 1 live, {failed.ToString(CultureInfo.InvariantCulture)} ended, {expected}", header);
        Assert.Equal(failed + 2, Lines(plain).Length);
    }

    [Fact]
    public async Task The_header_counts_a_row_that_needs_attention_whatever_makes_it_soAsync()
    {
        Live("live", 132, TimeSpan.FromMinutes(5));
        OrphanedByLeader("orphaned", TimeSpan.FromMinutes(20));

        (_, string plain) = await HereAsync();

        Assert.EndsWith("1 live, 1 ended, 1 needs attention", Lines(plain)[0], StringComparison.Ordinal);
    }
}
