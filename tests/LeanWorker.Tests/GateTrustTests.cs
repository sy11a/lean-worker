using System.Diagnostics;
using Xunit;

namespace LeanWorker.Tests;

public class GateTrustTests
{
    private static string NewDir() => Directory.CreateTempSubdirectory("lw-trust").FullName;

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
    public async Task A_symlink_to_a_regular_file_hashes_like_the_file_and_records_the_targetAsync()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "target.txt");
        await File.WriteAllTextAsync(target, "payload", TestContext.Current.CancellationToken);
        string link = Path.Combine(dir, "link.txt");
        _ = File.CreateSymbolicLink(link, target);

        GateTrust.Snapshot direct = await GateTrust.HashAsync([target]);
        GateTrust.Snapshot viaLink = await GateTrust.HashAsync([link]);

        Assert.Equal(direct.Hashes[target], viaLink.Hashes[target]);
        Assert.False(viaLink.Hashes.ContainsKey(link));
    }

    [Fact]
    public async Task Retargeting_a_symlink_is_a_changeAsync()
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

        Assert.Equal([targetA, targetB], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_fifo_hashes_within_the_time_bound_and_records_a_bad_stateAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = NewDir();
        string fifo = Path.Combine(dir, "fifo");
        ProcessStartInfo psi = new("sh", ["-c", "mkfifo '" + fifo + "'"]) { UseShellExecute = false };
        using Process mk = Process.Start(psi)!;
        await mk.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(new FileInfo(fifo).Exists);

        Task<GateTrust.Snapshot> hashTask = GateTrust.HashAsync([fifo]);
        GateTrust.Snapshot snapshot = await hashTask.WaitAsync(TimeSpan.FromSeconds(20), TimeProvider.System, TestContext.Current.CancellationToken);

        string state = snapshot.Hashes[fifo];
        Assert.True(state is "nonregular" || state.StartsWith("unreadable", StringComparison.Ordinal), state);
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
        Assert.Equal("unreadable: too large", snapshot.Hashes[path]);
    }

    [Fact]
    public async Task An_unreadable_file_is_reported_as_changedAsync()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName is "root" || Environment.GetEnvironmentVariable("SUDO_UID") is not null)
        {
            return;
        }

        string dir = NewDir();
        string path = Path.Combine(dir, "secret.txt");
        await File.WriteAllTextAsync(path, "shh", TestContext.Current.CancellationToken);
        GateTrust.Snapshot before = await GateTrust.HashAsync([path]);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            GateTrust.Snapshot after = await GateTrust.HashAsync([path]);
            Assert.StartsWith("unreadable", after.Hashes[path], StringComparison.Ordinal);
            Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
}
