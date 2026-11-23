using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The leftover reap (issue #160, StatusIndex.ReapLeftovers, called from StatusIndexScan.Read with reap): a ".claim-*"
/// or ".tmp-*" regular file directly in the index folder whose own last write time is more than 7 days before the
/// reader's clock is deleted; exactly 7 days, younger, or a time in the future stays; a directory or a link of such a
/// name stays however old (and so does the link's target); other names stay; a pointer is never removed by it; a
/// fresh claim is never reaped, and a Write of its name still takes the next suffix. The reader's clock is the rig's
/// fake one, the files' ages are set with File.SetLastWriteTimeUtc: no verdict depends on how long anything takes.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusIndexLeftoverReapTests : IDisposable
{
    private const string Name = "20261122-135850-1-r1.json";
    private const string Stem = "20261122-135850-1-r1";
    private const string OtherName = "20261122-135850-2-r1.json";

    private static readonly string[] _mine = ["claude-session:sess-A"];
    private static readonly TimeSpan _week = TimeSpan.FromDays(7);
    private static readonly TimeSpan _second = TimeSpan.FromSeconds(1);
    private static readonly DateTimeOffset _started = new(2026, 11, 22, 13, 58, 50, TimeSpan.Zero);

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static JsonObject Pointer(string runDir) =>
        StatusIndex.Pointer(_started, runDir, "chain-1", parentRun: null, ["claude-session:s1", "pid:1@2"], new JsonObject { ["pid"] = 41233, ["start_ticks"] = 7432110, ["boot_id"] = "b" });

    private static string SuffixName(int k) => Stem + "-" + k.ToString(CultureInfo.InvariantCulture) + ".json";

    private static string[] Sorted(params string[] names) => [.. names.Order(StringComparer.Ordinal)];

    private static string[] EntryNames(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    private void SetAge(string path, TimeSpan age)
    {
        DateTime time = _rig.Clock.GetUtcNow().UtcDateTime - age;
        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, time);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, time);
        }
    }

    /// <summary>
    /// A regular file of the index folder with <paramref name="age"/> (before the reader's clock; negative: after it).
    /// </summary>
    private string Make(string name, TimeSpan age, string content = "leftover")
    {
        _ = Directory.CreateDirectory(_rig.Folder);
        string path = Path.Combine(_rig.Folder, name);
        File.WriteAllText(path, content);
        SetAge(path, age);
        return path;
    }

    private (List<StatusRow> Rows, bool Complete) Scan(string? folder, bool reap)
    {
        StatusReader reader = new(_rig.Proc.Fs, _rig.Clock, FakeProcRoot.DefaultBoot);
        return StatusIndexScan.Read(folder, keys: null, reader, ReadBudget.Default(), reap);
    }

    [Theory]
    [InlineData(".claim-")]
    [InlineData(".tmp-")]
    public void A_leftover_file_older_than_7_days_is_deleted_and_one_at_the_age_or_younger_or_from_the_future_stays(string prefix)
    {
        string[] gone = [Make(prefix + "week-and-1s", _week + _second), Make(prefix + "month", TimeSpan.FromDays(30))];
        string[] kept =
        [
            Make(prefix + "exactly-7d", _week),
            Make(prefix + "week-less-1s", _week - _second),
            Make(prefix + "hour", TimeSpan.FromHours(1)),
            Make(prefix + "now", TimeSpan.Zero),
            Make(prefix + "future-1s", -_second),
            Make(prefix + "future-month", TimeSpan.FromDays(-30)),
        ];

        (List<StatusRow> rows, bool complete) = Scan(_rig.Folder, reap: true);

        Assert.True(complete);
        Assert.Empty(rows);
        Assert.All(gone, p => Assert.False(File.Exists(p), p));
        Assert.All(kept, p => Assert.True(File.Exists(p), p));
        Assert.All(kept, p => Assert.Equal("leftover", File.ReadAllText(p)));
    }

    [Theory]
    [InlineData(".claim-")]
    [InlineData(".tmp-")]
    public void A_directory_of_a_leftover_name_stays_with_what_is_in_it_however_old(string prefix)
    {
        string dir = Path.Combine(_rig.Folder, prefix + "x");
        _ = Directory.CreateDirectory(dir);
        string inner = Path.Combine(dir, "inner.txt");
        File.WriteAllText(inner, "inner");
        string innerLeftover = Path.Combine(dir, prefix + "inner");
        File.WriteAllText(innerLeftover, "inner leftover");
        SetAge(inner, TimeSpan.FromDays(60));
        SetAge(innerLeftover, TimeSpan.FromDays(60));
        SetAge(dir, TimeSpan.FromDays(60));

        _ = Scan(_rig.Folder, reap: true);

        Assert.True(Directory.Exists(dir));
        Assert.Equal("inner", File.ReadAllText(inner));
        Assert.Equal("inner leftover", File.ReadAllText(innerLeftover));
    }

    [Theory]
    [InlineData(".claim-")]
    [InlineData(".tmp-")]
    public void A_link_of_a_leftover_name_stays_however_old_and_so_does_its_target(string prefix)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string outside = _rig.Outside();
        string targetFile = Path.Combine(outside, "target.txt");
        File.WriteAllText(targetFile, "target");
        SetAge(targetFile, TimeSpan.FromDays(60));
        string targetDir = Path.Combine(outside, "target-dir");
        _ = Directory.CreateDirectory(targetDir);
        string inside = Path.Combine(targetDir, prefix + "inside");
        File.WriteAllText(inside, "inside");
        SetAge(inside, TimeSpan.FromDays(60));
        SetAge(targetDir, TimeSpan.FromDays(60));
        string missing = Path.Combine(outside, "missing");
        _ = Directory.CreateDirectory(_rig.Folder);
        string toFile = Path.Combine(_rig.Folder, prefix + "to-file");
        string toDir = Path.Combine(_rig.Folder, prefix + "to-dir");
        string dangling = Path.Combine(_rig.Folder, prefix + "dangling");
        _ = File.CreateSymbolicLink(toFile, targetFile);
        _ = Directory.CreateSymbolicLink(toDir, targetDir);
        _ = File.CreateSymbolicLink(dangling, missing);

        _ = Scan(_rig.Folder, reap: true);

        Assert.Equal(targetFile, new FileInfo(toFile).LinkTarget);
        Assert.Equal(targetDir, new FileInfo(toDir).LinkTarget);
        Assert.Equal(missing, new FileInfo(dangling).LinkTarget);
        Assert.Equal("target", File.ReadAllText(targetFile));
        Assert.Equal("inside", File.ReadAllText(inside));
        Assert.False(Path.Exists(missing));
        Assert.Equal(Sorted(prefix + "dangling", prefix + "to-dir", prefix + "to-file"), EntryNames(_rig.Folder));
        Assert.Equal(Sorted("target-dir", "target.txt"), EntryNames(outside));
    }

    [Fact]
    public void Other_dot_files_and_names_that_only_contain_the_prefix_stay_however_old()
    {
        string[] names =
        [
            ".hidden", ".claim", ".tmp", ".claimx-1", ".tmpfoo", ".Claim-1", ".TMP-1", "claim-1", "tmp-1", "x.claim-1", "x.tmp-1",
            Stem + ".claim-1.json", " .claim-1", "x-.claim-1", "..claim-1",
        ];
        string[] paths = [.. names.Select(n => Make(n, TimeSpan.FromDays(30)))];

        _ = Scan(_rig.Folder, reap: true);

        Assert.All(paths, p => Assert.True(File.Exists(p), p));
        Assert.Equal(Sorted(names), EntryNames(_rig.Folder));
    }

    [Fact]
    public void A_pointer_is_never_removed_by_the_leftover_reap_and_the_pointer_reap_is_as_before()
    {
        StatusIndexRig.Added ended = _rig.Add("ended-long-ago", _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21));
        StatusIndexRig.Added live = _rig.Add("live-old", _mine, startedAgo: TimeSpan.FromDays(60));
        string torn = Make(_rig.NextName(StatusDocs.Now), TimeSpan.FromDays(60), "{ torn");
        string empty = Make(_rig.NextName(StatusDocs.Now), TimeSpan.FromDays(60), string.Empty);
        string oldClaim = Make(".claim-" + Name, TimeSpan.FromDays(60));
        string oldTemp = Make(".tmp-0123456789abcdef0123456789abcdef", TimeSpan.FromDays(60), "{}");
        SetAge(Path.Combine(_rig.Folder, live.Pointer), TimeSpan.FromDays(60));

        _ = Scan(_rig.Folder, reap: true);

        Assert.False(File.Exists(Path.Combine(_rig.Folder, ended.Pointer)));
        Assert.True(File.Exists(Path.Combine(_rig.Folder, live.Pointer)));
        Assert.True(File.Exists(torn));
        Assert.True(File.Exists(empty));
        Assert.False(File.Exists(oldClaim));
        Assert.False(File.Exists(oldTemp));
    }

    [Fact]
    public void Without_reap_nothing_is_deleted()
    {
        StatusIndexRig.Added ended = _rig.Add("ended-long-ago", _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21));
        _ = Make(".claim-" + Name, TimeSpan.FromDays(60));
        _ = Make(".tmp-0123456789abcdef0123456789abcdef", TimeSpan.FromDays(60), "{}");
        string[] before = StatusIndexRig.Snapshot(_rig.Folder);

        _ = Scan(_rig.Folder, reap: false);

        Assert.Equal(before, StatusIndexRig.Snapshot(_rig.Folder));
        Assert.True(File.Exists(Path.Combine(_rig.Folder, ended.Pointer)));
        Assert.Equal(3, before.Length);
    }

    [Fact]
    public void An_index_folder_that_is_itself_a_link_has_nothing_deleted_through_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string real = _rig.Outside();
        string claim = Path.Combine(real, ".claim-" + Name);
        string temp = Path.Combine(real, ".tmp-0123456789abcdef0123456789abcdef");
        File.WriteAllText(claim, "claim");
        File.WriteAllText(temp, "temp");
        SetAge(claim, TimeSpan.FromDays(60));
        SetAge(temp, TimeSpan.FromDays(60));
        string link = Path.Combine(_rig.Outside(), "runs");
        _ = Directory.CreateSymbolicLink(link, real);
        string[] before = StatusIndexRig.Snapshot(real);

        (List<StatusRow> rows, bool complete) = Scan(link, reap: true);

        Assert.True(complete);
        Assert.Empty(rows);
        Assert.Equal(before, StatusIndexRig.Snapshot(real));
        Assert.Equal(real, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void A_missing_index_folder_does_not_throw_and_is_not_made()
    {
        string missing = Path.Combine(_rig.State, "no-such", "runs");

        (List<StatusRow> rows, bool complete) = Scan(missing, reap: true);

        Assert.True(complete);
        Assert.Empty(rows);
        Assert.False(Path.Exists(missing));
        Assert.False(Path.Exists(_rig.Folder));
    }

    [Fact]
    public void A_leftover_in_a_folder_that_became_unwritable_stays_and_the_scan_does_not_throw()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.SkipWhen(Environment.IsPrivilegedProcess, "needs mode bits that bind the user");
        string leftover = Make(".claim-" + Name, TimeSpan.FromDays(60));
        string tempLeftover = Make(".tmp-0123456789abcdef0123456789abcdef", TimeSpan.FromDays(60), "{}");
        File.SetUnixFileMode(_rig.Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        (List<StatusRow> rows, bool complete) result;
        try
        {
            result = Scan(_rig.Folder, reap: true);
        }
        finally
        {
            File.SetUnixFileMode(_rig.Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(result.complete);
        Assert.Empty(result.rows);
        Assert.True(File.Exists(leftover));
        Assert.True(File.Exists(tempLeftover));
    }

    [Fact]
    public void A_fresh_claim_survives_a_reap_and_a_Write_of_its_name_still_takes_the_next_suffix()
    {
        string fresh = Make(".claim-" + Name, TimeSpan.Zero, string.Empty);
        string minutes = Make(".claim-" + SuffixName(3), TimeSpan.FromMinutes(1), string.Empty);
        string stale = Make(".claim-" + OtherName, TimeSpan.FromDays(30), string.Empty);

        _ = Scan(_rig.Folder, reap: true);
        _ = Scan(_rig.Folder, reap: true);

        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(minutes));
        Assert.False(File.Exists(stale));
        string written = StatusIndex.Write(_rig.Folder, Name, Pointer("/runs/after-the-reap"));
        Assert.Equal(SuffixName(2), written);
        Assert.False(File.Exists(Path.Combine(_rig.Folder, Name)));
        Assert.Equal(Sorted(".claim-" + Name, ".claim-" + SuffixName(3), written), EntryNames(_rig.Folder));
        string freed = StatusIndex.Write(_rig.Folder, OtherName, Pointer("/runs/stale-claim-gone"));
        Assert.Equal(OtherName, freed);
    }

    [Fact]
    public async Task Only_a_scan_that_reaps_deletes_leftovers_all_and_not_run_or_hereAsync()
    {
        StatusIndexRig.Added live = _rig.Add("live", _mine);
        string old = Make(".claim-" + Name, TimeSpan.FromDays(60));
        string oldTemp = Make(".tmp-0123456789abcdef0123456789abcdef", TimeSpan.FromDays(60), "{}");
        string fresh = Make(".claim-" + OtherName, TimeSpan.Zero);
        Func<string, string?> caller = StatusIndexRig.Session("sess-A");

        (int runCode, _) = await _rig.CallAsync(caller, "--run", live.Dir);
        (int hereCode, _) = await _rig.CallAsync(caller, "--here", "--runs-root", _rig.Root, "--since", "90d");

        Assert.Equal([0, 0], [runCode, hereCode]);
        Assert.True(File.Exists(old));
        Assert.True(File.Exists(oldTemp));

        (int allCode, _) = await _rig.AllAsync();

        Assert.Equal(0, allCode);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(_rig.Folder, live.Pointer)));
    }
}
