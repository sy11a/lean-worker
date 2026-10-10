using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace LeanWorker.Tests;

// Snapshot values are "<final-target>|<sha-or-sentinel>" under the trusted path itself; collecting paths
// reads PATH (FindOnPath), so the class shares the process-state collection with the launcher tests.
[Collection("launcher-process-state")]
public sealed class GateTrustTests : IDisposable
{
    private const string Sha256OfHello = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private string NewDir() => _dirs.Create("lw-trust");

    private static string StateOf(string value) => value[(value.IndexOf('|', StringComparison.Ordinal) + 1)..];

    private static Dictionary<string, HashSet<GateTrust.TrustSource>> SourcesOf(string path, GateTrust.TrustSource source) =>
        new(StringComparer.Ordinal) { [path] = [source] };

    [Fact]
    public void A_report_path_outside_the_trusted_set_is_no_violation() =>
        Assert.Null(GateTrust.ReportPathViolation("/r/report.sarif", SourcesOf("/r/other", GateTrust.TrustSource.ArgvEntry)));

    [Fact]
    public void A_null_or_empty_report_path_is_no_violation()
    {
        Dictionary<string, HashSet<GateTrust.TrustSource>> sources = SourcesOf("/r/a", GateTrust.TrustSource.ArgvEntry);
        Assert.Null(GateTrust.ReportPathViolation(reportPath: null, sources));
        Assert.Null(GateTrust.ReportPathViolation(string.Empty, sources));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_report_path_that_is_trusted_from_any_source_is_a_violation(bool argvEntry)
    {
        GateTrust.TrustSource source = argvEntry ? GateTrust.TrustSource.ArgvEntry : GateTrust.TrustSource.Executable;
        Assert.Equal(
            "the gate's report path is a trusted input: /r/gate.sh",
            GateTrust.ReportPathViolation("/r/gate.sh", SourcesOf("/r/gate.sh", source)));
    }

    [Fact]
    public void A_declared_output_or_a_path_under_the_run_directory_is_no_violation()
    {
        Dictionary<string, HashSet<GateTrust.TrustSource>> sources = SourcesOf("/r/out.sarif", GateTrust.TrustSource.ArgvEntry);
        Assert.Null(GateTrust.ReportPathViolation("/r/out.sarif", sources, new HashSet<string>(StringComparer.Ordinal) { "/r/out.sarif" }));

        Dictionary<string, HashSet<GateTrust.TrustSource>> underRun = SourcesOf("/runs/20260101-golden/x.json", GateTrust.TrustSource.ArgvEntry);
        Assert.Null(GateTrust.ReportPathViolation("/runs/20260101-golden/x.json", underRun, runDirectory: "/runs/20260101-golden"));
    }

    [Fact]
    public async Task An_excluded_path_is_dropped_from_both_sides_of_the_set_differenceAsync()
    {
        string dir = NewDir();
        string kept = Path.Combine(dir, "kept.txt");
        string output = Path.Combine(dir, "out.sarif");
        await File.WriteAllTextAsync(kept, "k", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output, "o", TestContext.Current.CancellationToken);
        GateTrust.Snapshot before = await GateTrust.HashAsync([kept, output]);

        Assert.Equal([output], GateTrust.SetDifferences(before, [kept]), StringComparer.Ordinal);
        Assert.Empty(GateTrust.SetDifferences(before, [kept], new HashSet<string>(StringComparer.Ordinal) { output }));
    }

    [Fact]
    public async Task Unchanged_files_report_no_differenceAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(path, "same", TestContext.Current.CancellationToken);
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
        Assert.Empty(GateTrust.Changed(before, after));
    }

