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

    // The paths whose trust state differs between two snapshots, the way the chain checks them: the
    // path sets first, then the first hashed entry that differs.
    private static async Task<List<string>> ChangedAsync(GateTrust.Snapshot before, GateTrust.Snapshot after)
    {
        List<string> paths = [.. after.Hashes.Keys];
        List<string> setDiff = GateTrust.SetDifferences(before, paths);
        if (setDiff.Count > 0)
        {
            return setDiff;
        }

        GateTrust.TrustDiff? diff = await GateTrust.FirstDiffAsync(before, paths);
        return diff is null ? [] : [diff.Path];
    }

    // The fixed chain paths plus the volatile walks: the whole trust set as one list.
    private static List<string> CollectPaths(string runsRoot, string gitRoot, string work, IReadOnlyList<string> command,
        IReadOnlyList<string>? extraTrust = null, string? extraPricesFile = null)
    {
        List<string> paths = GateTrust.CollectChainPaths(runsRoot, gitRoot, work, command, extraTrust, extraPricesFile);
        foreach (string p in GateTrust.CollectVolatilePaths(runsRoot, work, gitRoot))
        {
            if (!paths.Contains(p, StringComparer.Ordinal))
            {
                paths.Add(p);
            }
        }

        return paths;
    }

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
        Assert.Empty(await ChangedAsync(before, after));
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
        Assert.Equal([path], await ChangedAsync(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_added_file_is_reportedAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        await File.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);
        GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
        Assert.Equal([path], await ChangedAsync(before, after), StringComparer.Ordinal);
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
        Assert.Equal([path], await ChangedAsync(before, after), StringComparer.Ordinal);
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
        Assert.Equal([link], await ChangedAsync(before, after), StringComparer.Ordinal);
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

        Assert.Equal([link], await ChangedAsync(before, after), StringComparer.Ordinal);
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
        GateTrust.Snapshot snapshot = await hashTask.WaitAsync(TimeSpan.FromSeconds(45), TimeProvider.System, TestContext.Current.CancellationToken);

        string state = StateOf(snapshot.Hashes[fifo]);
        Assert.True(state is "nonregular" || state.StartsWith("unreadable", StringComparison.Ordinal), snapshot.Hashes[fifo]);
    }

    [Fact]
    public async Task A_trusted_file_over_16_MiB_hashes_to_a_sha_not_a_sentinelAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "big.bin");
        await using (FileStream fs = File.Create(path))
        {
            fs.SetLength(20L * 1024L * 1024L);
        }

        GateTrust.Snapshot snapshot = await GateTrust.HashAsync([path]);
        string state = StateOf(snapshot.Hashes[path]);
        Assert.Matches("^[0-9a-f]{64}$", state);
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
            Assert.Equal([path], await ChangedAsync(before, after), StringComparer.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<GateTrust.Snapshot> HashBoundedAsync(string path) =>
        await GateTrust.HashAsync([path]).WaitAsync(TimeSpan.FromSeconds(45), TimeProvider.System, TestContext.Current.CancellationToken);

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

        Assert.Equal([link], await ChangedAsync(before, after), StringComparer.Ordinal);
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

        List<string> paths = CollectPaths(runsRoot, workDir, workDir, ["sh", Path.Combine("sub", "gate.sh")]);

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

        List<string> paths = CollectPaths(NewDir(), gitRoot, work, ["sh"]);

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
        List<string> paths = CollectPaths(NewDir(), work, work, [name]);
        Assert.Contains(exe, paths, StringComparer.Ordinal);

        GateTrust.Snapshot before = await GateTrust.HashAsync(paths);
        await File.WriteAllTextAsync(exe, "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);
        GateTrust.Snapshot after = await GateTrust.HashAsync(paths);

        Assert.Equal([exe], await ChangedAsync(before, after), StringComparer.Ordinal);
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

        List<string> paths = CollectPaths(NewDir(), gitRoot, gitRoot, ["sh"], ["Directory.Build.props", "src/**/*.props"]);

        Assert.Contains(Path.Combine(gitRoot, "Directory.Build.props"), paths, StringComparer.Ordinal);
        Assert.Contains(props, paths, StringComparer.Ordinal);
        Assert.DoesNotContain(text, paths, StringComparer.Ordinal);
    }

    [Fact]
    public void An_unlisted_Directory_Build_props_is_not_collected()
    {
        string gitRoot = NewDir();
        File.WriteAllText(Path.Combine(gitRoot, "Directory.Build.props"), "<Project/>");

        List<string> paths = CollectPaths(NewDir(), gitRoot, gitRoot, ["sh"], ["other.props"]);

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

        List<string> withOutside = CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: outside);
        List<string> withInside = CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: inside);

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
        List<string> inInbox = CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: inboxPrices);
        List<string> walked = CollectPaths(runsRoot, work, work, ["sh"], extraPricesFile: walkedPrices);

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
        List<string> paths = CollectPaths(runsRoot, work, work, ["sh"]);

        foreach (string file in kept)
        {
            Assert.Contains(file, paths, StringComparer.Ordinal);
        }

        foreach (string file in skipped)
        {
            Assert.DoesNotContain(file, paths, StringComparer.Ordinal);
        }
    }

    // ---- Canonical: realpath(3) semantics ------------------------------------------------------------------

    // A fresh directory under its own canonical name, so expectations hold wherever the temp root lives.
    private string NewCanonicalDir() => GateTrust.Canonical(NewDir()).Path;

    [Fact]
    public void Canonical_of_a_plain_existing_path_is_the_path_itself_with_no_links()
    {
        string dir = NewCanonicalDir();
        string sub = Path.Combine(dir, "a", "b");
        _ = Directory.CreateDirectory(sub);

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(sub);

        Assert.True(canonical.Resolved);
        Assert.Equal(sub, canonical.Path);
        Assert.Empty(canonical.Links);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Canonical_resolves_a_symlinked_parent_component()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string real = Path.Combine(dir, "real");
        _ = Directory.CreateDirectory(Path.Combine(real, "bin"));
        string link = Path.Combine(dir, "link");
        _ = Directory.CreateSymbolicLink(link, real);

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(Path.Combine(link, "bin"));

        Assert.True(canonical.Resolved);
        Assert.Equal(Path.Combine(real, "bin"), canonical.Path);
        Assert.Equal([link], canonical.Links);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Canonical_follows_a_chain_of_two_links_and_lists_both_in_order()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string real = Path.Combine(dir, "real");
        _ = Directory.CreateDirectory(real);
        string first = Path.Combine(dir, "first");
        string second = Path.Combine(dir, "second");
        _ = Directory.CreateSymbolicLink(first, "real");
        _ = Directory.CreateSymbolicLink(second, "first");

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(second);

        Assert.True(canonical.Resolved);
        Assert.Equal(real, canonical.Path);
        Assert.Equal([second, first], canonical.Links);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Canonical_resolves_dot_dot_after_a_link_against_the_link_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string deep = Path.Combine(dir, "real", "deep");
        _ = Directory.CreateDirectory(deep);
        string link = Path.Combine(dir, "link");
        _ = Directory.CreateSymbolicLink(link, deep);

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(link + "/../x");

        Assert.True(canonical.Resolved);
        Assert.Equal(Path.Combine(dir, "real", "x"), canonical.Path);
        Assert.False(string.Equals(Path.Combine(dir, "x"), canonical.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Canonical_appends_a_non_existent_tail_lexically_normalised()
    {
        string dir = NewCanonicalDir();

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(dir + "/missing/./sub//x");

        Assert.True(canonical.Resolved);
        Assert.Equal(Path.Combine(dir, "missing", "sub", "x"), canonical.Path);
        Assert.Empty(canonical.Links);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Canonical_resolves_the_existing_prefix_of_a_path_with_a_non_existent_tail()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string real = Path.Combine(dir, "real");
        _ = Directory.CreateDirectory(real);
        string link = Path.Combine(dir, "link");
        _ = Directory.CreateSymbolicLink(link, real);

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(Path.Combine(link, "not", "there"));

        Assert.Equal(Path.Combine(real, "not", "there"), canonical.Path);
        Assert.Equal([link], canonical.Links);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [UnsupportedOSPlatform("windows")]
    public async Task Canonical_of_a_link_loop_fails_bounded_instead_of_hangingAsync(bool selfLink)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string a = Path.Combine(dir, "a");
        string b = Path.Combine(dir, "b");
        _ = Directory.CreateSymbolicLink(a, selfLink ? "a" : "b");
        if (!selfLink)
        {
            _ = Directory.CreateSymbolicLink(b, "a");
        }

        GateTrust.CanonicalPath canonical = await Task.Run(() => GateTrust.Canonical(Path.Combine(a, "tail")), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.False(canonical.Resolved);
        Assert.Equal("unreadable: too many links", canonical.Unreadable);
        Assert.NotEmpty(canonical.Links);
        Assert.All(canonical.Links, l => Assert.True(l == a || l == b));
    }

    // What the OS resolves: realpath(1) through sh; null when the OS itself fails (a loop, a missing path).
    private static async Task<string?> OsRealpathAsync(string path)
    {
        ProcessStartInfo info = new("sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("realpath \"$1\"");
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add(path);
        using Process process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode is 0 ? output.TrimEnd('\n') : null;
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Canonical_resolves_a_relative_dot_dot_link_target_physically_inside_a_symlinked_parentAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string realB = Path.Combine(dir, "real", "b");
        _ = Directory.CreateDirectory(realB);
        _ = Directory.CreateDirectory(Path.Combine(dir, "real", "x"));
        _ = Directory.CreateDirectory(Path.Combine(dir, "x"));
        _ = Directory.CreateSymbolicLink(Path.Combine(dir, "a"), realB);
        _ = Directory.CreateSymbolicLink(Path.Combine(realB, "link"), "../x");
        string input = Path.Combine(dir, "a", "link");

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(input);

        Assert.True(canonical.Resolved);
        Assert.Equal(Path.Combine(dir, "real", "x"), canonical.Path);
        Assert.False(string.Equals(Path.Combine(dir, "x"), canonical.Path, StringComparison.Ordinal));
        Assert.Equal(await OsRealpathAsync(input), canonical.Path);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Canonical_restarts_from_the_root_for_a_rooted_absolute_link_targetAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string target = Path.Combine(dir, "real", "deep");
        _ = Directory.CreateDirectory(target);
        string sub = Path.Combine(dir, "sub", "inner");
        _ = Directory.CreateDirectory(sub);
        string link = Path.Combine(sub, "abs");
        _ = Directory.CreateSymbolicLink(link, target);
        string input = Path.Combine(link, "..", "tail");

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(input);

        Assert.True(canonical.Resolved);
        Assert.Equal(Path.Combine(dir, "real", "tail"), canonical.Path);
        Assert.Equal([link], canonical.Links);
        Assert.Equal(Path.Combine(dir, "real"), await OsRealpathAsync(Path.GetDirectoryName(canonical.Path)!));
        Assert.Equal(await OsRealpathAsync(Path.Combine(link, "..")), Path.GetDirectoryName(canonical.Path));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Canonical_follows_a_chain_mixing_a_relative_and_an_absolute_targetAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string final = Path.Combine(dir, "real", "final");
        _ = Directory.CreateDirectory(final);
        string hop = Path.Combine(dir, "hop");
        _ = Directory.CreateDirectory(Path.Combine(hop, "in"));
        string absLink = Path.Combine(dir, "abs");
        string relLink = Path.Combine(hop, "in", "rel");
        string top = Path.Combine(dir, "top");
        _ = Directory.CreateSymbolicLink(absLink, final);
        _ = Directory.CreateSymbolicLink(relLink, "../../abs");
        _ = Directory.CreateSymbolicLink(top, "hop/in/rel");
        string input = Path.Combine(top, "file.txt");
        await File.WriteAllTextAsync(Path.Combine(final, "file.txt"), "x", TestContext.Current.CancellationToken);

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(input);

        Assert.True(canonical.Resolved);
        Assert.Equal(Path.Combine(final, "file.txt"), canonical.Path);
        Assert.Equal([top, relLink, absLink], canonical.Links);
        Assert.Equal(await OsRealpathAsync(input), canonical.Path);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Canonical_of_a_relative_and_absolute_mixed_loop_is_unreadable_where_the_os_failsAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need a Unix filesystem");

        string dir = NewCanonicalDir();
        string a = Path.Combine(dir, "a");
        string b = Path.Combine(dir, "b");
        _ = Directory.CreateSymbolicLink(a, b);
        _ = Directory.CreateSymbolicLink(b, "./a");

        GateTrust.CanonicalPath canonical = await Task.Run(() => GateTrust.Canonical(a), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.False(canonical.Resolved);
        Assert.Null(await OsRealpathAsync(a));
    }
}
