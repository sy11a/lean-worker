// The files of a directory tree: the hooks of a repository and the setup the launcher copies into
// a worktree are found the same way — every file of a named directory of a base directory, in a
// stable order, so a file that was added or removed shows up.

namespace LeanWorker;

/// <summary>
/// The files of a directory tree: the files of a named directory of a base directory and of the
/// folders below it.
/// </summary>
internal static class FileTree
{
    /// <summary>
    /// The files under a directory of a base directory: the files of the subdirectory and of every
    /// folder below it, in ordinal order. Nothing when the subdirectory is not there.
    /// </summary>
    internal static List<string> FilesUnder(string baseDir, string name)
    {
        string dir = Path.Combine(baseDir, name);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
    }
}
