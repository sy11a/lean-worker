using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Reaping (docs/research/live-status/README.md, section 4.1): a status call that reads the index deletes a pointer,
/// one file at a time and only on proof: the run is final and ended more than 7 days ago, or it is lost or gone and
/// the pointer is more than 7 days old. Never an orphaned, unknown, unreadable, ending or shared-dir run; only
/// regular files directly in the index folder with a pointer name; --run and --here never reap; run dirs are never
/// touched.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusIndexReapTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];
    private static readonly TimeSpan _week = TimeSpan.FromDays(7);
    private static readonly TimeSpan _second = TimeSpan.FromSeconds(1);

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private bool Kept(StatusIndexRig.Added run) => File.Exists(Path.Combine(_rig.Folder, run.Pointer));

    private static Func<string, string?> Caller() => StatusIndexRig.Session("sess-A");

    // A lost run: its launcher is gone and so is its worker.
    private StatusIndexRig.Added Lost(string name, TimeSpan startedAgo)
    {
        return _rig.Add(name, _mine, startedAgo: startedAgo, tweak: d =>
        {
            d["launcher"]!["pid"] = 900;
            d["worker"] = StatusDocs.Worker(pid: 301, pgid: 301, sid: 301);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_final_run_that_ended_more_than_7_days_ago_loses_its_pointer_and_a_younger_one_keepsAsync(bool allScope)
    {
        StatusIndexRig.Added old = _rig.Add("old-success", _mine, endedAgo: _week + _second, startedAgo: _week + TimeSpan.FromHours(1));
        StatusIndexRig.Added older = _rig.Add("old-failed", _mine, endedAgo: TimeSpan.FromDays(30), startedAgo: TimeSpan.FromDays(31), status: "error");
        StatusIndexRig.Added wrapped = _rig.Add("old-wrapped", _mine, endedAgo: TimeSpan.FromDays(9), startedAgo: TimeSpan.FromDays(10), status: "wrapped-up");
        StatusIndexRig.Added exactly = _rig.Add("exactly-7d", _mine, endedAgo: _week, startedAgo: _week + TimeSpan.FromHours(1));
        StatusIndexRig.Added young = _rig.Add("young", _mine, endedAgo: _week - _second, startedAgo: _week + TimeSpan.FromHours(1));

        (int code, _) = allScope ? await _rig.AllAsync() : await _rig.CallAsync(Caller());

        Assert.Equal(0, code);
        Assert.False(Kept(old));
        Assert.False(Kept(older));
        Assert.False(Kept(wrapped));
        Assert.True(Kept(exactly));
        Assert.True(Kept(young));
    }

    [Fact]
    public async Task A_final_state_from_the_summary_of_a_run_whose_launcher_died_is_reaped_after_7_days_tooAsync()
    {
        StatusIndexRig.Added run = _rig.Add("died-after-summary", _mine, startedAgo: TimeSpan.FromDays(9), tweak: d =>
        {
            d["launcher"]!["pid"] = 900;
            d["updated_at"] = StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromDays(8));
        });
        await File.WriteAllTextAsync(Path.Combine(run.Dir, "summary.json"), new JsonObject { ["status"] = "success" }.ToJsonString(), TestContext.Current.CancellationToken);

        _ = await _rig.AllAsync();

        Assert.False(Kept(run));
    }

    [Fact]
    public async Task A_lost_run_loses_its_pointer_when_the_pointer_is_more_than_7_days_oldAsync()
    {
        StatusIndexRig.Added old = Lost("lost-old", _week + _second);
        StatusIndexRig.Added young = Lost("lost-young", _week - _second);

        _ = await _rig.AllAsync();

        Assert.False(Kept(old));
        Assert.True(Kept(young));
    }

    [Fact]
    public async Task A_lost_run_whose_pointer_has_no_usable_age_is_keptAsync()
    {
        StatusIndexRig.Added run = Lost("lost-no-age", TimeSpan.FromDays(40));
        JsonObject pointer = run.PointerDoc.DeepClone().AsObject();
        pointer["created_at"] = "not a time";
        _ = _rig.WritePointer(run.Pointer, pointer.ToJsonString());

        _ = await _rig.AllAsync();

        Assert.True(Kept(run));
    }

    [Fact]
    public async Task A_gone_run_loses_its_pointer_when_the_pointer_is_more_than_7_days_oldAsync()
    {
        StatusIndexRig.Added old = _rig.Add("gone-old", _mine, startedAgo: _week + _second, tweak: d => d["launcher"]!["pid"] = 900);
        StatusIndexRig.Added young = _rig.Add("gone-young", _mine, startedAgo: _week - _second, tweak: d => d["launcher"]!["pid"] = 900);
        Directory.Delete(old.Dir, recursive: true);
        Directory.Delete(young.Dir, recursive: true);

        _ = await _rig.AllAsync();

        Assert.False(Kept(old));
        Assert.True(Kept(young));
    }

    [Fact]
    public async Task An_orphaned_unknown_unreadable_ending_and_shared_dir_run_keeps_its_pointer_however_oldAsync()
    {
        TimeSpan age = TimeSpan.FromDays(60);
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        StatusIndexRig.Added orphaned = _rig.Add("orphaned-old", _mine, startedAgo: age, tweak: d => d["launcher"]!["pid"] = 900);
        StatusIndexRig.Added unknown = _rig.Add("unknown-old", _mine, startedAgo: age, tweak: d => d["launcher"]!["boot_id"] = null);
        StatusIndexRig.Added torn = _rig.Add("unreadable-old", _mine, startedAgo: age);
        await File.WriteAllTextAsync(Path.Combine(torn.Dir, "status.json"), "{ torn", TestContext.Current.CancellationToken);
        StatusIndexRig.Added ending = _rig.Add("ending-old", _mine, startedAgo: age, tweak: d =>
        {
            d["phase"] = "done";
            d["end"] = StatusDocs.End("success", StatusDocs.Now - age);
        });
        StatusIndexRig.Added live = _rig.Add("live-old", _mine, startedAgo: age);
        StatusIndexRig.Added shared = _rig.Add("shared-old", _mine, endedAgo: age, startedAgo: age);
        string twin = _rig.WritePointer(_rig.NextName(StatusDocs.Now - age), shared.PointerDoc.ToJsonString());
        string badPointer = _rig.WritePointer(_rig.NextName(StatusDocs.Now - age), "{ torn");

        _ = await _rig.AllAsync();
        _ = await _rig.CallAsync(Caller());
        _ = await _rig.CallAsync("--owner", _mine[0]);

        Assert.All([orphaned, unknown, torn, ending, live, shared], r => Assert.True(Kept(r), r.Id));
        Assert.True(File.Exists(twin));
        Assert.True(File.Exists(badPointer));
    }

    [Fact]
    public async Task Only_a_regular_file_with_a_pointer_name_directly_in_the_index_folder_is_ever_deletedAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        StatusIndexRig.Added reapable = _rig.Add("reapable", _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21));
        JsonObject oldPointer = reapable.PointerDoc.DeepClone().AsObject();
        string oldText = oldPointer.ToJsonString();

        string outsideTarget = Path.Combine(_rig.Outside(), "old-valid-pointer.json");
        await File.WriteAllTextAsync(outsideTarget, oldText, TestContext.Current.CancellationToken);
        string linkName = _rig.NextName(StatusDocs.Now - TimeSpan.FromDays(20));
        _ = File.CreateSymbolicLink(Path.Combine(_rig.Folder, linkName), outsideTarget);
        string dirName = _rig.NextName(StatusDocs.Now - TimeSpan.FromDays(20));
        string dirPath = Path.Combine(_rig.Folder, dirName);
        _ = Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(Path.Combine(dirPath, "inner.json"), oldText, TestContext.Current.CancellationToken);
        string otherName = Path.Combine(_rig.Folder, "old-valid-pointer-wrong-name.json");
        await File.WriteAllTextAsync(otherName, oldText, TestContext.Current.CancellationToken);
        string stateSibling = Path.Combine(_rig.State, "old-valid-pointer.json");
        await File.WriteAllTextAsync(stateSibling, oldText, TestContext.Current.CancellationToken);
        string[] runDirsBefore = StatusIndexRig.Snapshot(_rig.Root);

        _ = await _rig.AllAsync();
        _ = await _rig.CallAsync(Caller());

        Assert.False(Kept(reapable));
        Assert.True(File.Exists(outsideTarget));
        Assert.True(new FileInfo(Path.Combine(_rig.Folder, linkName)).LinkTarget is not null);
        Assert.True(File.Exists(Path.Combine(dirPath, "inner.json")));
        Assert.True(File.Exists(otherName));
        Assert.True(File.Exists(stateSibling));
        Assert.Equal(runDirsBefore, StatusIndexRig.Snapshot(_rig.Root));
    }

    [Fact]
    public async Task Run_dirs_are_never_touched_by_a_scan_that_reapsAsync()
    {
        StatusIndexRig.Added ended = _rig.Add("ended-long-ago", _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21));
        StatusIndexRig.Added lost = Lost("lost-long-ago", TimeSpan.FromDays(20));
        StatusIndexRig.Added gone = _rig.Add("gone-long-ago", _mine, startedAgo: TimeSpan.FromDays(20), tweak: d => d["launcher"]!["pid"] = 900);
        Directory.Delete(gone.Dir, recursive: true);
        string[] before = StatusIndexRig.Snapshot(_rig.Root, _rig.Proc.Root);

        _ = await _rig.AllAsync("--json");

        Assert.False(Kept(ended));
        Assert.False(Kept(lost));
        Assert.False(Kept(gone));
        Assert.Equal(before, StatusIndexRig.Snapshot(_rig.Root, _rig.Proc.Root));
        Assert.True(Directory.Exists(ended.Dir));
        Assert.True(Directory.Exists(lost.Dir));
    }

    [Fact]
    public async Task A_reaped_pointer_is_gone_from_the_next_listing_and_a_second_call_is_fineAsync()
    {
        StatusIndexRig.Added old = _rig.Add("long-ago", _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21));
        _ = _rig.Add("recent", _mine, endedAgo: TimeSpan.FromHours(1), startedAgo: TimeSpan.FromHours(2));

        _ = await _rig.AllAsync();
        (int code, string json) = await _rig.AllAsync("--since", "90d", "--json");

        Assert.Equal(0, code);
        Assert.Equal(["recent"], StatusIndexRig.JsonNames(json));
        Assert.False(Kept(old));
    }

    [Fact]
    public async Task Run_and_here_never_reapAsync()
    {
        StatusIndexRig.Added old = _rig.Add("long-ago", _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21));
        StatusIndexRig.Added lost = Lost("lost-long-ago", TimeSpan.FromDays(20));

        (int runCode, _) = await _rig.CallAsync(Caller(), "--run", old.Dir);
        (int hereCode, _) = await _rig.CallAsync(Caller(), "--here", "--runs-root", _rig.Root, "--since", "90d");
        (int whoCode, _) = await _rig.CallAsync(Caller(), "--whoami");

        Assert.Equal([0, 0, 0], [runCode, hereCode, whoCode]);
        Assert.True(Kept(old));
        Assert.True(Kept(lost));
    }

    [Fact]
    public async Task Many_readers_reaping_at_once_all_succeed_and_the_pointer_goes_onceAsync()
    {
        StatusIndexRig.Added[] old =
        [
            .. Enumerable.Range(0, 6).Select(i => _rig.Add("long-ago-" + i.ToString(CultureInfo.InvariantCulture), _mine, endedAgo: TimeSpan.FromDays(20), startedAgo: TimeSpan.FromDays(21))),
        ];

        (int Code, string Output)[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await _rig.AllAsync()));

        Assert.All(results, r => Assert.Equal(0, r.Code));
        Assert.All(old, r => Assert.False(Kept(r)));
        Assert.Empty(_rig.PointerNames());
    }

    [Fact]
    public void Reap_deletes_only_a_regular_non_empty_file_of_the_pointer_form_directly_in_the_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string name = _rig.NextName(StatusDocs.Now);
        _ = _rig.WritePointer(name, "{}");
        string empty = _rig.NextName(StatusDocs.Now);
        _ = _rig.WritePointer(empty, string.Empty);
        string dir = _rig.NextName(StatusDocs.Now);
        _ = Directory.CreateDirectory(Path.Combine(_rig.Folder, dir));
        string target = Path.Combine(_rig.Outside(), "target.json");
        File.WriteAllText(target, "{}");
        string link = _rig.NextName(StatusDocs.Now);
        _ = File.CreateSymbolicLink(Path.Combine(_rig.Folder, link), target);
        string outside = Path.Combine(_rig.State, "escape.json");
        File.WriteAllText(outside, "{}");
        string notes = Path.Combine(_rig.Folder, "notes.json");
        File.WriteAllText(notes, "{}");

        foreach (string refused in new[] { empty, dir, link, "notes.json", "../escape.json", "sub/" + name, string.Empty, ".tmp-1234" })
        {
            StatusIndex.Reap(_rig.Folder, refused, ReadBudget.Default());
        }

        Assert.True(File.Exists(Path.Combine(_rig.Folder, empty)));
        Assert.True(Directory.Exists(Path.Combine(_rig.Folder, dir)));
        Assert.True(new FileInfo(Path.Combine(_rig.Folder, link)).LinkTarget is not null);
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(Path.Combine(_rig.Folder, name)));

        StatusIndex.Reap(_rig.Folder, name, ReadBudget.Default());
        StatusIndex.Reap(_rig.Folder, name, ReadBudget.Default());

        Assert.False(File.Exists(Path.Combine(_rig.Folder, name)));
    }

    [Fact]
    public void Reap_of_a_file_that_is_already_gone_or_in_a_folder_that_is_gone_is_fine()
    {
        StatusIndex.Reap(_rig.Folder, "20261122-115000-1-r1.json", ReadBudget.Default());
        _ = _rig.WritePointer(_rig.NextName(StatusDocs.Now), "{}");
        StatusIndex.Reap(_rig.Folder, "20261122-115000-2-r1.json", ReadBudget.Default());

        _ = Assert.Single(_rig.PointerNames());
    }
}
