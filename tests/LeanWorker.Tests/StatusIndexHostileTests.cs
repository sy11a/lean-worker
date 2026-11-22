using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Pointers are as untrusted as status files (any process of the user can write the index folder): a pointer that is
/// a link, a FIFO, too large, duplicated keys, a lone surrogate, the wrong types, or names a run_dir that is relative,
/// a link, a file or carries an escape sequence or a newline makes its own row (or none) and nothing else: stdout and
/// stderr stay clean, one line per run, exit 0, the healthy run beside it is listed as usual.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusIndexHostileTests : IDisposable
{
    private const string Marker = "SECRET-MARKER";
    private const int OneMiB = 1024 * 1024;

    private static readonly string[] _mine = ["claude-session:sess-A"];

    private readonly StatusIndexRig _rig = new();

    public StatusIndexHostileTests() => _ = _rig.Add("healthy", _mine);

    public void Dispose() => _rig.Dispose();

    private static async Task PutAsync(string path, string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));
    }

    // The path of a new file in the index folder with a name of the pointer form.
    private string PointerPath() => Path.Combine(_rig.Folder, _rig.NextName(StatusDocs.Now - TimeSpan.FromMinutes(10)));

    private async Task<string> PointerAsync(string text)
    {
        _ = Directory.CreateDirectory(_rig.Folder);
        string path = PointerPath();
        await PutAsync(path, text);
        return path;
    }

    private static JsonObject GoodPointer(string runDir)
    {
        JsonObject launcher = new() { ["pid"] = 900, ["start_ticks"] = 1, ["boot_id"] = FakeProcRoot.DefaultBoot };
        return StatusIndexRig.Pointer(runDir, "c", parent: null, _mine, launcher, StatusDocs.Now);
    }

    // A run dir under the runs folder whose status file says it ended well.
    private async Task<string> RunDirAsync(string dirName, string name)
    {
        string dir = Path.Combine(_rig.Root, "runs", dirName);
        _ = Directory.CreateDirectory(dir);
        await PutAsync(Path.Combine(dir, "status.json"), StatusDocs.Make(name, launcherPid: 900, end: StatusDocs.End("success")).ToJsonString());
        return dir;
    }

    /// <summary>
    /// Lists --all in both modes under the common claims and returns the rows of the --json listing but the healthy one's.
    /// </summary>
    private async Task<List<JsonObject>> ListAsync(int others, params string[] absent)
    {
        TextWriter oldErr = Console.Error;
        await using StringWriter err = new();
        Console.SetError(err);
        try
        {
            (int plainCode, string plain) = await _rig.AllAsync();
            (int jsonCode, string json) = await _rig.AllAsync("--json");

            Assert.Equal(0, plainCode);
            Assert.Equal(0, jsonCode);
            Assert.Equal(string.Empty, err.ToString());
            string[] lines = TerminalAssert.AssertPlain(plain, others + 1);
            Assert.Contains(lines, l => l.StartsWith("running", StringComparison.Ordinal) && l.Contains("healthy", StringComparison.Ordinal));
            Assert.Empty(TerminalAssert.JsonProblems(json));
            List<JsonObject> rows = StatusIndexRig.JsonRows(json);
            Assert.Equal(others + 1, rows.Count);
            foreach (string text in absent.Append(Marker))
            {
                Assert.DoesNotContain(text, plain + json, StringComparison.Ordinal);
            }

            JsonObject healthy = rows.Single(r => r["run"]?["name"]?.GetValue<string>() is "healthy");
            Assert.Equal("running", healthy["state"]!.GetValue<string>());
            return [.. rows.Where(r => r != healthy)];
        }
        finally
        {
            Console.SetError(oldErr);
        }
    }

    private async Task<JsonObject> OneRowAsync(params string[] absent) => Assert.Single(await ListAsync(1, absent));

    private static void AssertUnreadable(JsonObject row)
    {
        Assert.Equal("unreadable", row["state"]!.GetValue<string>());
        string detail = row["detail"]!.GetValue<string>();
        Assert.DoesNotContain("Exception", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_symbolic_link_as_a_pointer_is_an_unreadable_row_and_its_target_is_never_readAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string target = Path.Combine(_rig.Outside(), "valid-pointer.json");
        await PutAsync(target, GoodPointer(Path.Combine(_rig.Root, "runs", Marker)).ToJsonString());
        _ = Directory.CreateDirectory(_rig.Folder);
        _ = File.CreateSymbolicLink(PointerPath(), target);

        AssertUnreadable(await OneRowAsync());
    }

    [Fact]
    public async Task A_dangling_or_looping_link_as_a_pointer_is_an_unreadable_rowAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        _ = Directory.CreateDirectory(_rig.Folder);
        _ = File.CreateSymbolicLink(PointerPath(), Path.Combine(_rig.Outside(), "gone.json"));
        string loop = PointerPath();
        _ = File.CreateSymbolicLink(loop, loop);

        List<JsonObject> rows = await ListAsync(2);

        Assert.All(rows, r => AssertUnreadable(r));
    }

    [Fact]
    public async Task A_fifo_as_a_pointer_is_an_unreadable_row_and_the_command_does_not_hangAsync()
    {
        _ = Directory.CreateDirectory(_rig.Folder);
        await Fifo.MakeAsync(PointerPath());

        AssertUnreadable(await OneRowAsync());
    }

    [Theory]
    [InlineData(OneMiB + 1)]
    [InlineData(3 * OneMiB)]
    public async Task A_pointer_over_one_MiB_is_an_unreadable_row_and_its_content_is_not_echoedAsync(int length)
    {
        string pointer = GoodPointer("/x").ToJsonString().TrimEnd('}') + ",\"pad\":\"" + new string('x', length) + "\"}";
        _ = await PointerAsync(pointer);

        AssertUnreadable(await OneRowAsync(new string('x', 100)));
    }

    [Fact]
    public async Task A_pointer_of_exactly_one_MiB_is_read_like_any_otherAsync()
    {
        byte[] body = Encoding.UTF8.GetBytes(GoodPointer(Path.Combine(_rig.Root, "runs", "20261122-115000-padded")).ToJsonString());
        byte[] bytes = new byte[OneMiB];
        body.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)' ', body.Length, bytes.Length - body.Length);
        _ = Directory.CreateDirectory(_rig.Folder);
        await File.WriteAllBytesAsync(PointerPath(), bytes, TestContext.Current.CancellationToken);

        JsonObject row = await OneRowAsync();

        Assert.Equal("gone", row["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/y\",\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/y\",\"launcher\":{\"pid\":1,\"pid\":2},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/\\ud800y\",\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/y\",\"launcher\":{},\"note\":\"SECRET-MARKER\\udc00\"}")]
    [InlineData("{\"schema\":1,\"run_dir\":\"/x/y\",\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/2\",\"run_dir\":\"/x/y\",\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":7,\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":[\"/x/y\"],\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"launcher\":{},\"note\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/y\",\"launcher\":\"SECRET-MARKER\"}")]
    [InlineData("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/y\",\"note\":\"SECRET-MARKER\"}")]
    [InlineData("[\"SECRET-MARKER\"]")]
    [InlineData("\"SECRET-MARKER\"")]
    [InlineData("null")]
    [InlineData("12345")]
    [InlineData("{ SECRET-MARKER")]
    [InlineData("SECRET-MARKER")]
    public async Task A_pointer_with_the_wrong_shape_is_an_unreadable_row_whatever_it_holdsAsync(string text)
    {
        _ = await PointerAsync(text);

        AssertUnreadable(await OneRowAsync());
    }

    [Fact]
    public async Task A_pointer_nested_deeper_than_16_levels_is_an_unreadable_rowAsync()
    {
        _ = await PointerAsync("{\"schema\":\"lean-worker.pointer/1\",\"run_dir\":\"/x/y\",\"launcher\":{},\"deep\":" + new string('[', 40) + "\"SECRET-MARKER\"" + new string(']', 40) + "}");

        AssertUnreadable(await OneRowAsync());
    }

    [Fact]
    public async Task An_empty_pointer_file_is_an_unreadable_rowAsync()
    {
        _ = await PointerAsync(string.Empty);

        AssertUnreadable(await OneRowAsync());
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/x/../y")]
    [InlineData("/x/./y")]
    [InlineData("/x//y")]
    [InlineData("/x/y/")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("~/runs/x")]
    [InlineData("file:///x/y")]
    public async Task A_run_dir_that_is_not_an_absolute_plain_path_is_an_unreadable_row_and_nothing_is_openedAsync(string runDir)
    {
        _ = await PointerAsync(GoodPointer(runDir).ToJsonString());

        AssertUnreadable(await OneRowAsync());
    }

    [Fact]
    public async Task A_run_dir_with_a_nul_character_or_over_4096_characters_is_an_unreadable_rowAsync()
    {
        _ = await PointerAsync(GoodPointer("/x/" + (char)0 + "y").ToJsonString());
        _ = await PointerAsync(GoodPointer("/" + new string('d', 5000)).ToJsonString());

        List<JsonObject> rows = await ListAsync(2);

        Assert.All(rows, r => AssertUnreadable(r));
    }

    [Fact]
    public async Task A_run_dir_that_is_a_symbolic_link_is_an_unreadable_row_and_its_target_is_never_readAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string target = await RunDirAsync("20261122-115000-target", Marker);
        string link = Path.Combine(_rig.Root, "runs", "20261122-115000-linked");
        _ = File.CreateSymbolicLink(link, target);
        _ = await PointerAsync(GoodPointer(link).ToJsonString());

        // The target is a run dir of its own (not pointed at), so only the link's row exists.
        AssertUnreadable(await OneRowAsync());
    }

    [Fact]
    public async Task A_run_dir_that_is_a_file_is_a_gone_row_and_the_file_is_never_readAsync()
    {
        string file = Path.Combine(_rig.Outside(), "20261122-115000-a-file");
        await PutAsync(file, Marker);
        _ = await PointerAsync(GoodPointer(file).ToJsonString());

        JsonObject row = await OneRowAsync();

        Assert.Equal("gone", row["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_dir_whose_path_carries_an_escape_sequence_or_a_newline_is_listed_clean_one_line_per_runAsync()
    {
        string esc = await RunDirAsync("20261122-115000-evil" + (char)27 + "[2J" + (char)27 + "]0;pwned" + (char)7, "name-esc" + (char)27 + "[2J");
        string newline = await RunDirAsync("20261122-115001-two" + (char)10 + "lines", "name-two" + (char)10 + "lines");
        foreach (string dir in new[] { esc, newline })
        {
            _ = await PointerAsync(GoodPointer(dir).ToJsonString());
        }

        _ = Directory.CreateDirectory(Path.Combine(_rig.Root, "runs", "20261122-115002-nopointer" + (char)27 + "[2J"));
        string gone = Path.Combine(_rig.Root, "runs", "20261122-115003-gone" + (char)27 + "[2J" + (char)10 + "x");
        _ = await PointerAsync(GoodPointer(gone).ToJsonString());

        List<JsonObject> rows = await ListAsync(3);

        Assert.Equal(2, rows.Count(r => r["state"]!.GetValue<string>() is "success"));
        Assert.Contains(rows, r => r["state"]!.GetValue<string>() is "gone");
    }

    [Fact]
    public async Task Owner_keys_that_fail_the_check_are_dropped_and_the_first_good_one_is_shownAsync()
    {
        string dir = await RunDirAsync("20261122-115000-keys", "keys");
        JsonObject pointer = GoodPointer(dir);
        pointer["owner_keys"] = new JsonArray((char)27 + "[31mred", "has space", 5, null, new string('k', 201), "good-key", "second-key");
        _ = await PointerAsync(pointer.ToJsonString());

        (_, string plain) = await _rig.AllAsync();
        (_, string owned) = await _rig.CallAsync("--owner", "second-key");
        (_, string bad) = await _rig.CallAsync("--owner", "has space");

        Assert.Matches(@"keys\s+good-key\s", plain);
        Assert.Contains("owner second-key, 0 live, 1 ended", owned, StringComparison.Ordinal);
        Assert.Contains("--owner needs a key", bad, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_keys_that_are_not_an_array_leave_a_pointer_that_matches_no_owner_but_is_listed_by_allAsync()
    {
        string dir = await RunDirAsync("20261122-115000-nokeys", "nokeys");
        JsonObject pointer = GoodPointer(dir);
        pointer["owner_keys"] = "claude-session:sess-A";
        _ = await PointerAsync(pointer.ToJsonString());

        (_, string mine) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--json");
        JsonObject listed = await OneRowAsync();

        Assert.Equal(["healthy"], StatusIndexRig.JsonNames(mine));
        Assert.Equal("success", listed["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_parent_run_that_is_no_run_id_is_ignored_and_the_run_stays_at_the_top_levelAsync()
    {
        string dir = await RunDirAsync("20261122-115000-badparent", "badparent");
        JsonObject pointer = GoodPointer(dir);
        pointer["parent_run"] = "../../etc/" + (char)27 + "[2J";
        _ = await PointerAsync(pointer.ToJsonString());

        JsonObject row = await OneRowAsync();

        Assert.Null(row["depth"]);
    }

    [Theory]
    [InlineData("{\"pid\":1e300,\"start_ticks\":5000,\"boot_id\":\"boot-A\"}")]
    [InlineData("{\"pid\":-5,\"start_ticks\":5000,\"boot_id\":\"boot-A\"}")]
    [InlineData("{\"pid\":99999999999999,\"start_ticks\":5000,\"boot_id\":\"boot-A\"}")]
    [InlineData("{\"pid\":100,\"start_ticks\":\"5000\",\"boot_id\":\"boot-A\"}")]
    [InlineData("{\"pid\":100,\"start_ticks\":5000,\"boot_id\":7}")]
    [InlineData("{}")]
    public async Task A_launcher_identity_with_odd_values_is_an_unknown_row_not_a_crashAsync(string launcher)
    {
        string dir = Path.Combine(_rig.Root, "runs", "20261122-115000-oddlauncher");
        _ = Directory.CreateDirectory(dir);
        await PutAsync(Path.Combine(dir, "status.json"), StatusDocs.Make("oddlauncher", worker: StatusDocs.Worker()).ToJsonString());
        JsonObject pointer = GoodPointer(dir);
        pointer["launcher"] = JsonNode.Parse(launcher);
        _ = await PointerAsync(pointer.ToJsonString());

        JsonObject row = await OneRowAsync();

        Assert.Equal("unknown", row["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Files_and_directories_in_the_index_that_are_no_pointer_names_are_ignored_altogetherAsync()
    {
        _ = _rig.WritePointer("notes.json", Marker);
        _ = _rig.WritePointer(".tmp-0123456789abcdef", Marker);
        _ = _rig.WritePointer("20261122-115000-5-r1.json.bak", Marker);
        string nested = Path.Combine(_rig.Folder, "20261122-115000-6-r1.json");
        _ = Directory.CreateDirectory(nested);
        await PutAsync(Path.Combine(nested, "20261122-115000-7-r1.json"), Marker);
        _ = Directory.CreateDirectory(Path.Combine(_rig.Folder, "sub"));
        await PutAsync(Path.Combine(_rig.Folder, "sub", "20261122-115000-8-r1.json"), Marker);

        List<JsonObject> rows = await ListAsync(0);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task An_index_folder_that_is_a_symbolic_link_lists_nothingAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string real = Path.Combine(_rig.Outside(), "runs");
        Directory.Move(_rig.Folder, real);
        _ = Directory.CreateSymbolicLink(_rig.Folder, real);

        (int code, string output) = await _rig.AllAsync();

        Assert.Equal(0, code);
        Assert.Equal(["lean-worker status: all runs in " + _rig.Folder + ", 0 live, 0 ended, 0 need attention"], StatusIndexRig.Lines(output));
    }
}
