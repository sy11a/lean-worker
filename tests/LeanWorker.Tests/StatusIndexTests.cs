using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The per-user index (docs/research/live-status/README.md, section 4.1): where the folder is, how a pointer file is
/// named and written (modes, once, never over another, the -2 to -99 suffix) and which names the reader and the
/// reaper take for pointers.
/// </summary>
public sealed class StatusIndexTests : IDisposable
{
    private static readonly DateTimeOffset _started = new(2026, 11, 22, 13, 58, 50, TimeSpan.Zero);

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private static JsonObject Pointer(string runDir = "/runs/x") =>
        StatusIndex.Pointer(_started, runDir, "chain-1", parentRun: null, ["claude-session:s1", "pid:1@2"], new JsonObject { ["pid"] = 41233, ["start_ticks"] = 7432110, ["boot_id"] = "b" });

    private string Folder() => Path.Combine(_dirs.Create("lw-index"), "runs");

    [Fact]
    public void The_file_name_is_the_start_stamp_the_launcher_pid_and_the_round()
    {
        Assert.Equal("20261122-135850-41233-r1.json", StatusIndex.FileName(_started, 41233, 1));
        Assert.Equal("20261122-135850-7-r12.json", StatusIndex.FileName(_started, 7, 12));
    }

    [Theory]
    [InlineData("20261122-135850-41233-r1.json", true)]
    [InlineData("20261122-135850-41233-r1-2.json", true)]
    [InlineData("20261122-135850-41233-r1-99.json", true)]
    [InlineData("20261122-135850-41233-r123456.json", true)]
    [InlineData("20261122-135850-41233-r1-100.json", false)]
    [InlineData("20261122-135850-41233-r1-2-3.json", false)]
    [InlineData("20261122-135850-41233-r.json", false)]
    [InlineData("20261122-135850-41233-r1234567.json", false)]
    [InlineData("20261122-135850-41233.json", false)]
    [InlineData("20261122-13585-41233-r1.json", false)]
    [InlineData("2026112-135850-41233-r1.json", false)]
    [InlineData("20261122-135850-41233-r1.jsonl", false)]
    [InlineData("20261122-135850-41233-r1.json.tmp", false)]
    [InlineData("20261122-135850--r1.json", false)]
    [InlineData("20261122-135850-12345678901-r1.json", false)]
    [InlineData(".tmp-0123456789abcdef", false)]
    [InlineData("notes.json", false)]
    [InlineData("", false)]
    public void Only_a_name_of_the_pointer_form_is_a_pointer_name(string name, bool expected) =>
        Assert.Equal(expected, StatusIndex.IsPointerName(name));

