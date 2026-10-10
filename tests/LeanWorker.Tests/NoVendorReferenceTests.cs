using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// R-083: lean-worker must not know any specific gate tool. No source file under the launcher project may
/// mention the vendor name, in code, comments or the project file.
/// </summary>
public class NoVendorReferenceTests
{
    private static string FindLauncherDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "skills")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("could not find the repo root above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "skills", "lean-worker", "launcher");
    }

    [Fact]
    public void No_source_file_mentions_the_vendor_name()
    {
        string launcherDir = FindLauncherDir();
        List<string> files = [
            .. Directory.EnumerateFiles(launcherDir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
            .. Directory.EnumerateFiles(launcherDir, "*.csproj", SearchOption.AllDirectories),
        ];

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.False(text.Contains("Sy11a", StringComparison.OrdinalIgnoreCase), $"{file} mentions the vendor name");
        }
    }
}