    [Fact]
    public async Task A_changed_file_is_reportedAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(path, "before", TestContext.Current.CancellationToken);
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        await File.WriteAllTextAsync(path, "after", TestContext.Current.CancellationToken);
        GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
        Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_added_file_is_reportedAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        await File.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);
        GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
        Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_deleted_file_is_reportedAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        await File.WriteAllTextAsync(path, "gone-soon", TestContext.Current.CancellationToken);
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        File.Delete(path);
        GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
        Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_symlink_to_a_regular_file_is_keyed_by_the_link_with_the_target_in_the_valueAsync()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "target.txt");
        await File.WriteAllTextAsync(target, "payload", TestContext.Current.CancellationToken);
        string link = Path.Combine(dir, "link.txt");
        _ = File.CreateSymbolicLink(link, target);

        GateTrust.Snapshot direct = await GateTrust.HashAsync([target]);
        GateTrust.Snapshot viaLink = await GateTrust.HashAsync([link]);

        Assert.Equal(direct.Hashes[target], viaLink.Hashes[link]);
        Assert.StartsWith(target + "|", viaLink.Hashes[link], StringComparison.Ordinal);
        Assert.False(viaLink.Hashes.ContainsKey(target));
    }

    [Fact]
    public async Task A_relative_symlink_records_the_resolved_targetAsync()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "target.txt");
        await File.WriteAllTextAsync(target, "payload", TestContext.Current.CancellationToken);
        string link = Path.Combine(dir, "rel.txt");
        _ = File.CreateSymbolicLink(link, "target.txt");

        GateTrust.Snapshot snapshot = await GateTrust.HashAsync([link]);

        Assert.StartsWith(target + "|", snapshot.Hashes[link], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_links_to_the_same_target_stay_two_entriesAsync()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "target.txt");
        await File.WriteAllTextAsync(target, "payload", TestContext.Current.CancellationToken);
        string linkA = Path.Combine(dir, "a.lnk");
        string linkB = Path.Combine(dir, "b.lnk");
        _ = File.CreateSymbolicLink(linkA, target);
        _ = File.CreateSymbolicLink(linkB, target);

        GateTrust.Snapshot snapshot = await GateTrust.HashAsync([linkA, linkB]);

        Assert.Equal(2, snapshot.Hashes.Count);
        Assert.True(snapshot.Hashes.ContainsKey(linkA));
        Assert.True(snapshot.Hashes.ContainsKey(linkB));
    }

    [Fact]
    public async Task Retargeting_a_symlink_is_a_change_under_the_link_pathAsync()
    {
        string dir = NewDir();
        string targetA = Path.Combine(dir, "a.txt");
        string targetB = Path.Combine(dir, "b.txt");
        await File.WriteAllTextAsync(targetA, "aaa", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(targetB, "bbb", TestContext.Current.CancellationToken);
        string link = Path.Combine(dir, "link.txt");
        _ = File.CreateSymbolicLink(link, targetA);

        GateTrust.Snapshot before = await GateTrust.HashAsync([link]);
        File.Delete(link);
        _ = File.CreateSymbolicLink(link, targetB);
        GateTrust.Snapshot after = await GateTrust.HashAsync([link]);

        Assert.StartsWith(targetA + "|", before.Hashes[link], StringComparison.Ordinal);
        Assert.StartsWith(targetB + "|", after.Hashes[link], StringComparison.Ordinal);
        Assert.Equal([link], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Retargeting_a_symlink_to_identical_content_is_still_a_changeAsync()
    {
        string dir = NewDir();
        string targetA = Path.Combine(dir, "a.txt");
        string targetB = Path.Combine(dir, "b.txt");
        await File.WriteAllTextAsync(targetA, "same", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(targetB, "same", TestContext.Current.CancellationToken);
        string link = Path.Combine(dir, "link.txt");
        _ = File.CreateSymbolicLink(link, targetA);

        GateTrust.Snapshot before = await GateTrust.HashAsync([link]);
        File.Delete(link);
        _ = File.CreateSymbolicLink(link, targetB);
        GateTrust.Snapshot after = await GateTrust.HashAsync([link]);

        Assert.Equal([link], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_fifo_hashes_within_the_time_bound_and_records_a_bad_stateAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Unix only");

        string dir = NewDir();
        string fifo = Path.Combine(dir, "fifo");
        ProcessStartInfo psi = new("sh", ["-c", "mkfifo '" + fifo + "'"]) { UseShellExecute = false };
        using Process mk = Process.Start(psi)!;
        await mk.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(new FileInfo(fifo).Exists);

        Task<GateTrust.Snapshot> hashTask = GateTrust.HashAsync([fifo]);
        GateTrust.Snapshot snapshot = await hashTask.WaitAsync(TimeSpan.FromSeconds(20), TimeProvider.System, TestContext.Current.CancellationToken);

        string state = StateOf(snapshot.Hashes[fifo]);
        Assert.True(state is "nonregular" || state.StartsWith("unreadable", StringComparison.Ordinal), snapshot.Hashes[fifo]);
    }

    [Fact]
    public async Task A_file_over_the_byte_cap_is_too_largeAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "big.bin");
        await using (FileStream fs = File.Create(path))
        {
            fs.SetLength((16L * 1024L * 1024L) + 1L);
        }

        GateTrust.Snapshot snapshot = await GateTrust.HashAsync([path]);
        Assert.Equal(path + "|unreadable: too large", snapshot.Hashes[path]);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_file_is_reported_as_changedAsync()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.UserName is "root" || Environment.GetEnvironmentVariable("SUDO_UID") is not null,
            "file permissions do not stop root and Windows has no unix mode");

        string dir = NewDir();
        string path = Path.Combine(dir, "secret.txt");
        await File.WriteAllTextAsync(path, "shh", TestContext.Current.CancellationToken);
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
            Assert.StartsWith(path + "|unreadable", after.Hashes[path], StringComparison.Ordinal);
            Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<GateTrust.Snapshot> HashBoundedAsync(string path) =>
        await GateTrust.HashAsync([path]).WaitAsync(TimeSpan.FromSeconds(20), TimeProvider.System, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_directory_is_nonregularAsync()
    {
        string dir = NewDir();
        GateTrust.Snapshot snapshot = await HashBoundedAsync(dir);
        Assert.Equal(dir + "|nonregular", snapshot.Hashes[dir]);
    }

    [Fact]
    public async Task A_symlink_to_a_directory_is_nonregular_under_the_link_pathAsync()
    {
        string dir = NewDir();
        string link = Path.Combine(NewDir(), "dirlink");
        _ = File.CreateSymbolicLink(link, dir);
        GateTrust.Snapshot snapshot = await HashBoundedAsync(link);
        Assert.Equal(dir + "|nonregular", snapshot.Hashes[link]);
        _ = Assert.Single(snapshot.Hashes);
    }

    [Fact]
    public async Task Dev_null_is_nonregularAsync()
    {
        Assert.SkipUnless(File.Exists("/dev/null"), "no /dev/null on this platform");

        GateTrust.Snapshot snapshot = await HashBoundedAsync("/dev/null");
        Assert.Equal("/dev/null|nonregular", snapshot.Hashes["/dev/null"]);
    }

    [Fact]
    public async Task A_symlink_to_dev_null_is_nonregular_under_the_link_pathAsync()
    {
        Assert.SkipUnless(File.Exists("/dev/null"), "no /dev/null on this platform");

        string link = Path.Combine(NewDir(), "nulllink");
        _ = File.CreateSymbolicLink(link, "/dev/null");
        GateTrust.Snapshot snapshot = await HashBoundedAsync(link);
        Assert.Equal("/dev/null|nonregular", snapshot.Hashes[link]);
        _ = Assert.Single(snapshot.Hashes);
    }

    [Fact]
    public async Task A_dangling_symlink_is_unreadable_under_the_link_path_not_absentAsync()
    {
        string dir = NewDir();
        string link = Path.Combine(dir, "dangling");
        string missing = Path.Combine(dir, "missing");
        _ = File.CreateSymbolicLink(link, missing);

        GateTrust.Snapshot snapshot = await HashBoundedAsync(link);

        Assert.Equal("unreadable: dangling link", snapshot.Hashes[link]);
        Assert.False(snapshot.Hashes.ContainsKey(missing));
        _ = Assert.Single(snapshot.Hashes);
    }

    [Fact]
    public async Task A_dangling_symlink_that_gains_a_target_is_a_changeAsync()
    {
        string dir = NewDir();
        string link = Path.Combine(dir, "dangling");
        string missing = Path.Combine(dir, "missing");
        _ = File.CreateSymbolicLink(link, missing);
        GateTrust.Snapshot before = await HashBoundedAsync(link);
        await File.WriteAllTextAsync(missing, "now here", TestContext.Current.CancellationToken);
        GateTrust.Snapshot after = await HashBoundedAsync(link);

        Assert.Equal([link], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_absent_path_is_a_dash_sentinelAsync()
    {
        string path = Path.Combine(NewDir(), "nothing-here");
        GateTrust.Snapshot snapshot = await HashBoundedAsync(path);
        Assert.Equal("-", snapshot.Hashes[path]);
    }

    [Fact]
    public async Task A_regular_file_hashes_to_its_target_and_sha256Async()
    {
        string path = Path.Combine(NewDir(), "a.txt");
        await File.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);
        GateTrust.Snapshot snapshot = await HashBoundedAsync(path);
        Assert.Equal(path + "|" + Sha256OfHello, snapshot.Hashes[path]);
    }

    [Fact]
    public async Task A_relative_argv_path_resolves_against_the_gate_working_directoryAsync()
    {
        string workDir = NewDir();
        string runsRoot = NewDir();
        string scriptDir = Path.Combine(workDir, "sub");
        Directory.CreateDirectory(scriptDir);
        string script = Path.Combine(scriptDir, "gate.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);

        List<string> paths = GateTrust.CollectPaths(runsRoot, workDir, workDir, ["sh", Path.Combine("sub", "gate.sh")]);

        Assert.Contains(script, paths, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("global.json")]
    [InlineData("dotnet-tools.json")]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("nuget.config")]
    [InlineData("NuGet.Config")]
    [InlineData("NuGet.config")]
    public void Config_files_are_collected_for_every_directory_from_the_working_directory_up_to_the_git_root(string relative)
    {
        string gitRoot = NewDir();
        string middle = Path.Combine(gitRoot, "a");
        string work = Path.Combine(middle, "b");
        Directory.CreateDirectory(work);

        List<string> paths = GateTrust.CollectPaths(NewDir(), gitRoot, work, ["sh"]);

        Assert.Contains(Path.Combine(work, relative), paths, StringComparer.Ordinal);
        Assert.Contains(Path.Combine(middle, relative), paths, StringComparer.Ordinal);
        Assert.Contains(Path.Combine(gitRoot, relative), paths, StringComparer.Ordinal);
        string above = Path.GetDirectoryName(gitRoot)!;
        Assert.DoesNotContain(Path.Combine(above, relative), paths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_argv0_found_on_PATH_is_collected_and_swapping_it_is_a_changeAsync()
    {
        const string name = "lw-trust-path-tool";
        using FakeOnPath onPath = new(name);
        string exe = Launcher.FindOnPath(name)!;
        Assert.NotNull(exe);

        string work = NewDir();
        List<string> paths = GateTrust.CollectPaths(NewDir(), work, work, [name]);
        Assert.Contains(exe, paths, StringComparer.Ordinal);

        GateTrust.Snapshot before = await GateTrust.HashAsync(paths);
        await File.WriteAllTextAsync(exe, "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);
        GateTrust.Snapshot after = await GateTrust.HashAsync(paths);

        Assert.Equal([exe], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public void Literal_gate_trust_entries_are_collected_even_when_absent_and_globs_only_match_existing_files()
    {
        string gitRoot = NewDir();
        string src = Path.Combine(gitRoot, "src", "deep");
        Directory.CreateDirectory(src);
        string props = Path.Combine(src, "x.props");
        string text = Path.Combine(src, "x.txt");
        File.WriteAllText(props, "<Project/>");
        File.WriteAllText(text, "text");

        List<string> paths = GateTrust.CollectPaths(NewDir(), gitRoot, gitRoot, ["sh"], ["Directory.Build.props", "src/**/*.props"]);

        Assert.Contains(Path.Combine(gitRoot, "Directory.Build.props"), paths, StringComparer.Ordinal);
        Assert.Contains(props, paths, StringComparer.Ordinal);
        Assert.DoesNotContain(text, paths, StringComparer.Ordinal);
    }

    [Fact]
    public void An_unlisted_Directory_Build_props_is_not_collected()
    {
        string gitRoot = NewDir();
        File.WriteAllText(Path.Combine(gitRoot, "Directory.Build.props"), "<Project/>");

        List<string> paths = GateTrust.CollectPaths(NewDir(), gitRoot, gitRoot, ["sh"], ["other.props"]);

        Assert.DoesNotContain(Path.Combine(gitRoot, "Directory.Build.props"), paths, StringComparer.Ordinal);
    }

    [Fact]
    public void A_prices_file_outside_the_runs_root_is_collected_and_one_inside_is_not_added_twice()
    {
        string runsRoot = NewDir();
        string outside = Path.Combine(NewDir(), "prices.json");
        string inside = Path.Combine(runsRoot, "own-prices.json");
        File.WriteAllText(outside, "{}");
        File.WriteAllText(inside, "{}");
        string work = NewDir();

        List<string> withOutside = GateTrust.CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: outside);
        List<string> withInside = GateTrust.CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: inside);

        Assert.Contains(outside, withOutside, StringComparer.Ordinal);
        Assert.Equal(1, withInside.Count(p => p == inside));
    }

    [Fact]
    public void A_prices_file_under_the_runs_root_inbox_is_collected_and_one_reached_by_the_walk_appears_once()
    {
        string runsRoot = NewDir();
        string inboxPrices = Path.Combine(runsRoot, "inbox", "t", "prices.json");
        string walkedPrices = Path.Combine(runsRoot, "sub", "prices.json");
        foreach (string file in new[] { inboxPrices, walkedPrices })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");
        }

        string work = NewDir();
        List<string> inInbox = GateTrust.CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: inboxPrices);
        List<string> walked = GateTrust.CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: walkedPrices);

        Assert.Equal(1, inInbox.Count(p => p == inboxPrices));
        Assert.Equal(1, walked.Count(p => p == walkedPrices));
    }

    [Fact]
    public void The_runs_root_is_walked_recursively_except_the_launcher_owned_subtrees()
    {
        string runsRoot = NewDir();
        string[] kept = [Path.Combine(runsRoot, "profiles.json"), Path.Combine(runsRoot, "sub", "deep", "x.json")];
        string[] skipped =
        [
            Path.Combine(runsRoot, "runs.jsonl"),
            Path.Combine(runsRoot, "runs", "20260101-000000-a", "summary.json"),
            Path.Combine(runsRoot, "inbox", "t", "task.md"),
            Path.Combine(runsRoot, "system", "abc.md"),
        ];
        foreach (string file in kept.Concat(skipped))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
        }

        string work = NewDir();
        List<string> paths = GateTrust.CollectPaths(runsRoot, work, work, ["sh"]);

        foreach (string file in kept)
        {
            Assert.Contains(file, paths, StringComparer.Ordinal);
        }

        foreach (string file in skipped)
        {
            Assert.DoesNotContain(file, paths, StringComparer.Ordinal);
        }
    }
}
