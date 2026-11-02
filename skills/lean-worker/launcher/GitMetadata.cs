// The git metadata a run shares with the repository it works in: the config and the hooks of the
// common git dir, and the worktree's own config. A worker can change them (core.fsmonitor,
// core.hooksPath, a hook of its own) and every git call after that — the launcher's own and the
// operator's next session's — runs what it wrote. The write-scope snapshot is git-status based and
// never sees them, so they are watched here: hashed before the worker and after it.
//
// The two passes watch the same paths on purpose. The git dirs are resolved once, before the
// worker runs (git itself resolves them), and the after pass hashes exactly those paths and runs
// no git at all: a worker that redirected the worktree to a copy of the git dir it made (the
// commondir, the gitdir file) cannot make the after pass hash the copy — the redirect is a watched
// file itself, and the copy is never looked at.

namespace LeanWorker;

/// <summary>
/// The hashes of the git metadata of a working tree: the common git dir's config, every file under
/// its hooks (by name, so an added or a removed hook counts), the attributes file, the per-worktree
/// config and the files that redirect a worktree to the git dir it belongs to. The directories are
/// the ones git itself resolved before the worker ran, never a string-built ".git".
/// </summary>
internal static class GitMetadata
{
    /// <summary>
    /// The git dirs of a working tree, as git resolved them: the common git dir (the repository's
    /// own ".git") and the working tree's own git dir (the same one, or the per-worktree one under
    /// "worktrees/"). The own git dir is null when git did not resolve one.
    /// </summary>
    internal sealed record Dirs(string Common, string? Own);

    /// <summary>
    /// The git dirs of a working tree, resolved by git itself: the common git dir and the working
    /// tree's own git dir, both absolute. Null when git cannot be run or the directory is not a git
    /// working tree. Resolved once, before the worker runs: after the worker, git would resolve the
    /// dirs the worker rewrote, and the after pass would hash the copy the worker made.
    /// </summary>
    public static async Task<Dirs?> ResolveAsync(string dir)
    {
        Git.Result? common = await Git.RunResultAsync(dir, "rev-parse", "--git-common-dir").ConfigureAwait(false);
        if (common is null || common.ExitCode is not 0)
        {
            return null;
        }

        Git.Result? own = await Git.RunResultAsync(dir, "rev-parse", "--git-dir").ConfigureAwait(false);
        string? ownDir = own is { ExitCode: 0 } ? Resolve(dir, own.Stdout) : null;
        return new Dirs(Resolve(dir, common.Stdout), ownDir);
    }

    /// <summary>
    /// The hashes of the watched files: the label of every file → its hash. No git call at all:
    /// the directories are the ones git resolved before the worker ran. Null when the pass could
    /// not be made at all (an I/O error reading the tree).
    /// </summary>
    public static async Task<Dictionary<string, string>?> HashAsync(Dirs dirs, string labelBase)
    {
        try
        {
            return await HashFilesAsync(dirs, labelBase).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The watched files of the two git dirs: the config, the attributes file and the per-worktree
    /// config of the common git dir; the per-worktree config and the two files that redirect a
    /// worktree to the git dir it belongs to in the working tree's own git dir; every file under
    /// the hooks of the common git dir.
    /// </summary>
    private static async Task<Dictionary<string, string>> HashFilesAsync(Dirs dirs, string labelBase)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        string common = dirs.Common;
        await AddFileAsync(files, Path.Combine(common, "config"), labelBase).ConfigureAwait(false);
        // The attributes file of the common git dir: it names the filters a checkout runs.
        await AddFileAsync(files, Path.Combine(common, "info", "attributes"), labelBase).ConfigureAwait(false);
        // The per-worktree config of the common git dir (the main worktree's own config).
        await AddFileAsync(files, Path.Combine(common, "config.worktree"), labelBase).ConfigureAwait(false);
        if (dirs.Own is { } own)
        {
            // The per-worktree config of the working tree's own git dir.
            await AddFileAsync(files, Path.Combine(own, "config.worktree"), labelBase).ConfigureAwait(false);
            // The two files that redirect a worktree to the git dir it belongs to: a worker that
            // rewrote them pointed the worktree at a copy of its own, and that rewrite is the
            // finding — the copy is never hashed.
            await AddFileAsync(files, Path.Combine(own, "commondir"), labelBase).ConfigureAwait(false);
            await AddFileAsync(files, Path.Combine(own, "gitdir"), labelBase).ConfigureAwait(false);
        }

        await AddHooksAsync(files, Path.Combine(common, "hooks"), labelBase).ConfigureAwait(false);
        return files;
    }

    /// <summary>
    /// The hash of a watched file, added to the map under its label. A file that is not there is
    /// not watched at all (it was not there before the worker either).
    /// </summary>
    private static async Task AddFileAsync(Dictionary<string, string> files, string path, string labelBase)
    {
        if (!File.Exists(path) && LinkTarget(path) is null)
        {
            return;
        }

        files[Label(path, labelBase)] = await HashFileAsync(path).ConfigureAwait(false);
    }

    /// <summary>
    /// The hashes of the hooks: every file under the hooks directory, by name, so an added or a
    /// removed hook counts. A hooks directory that is a link is hashed as the link it is: following
    /// it would hash the files of a directory the worker made, and the link itself is the finding.
    /// </summary>
    private static async Task AddHooksAsync(Dictionary<string, string> files, string hooksDir, string labelBase)
    {
        string? link = LinkTarget(hooksDir);
        if (link is not null)
        {
            files[Label(hooksDir, labelBase)] = FileHash.OfLink(link);
            return;
        }

        foreach (string file in FilesUnder(hooksDir))
        {
            files[Label(file, labelBase)] = await HashFileAsync(file).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The hash of a watched file: its content with its mode (an exec bit added to a hook counts),
    /// or the target of the link it is (a hook that is a link runs what it points at). A file that
    /// cannot be read is recorded by the marker of <see cref="FileHash"/>, so it differs from
    /// every hash and from a file that is not there at all.
    /// </summary>
    private static async Task<string> HashFileAsync(string path)
    {
        string? link = LinkTarget(path);
        if (link is not null)
        {
            return FileHash.OfLink(link);
        }

        return await FileHash.OfAsync(path, withMode: true).ConfigureAwait(false);
    }

    /// <summary>
    /// The files under a directory: every file of it and of every folder below it, in ordinal
    /// order. A link is never followed: it is listed as the link it is, and the launcher hashes the
    /// link, not the files behind it. A directory the launcher cannot read is listed as itself, so
    /// its hash says that it could not be read.
    /// </summary>
    private static List<string> FilesUnder(string dir)
    {
        List<string> files = [];
        Collect(dir, files, 0);
        return files;
    }

    /// <summary>
    /// The files of a directory and of the folders below it. The depth is bounded: a directory
    /// that holds itself is not walked forever.
    /// </summary>
    private static void Collect(string dir, List<string> files, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files.Add(dir);
            return;
        }

        foreach (string entry in entries.Order(StringComparer.Ordinal))
        {
            if (LinkTarget(entry) is not null)
            {
                files.Add(entry);
                continue;
            }

            if (Directory.Exists(entry))
            {
                Collect(entry, files, depth + 1);
                continue;
            }

            files.Add(entry);
        }
    }

    /// <summary>
    /// The target of a link, or null when the path is not a link.
    /// </summary>
    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
}
