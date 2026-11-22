using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The reader over pointers (docs/research/live-status/README.md, section 4.1, "What a reader does, per pointer"): the
/// launcher identity of the ended rule comes from the pointer, a missing run dir is gone, two pointers on one run dir
/// are shared-dir, an unreadable pointer is a row in every index scope, and what a pointer names is displayed
/// data only.
/// </summary>
public sealed class StatusIndexReaderTests : IDisposable
{
    private static readonly string[] _mine = ["claude-session:sess-A"];

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static Func<string, string?> Caller() => StatusIndexRig.Session("sess-A");

    private async Task<JsonObject> OneRowAsync(params string[] args)
    {
        (int code, string json) = await _rig.CallAsync(Caller(), [.. args, "--json"]);
        Assert.Equal(0, code);
        return Assert.Single(StatusIndexRig.JsonRows(json));
    }

    private void Repoint(StatusIndexRig.Added run, Action<JsonObject> change)
    {
        JsonObject pointer = run.PointerDoc.DeepClone().AsObject();
        change(pointer);
        _ = _rig.WritePointer(run.Pointer, pointer.ToJsonString());
    }

    [Fact]
    public async Task The_launcher_identity_comes_from_the_pointer_a_status_file_that_claims_a_live_launcher_does_not_change_livenessAsync()
    {
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        StatusIndexRig.Added run = _rig.Add("claims-alive", _mine);
        Repoint(run, p => p["launcher"]!["pid"] = 900);

        JsonObject row = await OneRowAsync();

        Assert.Equal("orphaned", row["state"]!.GetValue<string>());
        Assert.Equal("gone", row["liveness"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_status_file_that_claims_a_gone_launcher_does_not_end_a_run_whose_pointer_says_it_is_aliveAsync()
    {
        StatusIndexRig.Added run = _rig.Add("claims-gone", _mine, tweak: d => d["launcher"]!["pid"] = 900);
        Repoint(run, p => p["launcher"]!["pid"] = StatusDocs.LauncherPid);

        JsonObject row = await OneRowAsync();

        Assert.Equal("running", row["state"]!.GetValue<string>());
        Assert.Equal("alive", row["liveness"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pointer_with_other_start_ticks_means_the_pid_was_reused_and_the_run_is_ended_not_runningAsync()
    {
        StatusIndexRig.Added run = _rig.Add("recycled", _mine, tweak: d => d["worker"] = StatusDocs.Worker(pid: 301, pgid: 301, sid: 301));
        Repoint(run, p => p["launcher"]!["start_ticks"] = 1);

        JsonObject row = await OneRowAsync();

        Assert.Equal("lost", row["state"]!.GetValue<string>());
        Assert.Equal("gone", row["liveness"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pointer_of_another_boot_is_lost_with_no_process_scan_even_when_the_worker_pid_existsAsync()
    {
        _rig.Proc.Add(StatusDocs.WorkerPid, StatusDocs.WorkerTicks);
        StatusIndexRig.Added run = _rig.Add("rebooted", _mine, tweak: d => d["worker"] = StatusDocs.Worker());
        Repoint(run, p => p["launcher"]!["boot_id"] = "boot-OLD");

        JsonObject row = await OneRowAsync();

        Assert.Equal("lost", row["state"]!.GetValue<string>());
        Assert.Equal("gone", row["liveness"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pointer_whose_launcher_is_not_an_object_with_a_pid_is_unknown_not_endedAsync()
    {
        StatusIndexRig.Added run = _rig.Add("no-pid", _mine);
        Repoint(run, p => p["launcher"] = new JsonObject { ["pid"] = "x", ["start_ticks"] = null, ["boot_id"] = FakeProcRoot.DefaultBoot });

        JsonObject row = await OneRowAsync();

        Assert.Equal("unknown", row["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_removed_run_dir_is_a_gone_row_that_is_ended_and_needs_no_attentionAsync()
    {
        StatusIndexRig.Added run = _rig.Add("removed", _mine);
        Directory.Delete(run.Dir, recursive: true);

        (_, string plain) = await _rig.CallAsync(Caller());
        JsonObject row = await OneRowAsync();

        Assert.Equal("gone", row["state"]!.GetValue<string>());
        Assert.Equal("gone", row["liveness"]!.GetValue<string>());
        Assert.Contains("run dir removed", row["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("lean-worker status: owner cc:sess-A, 0 live, 1 ended, 0 need attention", StatusIndexRig.Lines(plain)[0]);
        Assert.Matches(@"(?m)^gone\s+removed\s", plain);
        Assert.Equal(run.Pointer, row["pointer"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_dir_without_its_status_file_is_a_gone_row_when_it_was_reached_through_a_pointerAsync()
    {
        StatusIndexRig.Added run = _rig.Add("no-status", _mine);
        File.Delete(Path.Combine(run.Dir, "status.json"));

        JsonObject row = await OneRowAsync();

        Assert.Equal("gone", row["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_pointers_that_name_one_run_dir_make_both_rows_shared_dir_and_never_a_successAsync()
    {
        StatusIndexRig.Added first = _rig.Add("twice", _mine, endedAgo: TimeSpan.FromMinutes(5), status: "success");
        _ = _rig.WritePointer(_rig.NextName(StatusDocs.Now - TimeSpan.FromMinutes(5)), first.PointerDoc.ToJsonString());
        _ = _rig.Add("alone", _mine);

        (_, string json) = await _rig.CallAsync(Caller(), "--json");
        (_, string plain) = await _rig.CallAsync(Caller());

        List<JsonObject> rows = StatusIndexRig.JsonRows(json);
        Assert.Equal(3, rows.Count);
        JsonObject[] shared = [.. rows.Where(r => r["state"]!.GetValue<string>() is "shared-dir")];
        Assert.Equal(2, shared.Length);
        Assert.Equal(2, shared.Select(r => r["pointer"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(rows, r => r["state"]!.GetValue<string>() is "success");
        Assert.Equal("lean-worker status: owner cc:sess-A, 1 live, 0 ended, 2 need attention", StatusIndexRig.Lines(plain)[0]);
        Assert.Equal(2, StatusIndexRig.Rows(plain).Count(l => l.StartsWith("shared-dir", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_run_dir_shared_with_a_pointer_of_another_owner_is_shared_dir_for_this_owner_tooAsync()
    {
        StatusIndexRig.Added mine = _rig.Add("shared-run", _mine);
        _ = _rig.WritePointer(_rig.NextName(StatusDocs.Now), StatusIndexRig.Pointer(mine.Dir, mine.Id, parent: null, ["claude-session:sess-B"],
            mine.Status["launcher"]!.DeepClone().AsObject(), StatusDocs.Now).ToJsonString());

        JsonObject row = await OneRowAsync();

        Assert.Equal("shared-dir", row["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unreadable_pointer_is_an_unreadable_row_in_every_index_scope_and_is_never_deletedAsync()
    {
        _ = _rig.Add("healthy", _mine);
        string name = _rig.NextName(StatusDocs.Now - TimeSpan.FromDays(40));
        string path = _rig.WritePointer(name, "{ torn");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(40));

        string[][] scopes = [[], ["--owner", "no-such-key"], ["--all"]];
        foreach (string[] args in scopes)
        {
            (int code, string json) = await _rig.CallAsync(Caller(), [.. args, "--json"]);

            Assert.Equal(0, code);
            JsonObject row = StatusIndexRig.JsonRows(json).Single(r => r["state"]!.GetValue<string>() is "unreadable");
            Assert.Equal(name, row["pointer"]!.GetValue<string>());
            Assert.Equal("unknown", row["liveness"]!.GetValue<string>());
        }

        Assert.True(File.Exists(path));
        (_, string plain) = await _rig.CallAsync(Caller());
        Assert.Contains("1 needs attention", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pointer_to_a_directory_outside_any_runs_root_is_read_as_what_it_names_and_the_design_allows_itAsync()
    {
        // Section 5, "A forged status file or pointer": the files are trusted for display and waking only, so the
        // reader lists what a pointer names wherever it is; the only protection is that nothing is executed or
        // deleted, that links are not followed and that the content is cleaned.
        string outside = Path.Combine(_rig.Outside(), "20261122-115000-forged");
        _ = Directory.CreateDirectory(outside);
        JsonObject forged = StatusDocs.Make("forged-name", phase: "done", launcherPid: 900, end: StatusDocs.End("success"));
        await File.WriteAllTextAsync(Path.Combine(outside, "status.json"), forged.ToJsonString(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outside, "other.txt"), "untouched", TestContext.Current.CancellationToken);
        _ = _rig.WritePointer(_rig.NextName(StatusDocs.Now), StatusIndexRig.Pointer(outside, "x", parent: null, _mine, forged["launcher"]!.DeepClone().AsObject(), StatusDocs.Now).ToJsonString());
        string[] before = StatusIndexRig.Snapshot(outside);

        JsonObject row = await OneRowAsync();

        Assert.Equal("success", row["state"]!.GetValue<string>());
        Assert.Equal("forged-name", row["run"]!["name"]!.GetValue<string>());
        Assert.Equal(before, StatusIndexRig.Snapshot(outside));
    }

    [Fact]
    public async Task The_owner_key_and_parent_run_of_a_row_come_from_the_pointer_when_the_status_file_has_noneAsync()
    {
        string parentId = StatusIndexRig.Id(StatusDocs.Now - TimeSpan.FromMinutes(30), "up");
        _ = _rig.Add("up", _mine, startedAgo: TimeSpan.FromMinutes(30), id: parentId);
        _ = _rig.Add("pointer-owned", _mine, parent: parentId, tweak: d =>
        {
            d["owner"] = null;
            d["run"]!["parent_run"] = null;
        });

        (_, string plain) = await _rig.AllAsync();
        (_, string json) = await _rig.AllAsync("--json");

        Assert.Matches(@"pointer-owned\s+cc:sess-A\s", plain);
        Assert.Equal(["up", "pointer-owned"], StatusIndexRig.JsonNames(json));
        Assert.Equal(1, StatusIndexRig.JsonRows(json)[1]["depth"]!.GetValue<int>());
    }
}
