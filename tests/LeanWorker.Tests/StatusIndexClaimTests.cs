using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The claim-guarded publish of StatusIndex.Write (issue #159): a pointer name is published only under its
/// claim file ".claim-name", made with an exclusive create. Two writers of one name never get the same name, a
/// name that exists or whose claim is held is skipped, an existing file is never replaced, and after a write
/// the folder holds pointers only.
/// </summary>
public sealed class StatusIndexClaimTests : IDisposable
{
    private const string Name = "20261122-135850-1-r1.json";
    private const string Stem = "20261122-135850-1-r1";
    private const int Writers = 12;
    private const int Rounds = 50;

    private static readonly DateTimeOffset _started = new(2026, 11, 22, 13, 58, 50, TimeSpan.Zero);

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private static JsonObject Pointer(string runDir) =>
        StatusIndex.Pointer(_started, runDir, "chain-1", parentRun: null, ["claude-session:s1", "pid:1@2"], new JsonObject { ["pid"] = 41233, ["start_ticks"] = 7432110, ["boot_id"] = "b" });

    private string Folder() => Path.Combine(_dirs.Create("lw-claim"), "runs");

    private static string[] EntryNames(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    private static string[] Sorted(params string[] names) => [.. names.Order(StringComparer.Ordinal)];

    private static string RunDirOf(string path) => JsonNode.Parse(File.ReadAllText(path))!["run_dir"]!.GetValue<string>();

    /// <summary>
    /// Starts <paramref name="count"/> threads that all wait on one gate, opens the gate and joins them; the
    /// names they got come back by writer number. No verdict depends on how long anything takes.
    /// </summary>
    private static string[] Race(string folder, int count)
    {
        string[] names = new string[count];
        List<Exception> failures = [];
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using ManualResetEventSlim gate = new(initialState: false);
        Thread[] threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int writer = i;
            threads[i] = new Thread(() =>
            {
                gate.Wait(cancel);
                try
                {
                    names[writer] = StatusIndex.Write(folder, Name, Pointer("/runs/" + writer.ToString(CultureInfo.InvariantCulture)));
                }
                catch (IOException ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            });
            threads[i].Start();
        }

        gate.Set();
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(failures);
        return names;
    }

    private static void AssertOnlyPointers(string folder)
    {
        string[] entries = EntryNames(folder);
        Assert.All(entries, n => Assert.True(StatusIndex.IsPointerName(n), n));
        Assert.DoesNotContain(entries, n => n.StartsWith(".tmp-", StringComparison.Ordinal) || n.StartsWith(".claim-", StringComparison.Ordinal));
        Assert.All(Directory.GetFileSystemEntries(folder), e => Assert.True(File.Exists(e), e));
    }

    private static string SuffixName(int k) => Stem + "-" + k.ToString(CultureInfo.InvariantCulture) + ".json";

    [Fact]
    public void Writes_of_one_name_from_12_threads_in_50_rounds_each_get_a_pointer_of_their_own()
    {
        string root = _dirs.Create("lw-claim-rounds");

        for (int round = 0; round < Rounds; round++)
        {
            string folder = Path.Combine(root, "round-" + round.ToString(CultureInfo.InvariantCulture));
            _ = Directory.CreateDirectory(folder);

            string[] names = Race(folder, Writers);

            string label = "round " + round.ToString(CultureInfo.InvariantCulture);
            string[] files = Directory.GetFileSystemEntries(folder);
            string[] inFolder = EntryNames(folder);
            string[] given = [.. names.Order(StringComparer.Ordinal)];
            HashSet<string> distinctNames = new(names, StringComparer.Ordinal);
            HashSet<string> distinctRunDirs = new(files.Select(f => RunDirOf(f)), StringComparer.Ordinal);
            Assert.True(Writers == distinctNames.Count, label + ": the names given were " + string.Join(',', names));
            Assert.True(Writers == files.Length, label + ": the folder holds " + string.Join(',', inFolder));
            Assert.All(inFolder, n => Assert.True(StatusIndex.IsPointerName(n), n));
            Assert.Equal(given, inFolder);
            Assert.True(Writers == distinctRunDirs.Count, label + ": a run_dir was lost");
            for (int i = 0; i < Writers; i++)
            {
                Assert.Equal("/runs/" + i.ToString(CultureInfo.InvariantCulture), RunDirOf(Path.Combine(folder, names[i])));
            }
        }
    }

    [Fact]
    public void A_name_whose_claim_is_held_is_skipped_and_the_claim_is_left_as_it_was()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        string claim = Path.Combine(folder, ".claim-" + Name);
        File.WriteAllText(claim, "held by another writer");
        DateTime claimTime = File.GetLastWriteTimeUtc(claim);

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/after-the-claim"));

        Assert.Equal(SuffixName(2), written);
        Assert.False(File.Exists(Path.Combine(folder, Name)));
        Assert.True(File.Exists(claim));
        Assert.Equal("held by another writer", File.ReadAllText(claim));
        Assert.Equal(claimTime, File.GetLastWriteTimeUtc(claim));
        Assert.Equal(Sorted(".claim-" + Name, written), EntryNames(folder));
        Assert.Equal("/runs/after-the-claim", RunDirOf(Path.Combine(folder, written)));
    }

    [Fact]
    public void A_held_claim_of_the_suffix_name_too_is_skipped_to_the_next_suffix()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".claim-" + Name), "one");
        File.WriteAllText(Path.Combine(folder, ".claim-" + SuffixName(2)), "two");

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/third"));

        Assert.Equal(SuffixName(3), written);
        Assert.Equal(Sorted(".claim-" + Name, ".claim-" + SuffixName(2), written), EntryNames(folder));
        Assert.Equal("one", File.ReadAllText(Path.Combine(folder, ".claim-" + Name)));
        Assert.Equal("two", File.ReadAllText(Path.Combine(folder, ".claim-" + SuffixName(2))));
    }

    [Fact]
    public void A_name_that_exists_is_never_replaced_and_the_writer_gets_the_next_suffix()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        string existing = Path.Combine(folder, Name);
        byte[] before = [0x7b, 0x00, 0xff, 0x0a, 0x7d];
        File.WriteAllBytes(existing, before);

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/newcomer"));

        Assert.Equal(SuffixName(2), written);
        Assert.Equal(before, File.ReadAllBytes(existing));
        Assert.Equal("/runs/newcomer", RunDirOf(Path.Combine(folder, written)));
        Assert.Equal(Sorted(Name, written), EntryNames(folder));
    }

    [Fact]
    public void A_pointer_written_by_this_code_is_not_replaced_by_the_next_write_of_its_name()
    {
        string folder = Folder();
        string first = StatusIndex.Write(folder, Name, Pointer("/runs/first"));
        byte[] before = File.ReadAllBytes(Path.Combine(folder, first));

        string second = StatusIndex.Write(folder, Name, Pointer("/runs/second"));

        Assert.Equal(Name, first);
        Assert.Equal(SuffixName(2), second);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, first)));
    }

    [Fact]
    public void After_a_plain_write_nothing_but_the_pointer_is_left()
    {
        string folder = Folder();

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/plain"));

        Assert.Equal([written], EntryNames(folder));
        AssertOnlyPointers(folder);
    }

    [Fact]
    public void After_a_write_that_took_a_suffix_nothing_but_pointers_is_left()
    {
        string folder = Folder();
        _ = StatusIndex.Write(folder, Name, Pointer("/runs/one"));
        _ = StatusIndex.Write(folder, Name, Pointer("/runs/two"));

        string third = StatusIndex.Write(folder, Name, Pointer("/runs/three"));

        Assert.Equal(SuffixName(3), third);
        Assert.Equal(Sorted(Name, SuffixName(2), SuffixName(3)), EntryNames(folder));
        AssertOnlyPointers(folder);
    }

    [Fact]
    public void After_a_write_that_failed_because_all_99_names_are_taken_the_files_are_untouched_and_nothing_else_is_left()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        Dictionary<string, byte[]> before = [];
        for (int k = 1; k <= 99; k++)
        {
            string name = k is 1 ? Name : SuffixName(k);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("taken " + k.ToString(CultureInfo.InvariantCulture));
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            before[name] = bytes;
        }

        IOException ex = Assert.Throws<IOException>(() => StatusIndex.Write(folder, Name, Pointer("/runs/nowhere")));

        Assert.Contains(Name, ex.Message, StringComparison.Ordinal);
        Assert.Equal(99, Directory.GetFileSystemEntries(folder).Length);
        AssertOnlyPointers(folder);
        foreach ((string name, byte[] bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, name)));
        }
    }

    [Fact]
    public void After_a_write_next_to_a_held_claim_only_that_claim_and_the_pointers_are_left()
    {
        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".claim-" + Name), string.Empty);

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/next-to-claim"));

        Assert.Equal(Sorted(".claim-" + Name, written), EntryNames(folder));
        Assert.DoesNotContain(EntryNames(folder), n => n.StartsWith(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void The_listing_never_returns_a_claim_or_a_temp_name_as_a_pointer()
    {
        string folder = Folder();
        string written = StatusIndex.Write(folder, Name, Pointer("/runs/listed"));
        File.WriteAllText(Path.Combine(folder, ".claim-" + Name), string.Empty);
        File.WriteAllText(Path.Combine(folder, ".claim-" + SuffixName(2)), string.Empty);
        File.WriteAllText(Path.Combine(folder, ".tmp-0123456789abcdef0123456789abcdef"), "{}");

        (List<string> names, bool complete) = StatusIndex.List(folder, ReadBudget.Default());

        Assert.True(complete);
        Assert.Equal([written], names);
        Assert.False(StatusIndex.IsPointerName(".claim-" + Name));
        Assert.False(StatusIndex.IsPointerName(".claim-" + SuffixName(2)));
        Assert.False(StatusIndex.IsPointerName(".tmp-0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void The_pointer_is_0600_also_with_a_suffix_and_a_claim_left_by_the_setup_is_not_touched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Folder();
        _ = Directory.CreateDirectory(folder);
        string claim = Path.Combine(folder, ".claim-" + Name);
        File.WriteAllText(claim, "held");
        const UnixFileMode claimMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(claim, claimMode);

        string written = StatusIndex.Write(folder, Name, Pointer("/runs/mode"));

        Assert.Equal(SuffixName(2), written);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, written)));
        Assert.Equal(claimMode, File.GetUnixFileMode(claim));
        Assert.Equal("held", File.ReadAllText(claim));
    }

    [Fact]
    public void Every_pointer_of_a_race_is_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Folder();
        _ = Directory.CreateDirectory(folder);

        _ = Race(folder, Writers);

        string[] files = Directory.GetFiles(folder);
        Assert.Equal(Writers, files.Length);
        foreach (string file in files)
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }
}
