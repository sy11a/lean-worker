// The git metadata a run shares with the repository it works in: the config and the hooks of the
// common git dir, and the worktree's own config.worktree. A worker can change them (core.fsmonitor,
// core.hooksPath, a hook of its own) and every git call after that — the launcher's own and the
// operator's next session's — runs what it wrote. The write-scope snapshot is git-status based and
// never sees them, so they are watched here: hashed before the worker and after it.

using System.Security.Cryptography;

namespace LeanWorker;

/// <summary>
/// The hashes of the git metadata of a working tree: the common git dir's config, every file under
/// its hooks (by name, so an added or a removed hook counts) and the worktree's own config.worktree.
/// The directories are the ones git itself resolves, never a string-built ".git".
/// </summary>
internal static class GitMetadata
{
    /// <summary>
    /// The hashes of the watched files: the label of every file → its hash. Null when there is
    /// nothing to watch: the tree is not a git tree, or git is not there.
    /// </summary>
    public static async Task<Dictionary<string, string>?> TakeAsync(string dir, string labelBase)
    {
        Git.Result? common = await Git.RunResultAsync(dir, "rev-parse", "--git-common-dir").ConfigureAwait(false);
        if (common is null || common.ExitCode is not 0)
        {
            return null;
        }

        string commonDir = Resolve(dir, common.Stdout);
        Git.Result? own = await Git.RunResultAsync(dir, "rev-parse", "--git-dir").ConfigureAwait(false);
        string? ownDir = own is { ExitCode: 0 } ? Resolve(dir, own.Stdout) : null;

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        string config = Path.Combine(commonDir, "config");
        if (File.Exists(config))
        {
            files[Label(config, labelBase)] = await HashAsync(config).ConfigureAwait(false);
        }

        string hooks = Path.Combine(commonDir, "hooks");
        if (Directory.Exists(hooks))
        {
            foreach (string file in Directory.EnumerateFiles(hooks, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                files[Label(file, labelBase)] = await HashAsync(file).ConfigureAwait(false);
            }
        }

        // The worktree's own config: it lives in the per-worktree git dir and only exists when the
        // repository works with per-worktree configuration.
        if (ownDir is not null)
        {
            string worktreeConfig = Path.Combine(ownDir, "config.worktree");
            if (File.Exists(worktreeConfig))
            {
                files[Label(worktreeConfig, labelBase)] = await HashAsync(worktreeConfig).ConfigureAwait(false);
            }
        }

        return files;
    }

    /// <summary>
    /// The directory git printed, resolved against the directory the call ran in: git prints it
    /// relative to that directory (".git" for the tree itself) or absolute.
    /// </summary>
    private static string Resolve(string dir, string printed)
    {
        string path = printed.Trim();
        try
        {
            return Path.GetFullPath(Path.Combine(dir, path));
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// The label of a watched file: its path relative to the repository that owns the git dir
    /// (".git/config"), so the report says which file it is and not where the repository lives. A
    /// file outside that repository keeps its own path.
    /// </summary>
    private static string Label(string path, string labelBase)
    {
        if (labelBase.Length is 0)
        {
            return path;
        }

        try
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(labelBase), Path.GetFullPath(path)).Replace('\\', '/');
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// The hash of a watched file: its content, or the target of the link it is (a hook that is a
    /// link runs what it points at). A file that cannot be read is recorded by the marker "?", so
    /// it differs from every hash and from a file that is not there at all.
    /// </summary>
    private static async Task<string> HashAsync(string path)
    {
        try
        {
            string? link = new FileInfo(path).LinkTarget;
            if (link is not null)
            {
                return Convert.ToHexString(SHA256.HashData(Json.Utf8.GetBytes("link:" + link)));
            }

            return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }
}