    [Fact]
    public void The_pointer_has_the_schema_and_the_fields_of_the_design()
    {
        JsonObject p = Pointer("/runs/20261122-135850-fix");

        Assert.Equal(["schema", "created_at", "run_dir", "chain_id", "parent_run", "owner_keys", "launcher"], [.. p.Select(kv => kv.Key)]);
        Assert.Equal("lean-worker.pointer/1", p["schema"]!.GetValue<string>());
        Assert.Equal("2026-11-22T13:58:50.000Z", p["created_at"]!.GetValue<string>());
        Assert.Equal("/runs/20261122-135850-fix", p["run_dir"]!.GetValue<string>());
        Assert.Equal("chain-1", p["chain_id"]!.GetValue<string>());
        Assert.Null(p["parent_run"]);
        Assert.Equal(["claude-session:s1", "pid:1@2"], [.. p["owner_keys"]!.AsArray().Select(n => n!.GetValue<string>())]);
        Assert.Equal(41233, p["launcher"]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public void LEAN_WORKER_STATE_DIR_wins_then_XDG_STATE_HOME_then_the_home_default()
    {
        Assert.Equal("/s/runs", StatusIndex.Folder(StatusIndexRig.Env(("LEAN_WORKER_STATE_DIR", "/s"), ("XDG_STATE_HOME", "/x"), ("HOME", "/h"))));
        Assert.Equal("/x/lean-worker/runs", StatusIndex.Folder(StatusIndexRig.Env(("XDG_STATE_HOME", "/x"), ("HOME", "/h"))));
        Assert.Equal("/h/.local/state/lean-worker/runs", StatusIndex.Folder(StatusIndexRig.Env(("HOME", "/h"))));
        Assert.Null(StatusIndex.Folder(StatusIndexRig.Env()));
    }

    [Fact]
    public void A_variable_that_is_not_an_absolute_path_is_passed_over()
    {
        Assert.Equal("/x/lean-worker/runs", StatusIndex.Folder(StatusIndexRig.Env(("LEAN_WORKER_STATE_DIR", "relative/dir"), ("XDG_STATE_HOME", "/x"))));
        Assert.Equal("/h/.local/state/lean-worker/runs", StatusIndex.Folder(StatusIndexRig.Env(("LEAN_WORKER_STATE_DIR", string.Empty), ("XDG_STATE_HOME", "state"), ("HOME", "/h"))));
        Assert.Null(StatusIndex.Folder(StatusIndexRig.Env(("HOME", "home"))));
    }

    [Fact]
    public void A_write_makes_the_folder_0700_and_the_file_0600_and_leaves_no_temp_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Folder();

        string name = StatusIndex.Write(folder, "20261122-135850-1-r1.json", Pointer());

        Assert.Equal("20261122-135850-1-r1.json", name);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, name)));
        Assert.Equal([name], [.. Directory.GetFileSystemEntries(folder).Select(f => Path.GetFileName(f))]);
        Assert.Equal("lean-worker.pointer/1", JsonNode.Parse(File.ReadAllText(Path.Combine(folder, name)))!["schema"]!.GetValue<string>());
    }

    [Fact]
    public void A_write_never_replaces_a_pointer_and_takes_the_next_suffix_from_2_to_99()
    {
        string folder = Folder();
        string first = StatusIndex.Write(folder, "20261122-135850-1-r1.json", Pointer("/runs/first"));

        string second = StatusIndex.Write(folder, "20261122-135850-1-r1.json", Pointer("/runs/second"));
        string third = StatusIndex.Write(folder, "20261122-135850-1-r1.json", Pointer("/runs/third"));

        Assert.Equal(["20261122-135850-1-r1.json", "20261122-135850-1-r1-2.json", "20261122-135850-1-r1-3.json"], [first, second, third]);
        Assert.Contains("/runs/first", File.ReadAllText(Path.Combine(folder, first)), StringComparison.Ordinal);
        Assert.Contains("/runs/second", File.ReadAllText(Path.Combine(folder, second)), StringComparison.Ordinal);
        Assert.All([first, second, third], n => Assert.True(StatusIndex.IsPointerName(n)));
    }

    [Fact]
    public void The_ninety_ninth_suffix_is_the_last_one_tried()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        const string stem = "20261122-135850-1-r1";
        File.WriteAllText(Path.Combine(folder, stem + ".json"), "first");
        for (int k = 2; k <= 98; k++)
        {
            File.WriteAllText(Path.Combine(folder, stem + "-" + k.ToString(CultureInfo.InvariantCulture) + ".json"), "taken");
        }

        string last = StatusIndex.Write(folder, stem + ".json", Pointer());
        _ = Assert.Throws<IOException>(() => StatusIndex.Write(folder, stem + ".json", Pointer()));

        Assert.Equal(stem + "-99.json", last);
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, stem + ".json")));
        Assert.Equal(99, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_is_an_io_error_not_a_crash_of_another_kind()
    {
        string blocker = Path.Combine(_dirs.Create("lw-index"), "runs");
        File.WriteAllText(blocker, "a file where the folder should be");

        _ = Assert.ThrowsAny<IOException>(() => StatusIndex.Write(blocker, "20261122-135850-1-r1.json", Pointer()));
        Assert.Equal("a file where the folder should be", File.ReadAllText(blocker));
    }

    [Fact]
    public void Writes_of_the_same_name_from_many_threads_each_get_a_file_of_their_own()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);

        string[] names =
        [
            .. Enumerable.Range(0, 12).AsParallel().WithDegreeOfParallelism(12)
                .Select(i => StatusIndex.Write(folder, "20261122-135850-1-r1.json", Pointer("/runs/" + i.ToString(CultureInfo.InvariantCulture)))),
        ];

        Assert.Equal(12, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, Directory.GetFiles(folder).Length);
        Assert.Equal(12, Directory.GetFiles(folder).Select(f => JsonNode.Parse(File.ReadAllText(f))!["run_dir"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_listing_has_only_names_of_the_pointer_form_never_a_directory_or_a_path()
    {
        string folder = Folder();
        _ = StatusIndex.Write(folder, "20261122-135850-1-r1.json", Pointer());
        File.WriteAllText(Path.Combine(folder, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(folder, ".tmp-0123"), "{}");
        _ = Directory.CreateDirectory(Path.Combine(folder, "20261122-135850-2-r1.json"));
        _ = Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "20261122-135850-3-r1.json"), "{}");

        (List<string> names, bool complete) = StatusIndex.List(folder, ReadBudget.Default());

        Assert.True(complete);
        Assert.Equal(["20261122-135850-1-r1.json"], names);
    }

    [Fact]
    public void A_missing_folder_or_a_folder_that_is_a_link_lists_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string real = Folder();
        _ = StatusIndex.Write(real, "20261122-135850-1-r1.json", Pointer());
        string link = Path.Combine(_dirs.Create("lw-link"), "runs");
        _ = Directory.CreateSymbolicLink(link, real);

        Assert.Empty(StatusIndex.List(Path.Combine(_dirs.Create("lw-none"), "runs"), ReadBudget.Default()).Names);
        Assert.Empty(StatusIndex.List(link, ReadBudget.Default()).Names);
    }
}
