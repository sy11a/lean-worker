using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Fix round 1 of issue #146, points 4 and 7 (docs/research/live-status/README.md, section 4.1): a pointed run is gone
/// only on proof. The pointer's launcher is probed first: while it is alive a run dir or status file that cannot be
/// reached leaves the row live and unknown (an unreadable-status note, never ended); with the launcher not alive, a
/// path that provably is not there (ENOENT, ENOTDIR) is gone and its pointer is reaped after 7 days, any other error
/// (a permission) is unknown and never reaped. The reaper checks the index folder for being a link again right before
/// each delete.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class StatusGoneProofTests : IDisposable
{
    private const UnixFileMode OwnerAccess = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly string[] _mine = ["claude-session:sess-A"];
    private static readonly TimeSpan _week = TimeSpan.FromDays(7);
    private static readonly TimeSpan _second = TimeSpan.FromSeconds(1);

    private readonly StatusIndexRig _rig = new();
    private readonly List<string> _locked = [];

    public void Dispose()
    {
        foreach (string path in _locked.Where(p => Directory.Exists(p)))
        {
            File.SetUnixFileMode(path, OwnerAccess);
        }

        _rig.Dispose();
    }

    private static Func<string, string?> Caller() => StatusIndexRig.Session("sess-A");

    private bool Kept(StatusIndexRig.Added run) => File.Exists(Path.Combine(_rig.Folder, run.Pointer));

    private StatusIndexRig.Added Live(string name, TimeSpan startedAgo) => _rig.Add(name, _mine, startedAgo: startedAgo);

    private StatusIndexRig.Added Dead(string name, TimeSpan startedAgo) => _rig.Add(name, _mine, startedAgo: startedAgo, tweak: d => d["launcher"]!["pid"] = 900);

    // Takes away what the reader would reach, in the named way.
    private static void Vanish(string what, params StatusIndexRig.Added[] runs)
    {
        if (what is "under-a-file")
        {
            // The runs folder becomes a file: every run dir path then runs through something that is no directory.
            string folder = Path.GetDirectoryName(runs[0].Dir)!;
            Directory.Delete(folder, recursive: true);
            File.WriteAllText(folder, "not a directory");
            return;
        }

        foreach (StatusIndexRig.Added run in runs)
        {
            if (what is "run-dir")
            {
                Directory.Delete(run.Dir, recursive: true);
            }
            else
            {
                File.Delete(Path.Combine(run.Dir, "status.json"));
            }
        }
    }

    // Takes the access away from a directory (chmod 000), given back by Dispose.
    private void Lock(string dir)
    {
        _locked.Add(dir);
        File.SetUnixFileMode(dir, UnixFileMode.None);
    }

    private void Deny(string what, StatusIndexRig.Added run) => Lock(what is "parent" ? Path.GetDirectoryName(run.Dir)! : run.Dir);

    private async Task<JsonObject> RowAsync()
    {
        (int code, string json) = await _rig.CallAsync(Caller(), "--json");
        Assert.Equal(0, code);
        return Assert.Single(StatusIndexRig.JsonRows(json));
    }

    private async Task<string> HeaderAsync()
    {
        (_, string plain) = await _rig.CallAsync(Caller());
        return StatusIndexRig.Lines(plain)[0];
    }

    private async Task ScanEverywhereAsync()
    {
        _ = await _rig.AllAsync();
        _ = await _rig.CallAsync(Caller());
        _ = await _rig.CallAsync("--owner", _mine[0]);
    }

    private static void SkipUnlessPermissionsApply() =>
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "chmod 000 does not stop a privileged user or Windows");

    [Theory]
    [InlineData("run-dir")]
    [InlineData("status-file")]
    public async Task A_live_launcher_and_a_missing_run_dir_or_status_file_is_a_live_unknown_row_that_needs_attention_and_is_never_reapedAsync(string what)
    {
        StatusIndexRig.Added run = Live("vanished", TimeSpan.FromDays(60));
        Vanish(what, run);

        JsonObject row = await RowAsync();
        string header = await HeaderAsync();
        await ScanEverywhereAsync();

        Assert.Equal("unknown", row["state"]!.GetValue<string>());
        Assert.Equal("alive", row["liveness"]!.GetValue<string>());
        Assert.Contains("unreadable status", row["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("lean-worker status: owner cc:sess-A, 1 live, 0 ended, 1 needs attention", header);
        Assert.True(Kept(run));
    }

    [Theory]
    [InlineData("run-dir")]
    [InlineData("parent")]
    public async Task A_live_launcher_and_an_unreadable_run_dir_is_a_live_unknown_row_that_needs_attention_and_is_never_reapedAsync(string what)
    {
        SkipUnlessPermissionsApply();
        StatusIndexRig.Added run = Live("denied", TimeSpan.FromDays(60));
        Deny(what, run);

        JsonObject row = await RowAsync();
        string header = await HeaderAsync();
        await ScanEverywhereAsync();

        Assert.Equal("unknown", row["state"]!.GetValue<string>());
        Assert.Equal("alive", row["liveness"]!.GetValue<string>());
        Assert.Contains("unreadable status", row["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("lean-worker status: owner cc:sess-A, 1 live, 0 ended, 1 needs attention", header);
        Assert.True(Kept(run));
    }

    [Theory]
    [InlineData("run-dir")]
    [InlineData("parent")]
    public async Task A_dead_launcher_and_a_permission_error_is_unknown_not_gone_and_is_never_reapedAsync(string what)
    {
        SkipUnlessPermissionsApply();
        StatusIndexRig.Added run = Dead("denied", TimeSpan.FromDays(60));
        Deny(what, run);

        JsonObject row = await RowAsync();
        string header = await HeaderAsync();
        await ScanEverywhereAsync();

        Assert.Equal("unknown", row["state"]!.GetValue<string>());
        Assert.True(row["liveness"]!.GetValue<string>() is not "gone", "the run is called gone on a permission error");
        Assert.Equal("lean-worker status: owner cc:sess-A, 0 live, 0 ended, 1 needs attention", header);
        Assert.True(Kept(run));
    }

    [Theory]
    [InlineData("run-dir")]
    [InlineData("status-file")]
    [InlineData("under-a-file")]
    public async Task A_dead_launcher_and_a_path_that_provably_is_not_there_is_a_gone_row_that_needs_no_attentionAsync(string what)
    {
        StatusIndexRig.Added run = Dead("removed", TimeSpan.FromMinutes(30));
        Vanish(what, run);

        JsonObject row = await RowAsync();
        string header = await HeaderAsync();

        Assert.Equal("gone", row["state"]!.GetValue<string>());
        Assert.Equal("gone", row["liveness"]!.GetValue<string>());
        Assert.Equal("lean-worker status: owner cc:sess-A, 0 live, 1 ended, 0 need attention", header);
        Assert.True(Kept(run));
    }

    [Theory]
    [InlineData("run-dir")]
    [InlineData("status-file")]
    [InlineData("under-a-file")]
    public async Task A_gone_pointer_is_reaped_when_it_is_more_than_7_days_old_and_not_beforeAsync(string what)
    {
        StatusIndexRig.Added old = Dead("gone-old", _week + _second);
        StatusIndexRig.Added young = Dead("gone-young", _week - _second);
        Vanish(what, old, young);

        await ScanEverywhereAsync();

        Assert.False(Kept(old));
        Assert.True(Kept(young));
    }

    [Fact]
    public void The_reaper_deletes_nothing_in_an_index_folder_that_is_a_link_to_a_folder_with_a_pointer_named_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string target = _rig.Outside();
        string name = _rig.NextName(StatusDocs.Now - TimeSpan.FromDays(20));
        string inTarget = Path.Combine(target, name);
        File.WriteAllText(inTarget, "{\"schema\":\"lean-worker.pointer/1\"}");
        string link = Path.Combine(_rig.State, "runs-link");
        _ = Directory.CreateSymbolicLink(link, target);

        StatusIndex.Reap(link, name, ReadBudget.Default());

        Assert.True(File.Exists(inTarget));
        StatusIndex.Reap(target, name, ReadBudget.Default());
        Assert.False(File.Exists(inTarget));
    }
}
