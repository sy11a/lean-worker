using System.Text;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Launcher.WritePlainAsync / CreateWriter write through a temp file and a rename, so a symlink a worker
/// planted at the destination is replaced and its target is left alone.
/// </summary>
public sealed class PlainWriteTests : IDisposable
{
    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private string NewDir() => _dirs.Create("lw-plainwrite");

    private static string[] Names(string dir) =>
        [.. Directory.GetFileSystemEntries(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task WritePlainAsync_creates_and_overwrites_a_regular_file_and_leaves_no_tempAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "summary.json");

        await Launcher.WritePlainAsync(path, "first");
        await Launcher.WritePlainAsync(path, "second");

        Assert.Equal("second", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["summary.json"], Names(dir));
    }

    [Fact]
    public async Task WritePlainAsync_replaces_a_planted_symlink_and_leaves_its_target_untouchedAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        string dir = NewDir();
        string victim = Path.Combine(NewDir(), "victim.txt");
        await File.WriteAllTextAsync(victim, "untouched", TestContext.Current.CancellationToken);
        string path = Path.Combine(dir, "report.md");
        _ = File.CreateSymbolicLink(path, victim);

        await Launcher.WritePlainAsync(path, "launcher content");

        Assert.Equal("untouched", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("launcher content", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["report.md"], Names(dir));
    }

    [Fact]
    public async Task WritePlainAsync_replaces_a_dangling_symlink_tooAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        string dir = NewDir();
        string missing = Path.Combine(NewDir(), "not-created.txt");
        string path = Path.Combine(dir, "gate.json");
        _ = File.CreateSymbolicLink(path, missing);

        await Launcher.WritePlainAsync(path, "{}");

        Assert.False(File.Exists(missing));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("{}", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WritePlainAsync_failure_deletes_its_tempAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "taken");
        Directory.CreateDirectory(path);

        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await Launcher.WritePlainAsync(path, "content"));

        Assert.Equal(["taken"], Names(dir));
    }

    [Fact]
    public async Task CreateWriter_writes_to_the_path_and_leaves_no_tempAsync()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "gate.log");

        StreamWriter writer = Launcher.CreateWriter(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync("line one");
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("line one" + Environment.NewLine, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["gate.log"], Names(dir));
    }

    [Fact]
    public async Task CreateWriter_replaces_a_planted_symlink_and_leaves_its_target_untouchedAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        string dir = NewDir();
        string victim = Path.Combine(NewDir(), "victim.txt");
        await File.WriteAllTextAsync(victim, "untouched", TestContext.Current.CancellationToken);
        string path = Path.Combine(dir, "gate.log");
        _ = File.CreateSymbolicLink(path, victim);

        StreamWriter writer = Launcher.CreateWriter(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteLineAsync("launcher line");
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("untouched", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("launcher line" + Environment.NewLine, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["gate.log"], Names(dir));
    }

    [Fact]
    public void CreateWriter_failure_deletes_its_temp()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "taken");
        Directory.CreateDirectory(path);

        _ = Assert.ThrowsAny<Exception>(() => Launcher.CreateWriter(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));

        Assert.Equal(["taken"], Names(dir));
    }
}
