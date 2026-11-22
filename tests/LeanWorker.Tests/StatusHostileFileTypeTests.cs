using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// `status` over run dirs whose entries are not what the launcher wrote: a symbolic link as the run
/// dir or as a file (dangling and looping ones too), a FIFO, a directory or an empty file in a
/// status file's place, a file at and just over the 1 MiB read limit. Each makes THAT run an
/// unreadable row with a fixed reason, in plain and --json mode, while the healthy run beside it is
/// listed as usual; the target of a link is never read, and nothing blocks. The bound pinned for the
/// size: a file of exactly 1 MiB (1048576 bytes) is read, one byte more is refused.
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusHostileFileTypeTests : IDisposable
{
    private const int OneMiB = 1024 * 1024;

    private static readonly TimeSpan _fifoLimit = TimeSpan.FromSeconds(8);

    private readonly StatusHostileRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>
    /// The two files a run dir is read through: status.json, and summary.json of a dir with no status.json.
    /// </summary>
    public static TheoryData<string> Files => ["status.json", "summary.json"];

    private static string Valid() => StatusHostileRig.Hostile(StatusHostileRig.Marker).ToJsonString();

    private static void Link(string link, string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        _ = File.CreateSymbolicLink(link, target);
    }

    private string ValidFileOutside(string file)
    {
        string path = Path.Combine(_rig.Outside(), file);
        File.WriteAllText(path, file is "status.json" ? Valid() : "{\"status\":\"" + StatusHostileRig.Marker + "\"}");
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_symbolic_link_as_the_run_directory_is_an_unreadable_row_and_its_target_is_never_readAsync(bool targetInsideRoot)
    {
        string target = Path.Combine(targetInsideRoot ? _rig.Root : _rig.Outside(), "target");
        _ = Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "status.json"), Valid(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(target, "summary.json"), "{\"status\":\"" + StatusHostileRig.Marker + "\"}", TestContext.Current.CancellationToken);
        Link(Path.Combine(_rig.RunsDir, StatusHostileRig.HostileId), target);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_symbolic_link_given_to_run_is_unreadable_in_both_modes_and_its_target_is_never_readAsync()
    {
        string target = Path.Combine(_rig.Outside(), "target");
        _ = Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "status.json"), Valid(), TestContext.Current.CancellationToken);
        string link = Path.Combine(_rig.Outside(), "link");
        Link(link, target);

        (int code, string plain, string err) = await _rig.CallAsync(TimeSpan.FromSeconds(30), "--run", link);
        (int jsonCode, string json, string jsonErr) = await _rig.CallAsync(TimeSpan.FromSeconds(30), "--run", link, "--json");

        Assert.Equal(0, code);
        Assert.Equal(0, jsonCode);
        _ = TerminalAssert.AssertPlain(plain, 1);
        Assert.Contains("unreadable", plain, StringComparison.Ordinal);
        Assert.Equal("unreadable", Assert.Single(StatusHostileRig.Rows(json))["state"]!.GetValue<string>());
        foreach (string text in new[] { plain, json, err, jsonErr })
        {
            Assert.DoesNotContain(StatusHostileRig.Marker, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task A_symbolic_link_as_the_file_is_an_unreadable_row_and_its_target_is_never_readAsync(string file)
    {
        string target = ValidFileOutside(file);
        Link(_rig.HostilePath(file), target);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_symbolic_link_as_summary_json_next_to_a_status_file_whose_launcher_is_gone_is_unreadable_tooAsync()
    {
        // The summary decides the state of a run whose launcher died; it is read only when it is a plain file.
        string dir = _rig.HostileDir();
        string doc = StatusDocs.Make("hostile", phase: "finishing", launcherPid: 902, worker: StatusDocs.Worker()).ToJsonString();
        await File.WriteAllTextAsync(Path.Combine(dir, "status.json"), doc, TestContext.Current.CancellationToken);
        Link(Path.Combine(dir, "summary.json"), ValidFileOutside("summary.json"));

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_dangling_link_as_the_run_directory_is_an_unreadable_rowAsync()
    {
        Link(Path.Combine(_rig.RunsDir, StatusHostileRig.HostileId), Path.Combine(_rig.Outside(), "gone"));

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task A_dangling_link_as_the_file_is_an_unreadable_rowAsync(string file)
    {
        Link(_rig.HostilePath(file), Path.Combine(_rig.Outside(), "gone.json"));

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task Two_run_directories_that_link_to_each_other_are_unreadable_rows_and_nothing_hangsAsync()
    {
        string a = Path.Combine(_rig.RunsDir, StatusHostileRig.HostileId);
        string b = Path.Combine(_rig.RunsDir, "20261122-114900-hostile-b");
        _ = Directory.CreateDirectory(_rig.RunsDir);
        Link(a, b);
        Link(b, a);

        StatusHostileRig.Listing l = await _rig.ListAsync(TimeSpan.FromSeconds(15));

        List<JsonObject> others = StatusHostileRig.AssertHealthyStillListed(l, otherRows: 2);
        Assert.All(others, r => Assert.Equal("unreadable", r["state"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_run_directory_that_links_to_the_runs_folder_itself_is_an_unreadable_row_and_nothing_hangsAsync()
    {
        _ = Directory.CreateDirectory(_rig.RunsDir);
        Link(Path.Combine(_rig.RunsDir, StatusHostileRig.HostileId), _rig.RunsDir);

        StatusHostileRig.Listing l = await _rig.ListAsync(TimeSpan.FromSeconds(15));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task A_file_that_links_to_itself_is_an_unreadable_row_and_nothing_hangsAsync(string file)
    {
        string path = _rig.HostilePath(file);
        Link(path, path);

        StatusHostileRig.Listing l = await _rig.ListAsync(TimeSpan.FromSeconds(15));

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    private static async Task MakeFifoAsync(string path)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Unix only");
        ProcessStartInfo psi = new("mkfifo") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        try
        {
            using Process mk = Process.Start(psi)!;
            await mk.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch (Win32Exception ex)
        {
            Assert.Skip("mkfifo cannot be run here: " + ex.Message);
        }

        Assert.True(new FileInfo(path).Exists, "mkfifo made no file at " + path);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task A_fifo_as_the_file_is_an_unreadable_row_and_the_command_does_not_hangAsync(string file)
    {
        await MakeFifoAsync(_rig.HostilePath(file));

        StatusHostileRig.Listing l = await _rig.ListAsync(_fifoLimit);

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_fifo_given_to_run_as_the_status_file_does_not_hang_eitherAsync()
    {
        string dir = _rig.HostileDir();
        await MakeFifoAsync(Path.Combine(dir, "status.json"));

        (int code, string plain, _) = await _rig.CallAsync(_fifoLimit, "--run", dir);

        Assert.Equal(0, code);
        Assert.Contains("unreadable", plain, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task A_directory_in_the_place_of_the_file_is_an_unreadable_rowAsync(string file)
    {
        _ = Directory.CreateDirectory(_rig.HostilePath(file));

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task An_empty_file_is_an_unreadable_rowAsync(string file)
    {
        await File.WriteAllBytesAsync(_rig.HostilePath(file), [], TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    private static byte[] Padded(string json, int length)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        Assert.True(body.Length <= length);
        byte[] bytes = new byte[length];
        body.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)' ', body.Length, length - body.Length);
        return bytes;
    }

    [Fact]
    public async Task A_status_file_of_exactly_one_MiB_is_read_like_any_otherAsync()
    {
        await File.WriteAllBytesAsync(_rig.HostilePath("status.json"), Padded(StatusHostileRig.Hostile().ToJsonString(), OneMiB), TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        JsonObject row = Assert.Single(StatusHostileRig.AssertHealthyStillListed(l));
        Assert.Equal("running", row["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(OneMiB + 1)]
    [InlineData(2 * OneMiB)]
    [InlineData(5 * OneMiB)]
    public async Task A_status_file_over_one_MiB_is_an_unreadable_rowAsync(int length)
    {
        await File.WriteAllBytesAsync(_rig.HostilePath("status.json"), Padded(StatusHostileRig.Hostile().ToJsonString(), length), TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l);
    }

    [Fact]
    public async Task A_summary_file_of_exactly_one_MiB_is_read_and_one_byte_more_is_an_unreadable_rowAsync()
    {
        await File.WriteAllBytesAsync(_rig.HostilePath("summary.json"), Padded("{\"status\":\"success\"}", OneMiB), TestContext.Current.CancellationToken);
        StatusHostileRig.Listing within = await _rig.ListAsync();
        await File.WriteAllBytesAsync(_rig.HostilePath("summary.json"), Padded("{\"status\":\"success\"}", OneMiB + 1), TestContext.Current.CancellationToken);
        StatusHostileRig.Listing over = await _rig.ListAsync();

        JsonObject legacy = Assert.Single(StatusHostileRig.AssertHealthyStillListed(within));
        Assert.Equal("legacy", legacy["state"]!.GetValue<string>());
        Assert.Equal("success", legacy["summary_status"]!.GetValue<string>());
        _ = StatusHostileRig.AssertOnlyHostileUnreadable(over);
    }

    [Fact]
    public async Task A_large_file_is_refused_without_its_content_reaching_the_outputAsync()
    {
        string text = Valid().TrimEnd('}') + ",\"pad\":\"" + new string('x', OneMiB) + "\"}";
        await File.WriteAllTextAsync(_rig.HostilePath("status.json"), text, TestContext.Current.CancellationToken);

        StatusHostileRig.Listing l = await _rig.ListAsync();

        _ = StatusHostileRig.AssertOnlyHostileUnreadable(l, new string('x', 100));
    }
}
