using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// ProcFs over a fake /proc root: every probe is present, provably absent or failed, and only the
/// first two may end a run. The real /proc is never read.
/// </summary>
public sealed class ProcFsTests : IDisposable
{
    private readonly FakeProcRoot _proc = new();

    public void Dispose() => _proc.Dispose();

    [Fact]
    public void A_process_with_a_stat_line_is_present()
    {
        _proc.Add(50, startTicks: 777, pgrp: 40, session: 30);

        ProcProbe probe = _proc.Fs.Stat(50);

        Assert.Equal(ProbeKind.Present, probe.Kind);
        Assert.Equal(new ProcStat('S', 40, 30, 777), probe.Stat);
    }

    [Fact]
    public void A_process_without_an_entry_is_provably_absent() =>
        Assert.Equal(ProbeKind.Absent, _proc.Fs.Stat(51).Kind);

    [Fact]
    public void A_stat_line_of_the_wrong_shape_is_a_failed_probe_not_an_absence()
    {
        _proc.AddRaw(52, "not a stat line");

        ProcProbe probe = _proc.Fs.Stat(52);

        Assert.Equal(ProbeKind.Failed, probe.Kind);
        Assert.False(string.IsNullOrEmpty(probe.Error));
    }

    [Fact]
    public void A_stat_entry_that_cannot_be_read_is_a_failed_probe_not_an_absence()
    {
        _proc.AddUnreadable(53);

        Assert.Equal(ProbeKind.Failed, _proc.Fs.Stat(53).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void No_recorded_pid_is_a_failed_probe(int pid) =>
        Assert.Equal(ProbeKind.Failed, _proc.Fs.Stat(pid).Kind);

    [Fact]
    public void The_boot_id_is_read_trimmed() =>
        Assert.Equal(FakeProcRoot.DefaultBoot, _proc.Fs.BootId());

    [Fact]
    public void A_missing_or_empty_boot_id_is_null()
    {
        using FakeProcRoot none = new(bootId: null);
        using FakeProcRoot empty = new(bootId: "  ");

        Assert.Null(none.Fs.BootId());
        Assert.Null(empty.Fs.BootId());
    }

    [Fact]
    public void A_member_of_the_group_and_session_is_found()
    {
        _proc.Add(60, 1, pgrp: 300, session: 300);

        Assert.True(_proc.Fs.HasGroupMember(300, 300));
    }

    [Fact]
    public void A_scan_that_reads_every_entry_and_finds_no_member_says_false()
    {
        _proc.Add(61, 1, pgrp: 300, session: 999);
        _proc.Add(62, 1, pgrp: 999, session: 300);

        Assert.False(_proc.Fs.HasGroupMember(300, 300));
    }

    [Fact]
    public void A_zombie_is_not_a_member()
    {
        _proc.Add(63, 1, state: 'Z', pgrp: 300, session: 300);

        Assert.False(_proc.Fs.HasGroupMember(300, 300));
    }

    [Fact]
    public void An_entry_that_cannot_be_read_makes_a_negative_scan_unknown()
    {
        _proc.Add(64, 1, pgrp: 999, session: 999);
        _proc.AddUnreadable(65);

        Assert.Null(_proc.Fs.HasGroupMember(300, 300));
    }

    [Fact]
    public void A_member_found_wins_over_an_entry_that_cannot_be_read()
    {
        _proc.AddUnreadable(66);
        _proc.Add(67, 1, pgrp: 300, session: 300);

        Assert.True(_proc.Fs.HasGroupMember(300, 300));
    }

    [Fact]
    public void Non_numeric_entries_of_the_root_are_not_processes()
    {
        _proc.AddRaw(68, FakeProcRoot.Line(68, "w", 'S', 1, 1, 1));
        _ = Directory.CreateDirectory(Path.Combine(_proc.Root, "self"));

        Assert.False(_proc.Fs.HasGroupMember(300, 300));
    }

    [Fact]
    public void A_root_that_cannot_be_listed_makes_the_scan_unknown()
    {
        ProcFs missing = new(Path.Combine(_proc.Root, "no-such-root"));

        Assert.Null(missing.HasGroupMember(300, 300));
    }
}
