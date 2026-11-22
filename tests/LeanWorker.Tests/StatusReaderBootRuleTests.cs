using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The reader's boot and tick rules (docs/research/live-status/README.md, section 4.1): a run
/// recorded under another boot is lost whatever else is missing and no group is scanned; in the same
/// boot a worker without start ticks goes to the member scan (group and session), which gives
/// orphaned with members and lost without; unknown is only for a probe that failed.
/// </summary>
public sealed class StatusReaderBootRuleTests : IDisposable
{
    private const string Id = "20261122-110000-job-w1";
    private const string OtherBoot = "boot-OLD";

    private readonly TempDirs _dirs = new();
    private readonly FakeProcRoot _proc = new();
    private readonly FakeClock _clock = new(StatusDocs.Now);
    private readonly string _runsRoot;

    public StatusReaderBootRuleTests() => _runsRoot = _dirs.Create("lw-boot");

    public void Dispose()
    {
        _proc.Dispose();
        _dirs.Dispose();
    }

    private StatusRow Read(JsonObject doc, string? summary = null)
    {
        string dir = StatusDocs.WriteRun(_runsRoot, Id, doc, summary);
        return new StatusReader(_proc.Fs, _clock, FakeProcRoot.DefaultBoot).Read(dir);
    }

    private static JsonObject Doc(string boot = FakeProcRoot.DefaultBoot, JsonObject? worker = null) =>
        StatusDocs.Make("job-w1", boot: boot, worker: worker ?? StatusDocs.Worker());

    /// <summary>
    /// What a scan would find: the worker's group and session alive with a leader that has the
    /// recorded start ticks and one more member, and a process whose stat entry cannot be read.
    /// </summary>
    private void EverythingAliveAndOneEntryUnreadable()
    {
        _proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: StatusDocs.WorkerPid);
        _proc.AddUnreadable(450);
    }

    private static void AssertLostWithoutScan(StatusRow row)
    {
        Assert.Equal(StatusStates.Lost, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.True(row.Attention);
        Assert.Contains("another boot", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_boot_is_lost_when_the_launcher_start_ticks_are_missing()
    {
        EverythingAliveAndOneEntryUnreadable();
        JsonObject doc = Doc(OtherBoot);
        doc["launcher"]!["start_ticks"] = null;

        AssertLostWithoutScan(Read(doc));
    }

    [Fact]
    public void Another_boot_is_lost_when_the_launcher_pid_is_missing()
    {
        EverythingAliveAndOneEntryUnreadable();
        JsonObject doc = Doc(OtherBoot);
        _ = doc["launcher"]!.AsObject().Remove("pid");

        AssertLostWithoutScan(Read(doc));
    }

    [Fact]
    public void Another_boot_is_lost_when_the_launcher_has_only_a_boot_id()
    {
        EverythingAliveAndOneEntryUnreadable();
        JsonObject doc = Doc(OtherBoot);
        doc["launcher"] = new JsonObject { ["boot_id"] = OtherBoot };

        AssertLostWithoutScan(Read(doc));
    }

    [Fact]
    public void Another_boot_is_lost_when_the_launcher_pid_and_ticks_are_of_the_wrong_type()
    {
        EverythingAliveAndOneEntryUnreadable();
        JsonObject doc = Doc(OtherBoot);
        doc["launcher"]!["pid"] = "100";
        doc["launcher"]!["start_ticks"] = new JsonArray();

        AssertLostWithoutScan(Read(doc));
    }

    [Theory]
    [InlineData("no ticks")]
    [InlineData("no group")]
    [InlineData("no worker")]
    public void Another_boot_is_lost_whatever_the_worker_record_lacks_and_the_group_is_not_scanned(string lacks)
    {
        EverythingAliveAndOneEntryUnreadable();
        JsonObject worker = lacks is "no ticks"
            ? StatusDocs.Worker(ticks: null)
            : StatusDocs.Worker(pgid: null, sid: null);
        JsonObject doc = Doc(OtherBoot, worker);
        doc["launcher"]!["start_ticks"] = null;
        if (lacks is "no worker")
        {
            doc["worker"] = null;
        }

        AssertLostWithoutScan(Read(doc));
    }

    [Fact]
    public void Another_boot_is_lost_even_when_the_pid_of_the_launcher_is_alive_with_the_recorded_ticks()
    {
        _proc.Add(StatusDocs.LauncherPid, StatusDocs.LauncherTicks);

        StatusRow row = Read(Doc(OtherBoot));

        Assert.Equal(StatusStates.Lost, row.State);
        Assert.False(row.Live);
    }

    [Fact]
    public void In_the_same_boot_a_worker_without_start_ticks_whose_group_has_a_member_is_orphaned()
    {
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: StatusDocs.WorkerPid);

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)));

        Assert.Equal(StatusStates.Orphaned, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Attention);
    }

    [Fact]
    public void In_the_same_boot_a_worker_without_start_ticks_whose_leader_is_alive_is_orphaned_by_the_member_scan()
    {
        _proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)));

        Assert.Equal(StatusStates.Orphaned, row.State);
    }

    [Fact]
    public void In_the_same_boot_a_worker_without_start_ticks_and_nobody_in_the_group_is_lost_not_unknown()
    {
        _proc.Add(302, 1, pgrp: 800, session: 800);

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)));

        Assert.Equal(StatusStates.Lost, row.State);
        Assert.Equal(Liveness.Gone, row.Liveness);
        Assert.True(row.Ended);
        Assert.True(row.Attention);
    }

    [Fact]
    public void In_the_same_boot_a_worker_without_start_ticks_is_scanned_by_group_and_session_both()
    {
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: 9999);
        _proc.Add(302, 1, pgrp: 9998, session: StatusDocs.WorkerPid);

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)));

        Assert.Equal(StatusStates.Lost, row.State);
    }

    [Fact]
    public void In_the_same_boot_a_worker_without_start_ticks_or_session_cannot_be_scanned_and_is_lost()
    {
        _proc.Add(301, 1, pgrp: StatusDocs.WorkerPid, session: StatusDocs.WorkerPid);

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(sid: null, ticks: null)));

        Assert.Equal(StatusStates.Lost, row.State);
    }

    [Fact]
    public void In_the_same_boot_a_worker_without_start_ticks_with_a_process_list_that_cannot_be_read_is_unknown()
    {
        _proc.AddUnreadable(450);

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)));

        Assert.Equal(StatusStates.Unknown, row.State);
        Assert.False(row.Ended);
    }

    [Fact]
    public void Missing_ticks_alone_are_not_a_failed_probe()
    {
        // Same record as the unreadable-list case but with a readable /proc: the only difference is the probe.
        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)));

        Assert.NotEqual(StatusStates.Unknown, row.State, StringComparer.Ordinal);
    }

    [Fact]
    public void A_summary_still_decides_before_any_scan_in_the_same_boot()
    {
        EverythingAliveAndOneEntryUnreadable();

        StatusRow row = Read(Doc(worker: StatusDocs.Worker(ticks: null)), summary: "success");

        Assert.Equal("success", row.State);
    }
}
