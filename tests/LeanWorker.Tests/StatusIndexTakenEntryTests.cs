using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The three minors of the claim-guarded publish of StatusIndex.Write (issue #160), the write side: any entry at the
/// pointer name or at its claim (a directory, a link to a directory, a link to a file, a dangling link) means "taken",
/// the entry is left exactly as it was, and a lasting fault (a folder that cannot be written, a claim name the file
/// system refuses) surfaces as an exception instead of walking the suffixes. The retry of a claim create on a free
/// name needs a holder that releases its claim between another writer's failed create and its checks; without a seam
/// in the production code that interleaving cannot be forced, so it is covered by the 50-round race test (which goes
/// through StatusIndex.TakeClaim) and by reading.
/// </summary>
public sealed class StatusIndexTakenEntryTests : IDisposable
{
    private const string Name = "20261122-135850-1-r1.json";
    private const string Stem = "20261122-135850-1-r1";

    private static readonly DateTimeOffset _started = new(2026, 11, 22, 13, 58, 50, TimeSpan.Zero);

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private static JsonObject Pointer(string runDir) =>
        StatusIndex.Pointer(_started, runDir, "chain-1", parentRun: null, ["claude-session:s1", "pid:1@2"], new JsonObject { ["pid"] = 41233, ["start_ticks"] = 7432110, ["boot_id"] = "b" });

    private string Folder()
    {
        string folder = Path.Combine(_dirs.Create("lw-taken"), "runs");
        _ = Directory.CreateDirectory(folder);
        return folder;
    }

    private static string[] EntryNames(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    private static string[] Sorted(params string[] names) => [.. names.Order(StringComparer.Ordinal)];

    private static string RunDirOf(string path) => JsonNode.Parse(File.ReadAllText(path))!["run_dir"]!.GetValue<string>();

    private static string SuffixName(int k) => Stem + "-" + k.ToString(CultureInfo.InvariantCulture) + ".json";

    private static void SkipUnlessSupported(string kind) =>
        Assert.SkipWhen(OperatingSystem.IsWindows() && kind is not "directory", "symbolic links need privileges on Windows");

    /// <summary>
    /// Puts an entry of <paramref name="kind"/> at <paramref name="path"/> (links point to something outside the folder)
    /// and returns the check that it is exactly as it was: still a directory, still empty, or still the same link with
    /// the same target, nothing created inside or behind it.
    /// </summary>
    private Action Plant(string kind, string path)
    {
        string outside = _dirs.Create("lw-taken-outside");
        return kind switch
        {
            "directory" => PlantDirectory(path),
            "link-to-directory" => PlantLinkToDirectory(path, outside),
            "link-to-file" => PlantLinkToFile(path, outside),
            "dangling-link" => PlantDanglingLink(path, outside),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown entry kind"),
        };
    }

    private static Action PlantDirectory(string path)
    {
        _ = Directory.CreateDirectory(path);
        return () =>
        {
            Assert.True(Directory.Exists(path));
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Empty(Directory.GetFileSystemEntries(path));
        };
    }

    private static Action PlantLinkToDirectory(string path, string outside)
    {
        string targetDir = Path.Combine(outside, "dir");
        _ = Directory.CreateDirectory(targetDir);
        _ = Directory.CreateSymbolicLink(path, targetDir);
        return () =>
        {
            Assert.Equal(targetDir, new FileInfo(path).LinkTarget);
            Assert.True(Directory.Exists(targetDir));
            Assert.Empty(Directory.GetFileSystemEntries(targetDir));
            Assert.Equal([targetDir], Directory.GetFileSystemEntries(outside));
        };
    }

    private static Action PlantLinkToFile(string path, string outside)
    {
        string targetFile = Path.Combine(outside, "file.txt");
        byte[] bytes = [0x7b, 0x00, 0xff, 0x0a, 0x7d];
        File.WriteAllBytes(targetFile, bytes);
        _ = File.CreateSymbolicLink(path, targetFile);
        return () =>
        {
            Assert.Equal(targetFile, new FileInfo(path).LinkTarget);
            Assert.Equal(bytes, File.ReadAllBytes(targetFile));
            Assert.Equal([targetFile], Directory.GetFileSystemEntries(outside));
        };
    }

    private static Action PlantDanglingLink(string path, string outside)
    {
        string missing = Path.Combine(outside, "missing");
        _ = File.CreateSymbolicLink(path, missing);
        return () =>
        {
            Assert.Equal(missing, new FileInfo(path).LinkTarget);
            Assert.False(Path.Exists(missing));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        };
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("link-to-directory")]
    [InlineData("link-to-file")]
    [InlineData("dangling-link")]
    public void An_entry_of_any_kind_at_the_pointer_name_counts_as_taken_and_is_left_as_it_was(string kind)
    {
        SkipUnlessSupported(kind);
        string folder = Folder();
        Action unchanged = Plant(kind, Path.Combine(folder, Name));

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/after-the-entry"));

        Assert.Equal(SuffixName(2), written);
        unchanged();
        Assert.Equal(Sorted(Name, written), EntryNames(folder));
        Assert.Equal("/runs/after-the-entry", RunDirOf(Path.Combine(folder, written)));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("link-to-directory")]
    [InlineData("link-to-file")]
    [InlineData("dangling-link")]
    public void An_entry_of_any_kind_at_the_claim_counts_as_taken_and_is_left_as_it_was(string kind)
    {
        SkipUnlessSupported(kind);
        string folder = Folder();
        const string claim = ".claim-" + Name;
        Action unchanged = Plant(kind, Path.Combine(folder, claim));

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/after-the-claim"));

        Assert.Equal(SuffixName(2), written);
        unchanged();
        Assert.Equal(Sorted(claim, written), EntryNames(folder));
        Assert.False(Path.Exists(Path.Combine(folder, Name)));
        Assert.Equal("/runs/after-the-claim", RunDirOf(Path.Combine(folder, written)));
    }

    [Fact]
    public void A_directory_at_the_name_and_a_dangling_link_at_the_claim_of_the_next_suffix_walk_to_the_third_name()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        string folder = Folder();
        Action nameUnchanged = Plant("directory", Path.Combine(folder, Name));
        string claimTwo = ".claim-" + SuffixName(2);
        Action claimUnchanged = Plant("dangling-link", Path.Combine(folder, claimTwo));

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/third"));

        Assert.Equal(SuffixName(3), written);
        nameUnchanged();
        claimUnchanged();
        Assert.Equal(Sorted(Name, claimTwo, written), EntryNames(folder));
    }

    [Fact]
    public void A_folder_the_writer_cannot_write_makes_Write_throw_and_leaves_no_pointer_claim_or_temp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.SkipWhen(Environment.IsPrivilegedProcess, "needs mode bits that bind the user");
        string folder = Folder();
        const UnixFileMode readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        File.SetUnixFileMode(folder, readOnly);

        try
        {
            // The temp is the first thing written, so the refusal comes from there, as UnauthorizedAccessException;
            // it is not an IOException that the claim logic could read as "taken" and walk the suffixes for.
            _ = Assert.Throws<UnauthorizedAccessException>(() => StatusIndex.Write(folder, Name, Pointer("/runs/nowhere")));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void A_claim_name_the_file_system_refuses_surfaces_as_the_create_error_and_does_not_walk_the_suffixes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the 255-byte name limit is the Linux file systems'");
        string folder = Folder();
        string longName = new string('a', 245) + ".json";
        Assert.Equal(250, longName.Length);

        IOException ex = Assert.ThrowsAny<IOException>(() => StatusIndex.Write(folder, longName, Pointer("/runs/too-long")));

        // The name itself fits, its claim (".claim-" in front) does not: neither exists, so it is not "taken".
        Assert.DoesNotContain("are all taken", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void A_name_that_fits_with_its_claim_in_the_same_folder_is_written_with_the_same_helper()
    {
        // The control of the test above: the refusal is the claim name's length, not the folder or the pointer.
        string folder = Folder();
        string name = new string('a', 200) + ".json";

        string written = StatusIndex.Write(folder, name, Pointer("/runs/fits"));

        Assert.Equal(name, written);
        Assert.Equal([name], EntryNames(folder));
        Assert.Equal("/runs/fits", RunDirOf(Path.Combine(folder, name)));
        Assert.Equal(Encoding.UTF8.GetBytes(Pointer("/runs/fits").ToJsonString(Json.Indented)), File.ReadAllBytes(Path.Combine(folder, name)));
    }
}
