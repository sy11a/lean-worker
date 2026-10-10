namespace LeanWorker.Tests;

/// <summary>
/// Creates temp directories and deletes them (best effort) on dispose, so a test class that implements
/// IDisposable and owns one of these leaves nothing behind even when a test fails or is skipped.
/// </summary>
internal sealed class TempDirs : IDisposable
{
    private readonly List<string> _dirs = [];

    public string Create(string prefix)
    {
        string dir = Directory.CreateTempSubdirectory(prefix).FullName;
        Track(dir);
        return dir;
    }

    public string NewRoot()
    {
        string root = RunAsyncGolden.NewRoot();
        Track(root);
        return root;
    }

    public void Track(string dir) => _dirs.Add(dir);

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory must not fail the test that already finished.
            }
        }
    }
}
