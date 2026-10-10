using Xunit;

namespace LeanWorker.Tests;

public class GateTrustTests
{
    private static string NewDir() => Directory.CreateTempSubdirectory("lw-trust").FullName;

    [Fact]
    public void Unchanged_files_report_no_difference()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, "same");
        GateTrust.Snapshot before = GateTrust.Hash([path]);
        GateTrust.Snapshot after = GateTrust.Hash([path]);
        Assert.Empty(GateTrust.Changed(before, after));
    }

    [Fact]
    public void A_changed_file_is_reported()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, "before");
        GateTrust.Snapshot before = GateTrust.Hash([path]);
        File.WriteAllText(path, "after");
        GateTrust.Snapshot after = GateTrust.Hash([path]);
        Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public void An_added_file_is_reported()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        GateTrust.Snapshot before = GateTrust.Hash([path]);
        File.WriteAllText(path, "new");
        GateTrust.Snapshot after = GateTrust.Hash([path]);
        Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }

    [Fact]
    public void A_deleted_file_is_reported()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, "gone-soon");
        GateTrust.Snapshot before = GateTrust.Hash([path]);
        File.Delete(path);
        GateTrust.Snapshot after = GateTrust.Hash([path]);
        Assert.Equal([path], GateTrust.Changed(before, after), StringComparer.Ordinal);
    }
}
