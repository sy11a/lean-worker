using System.Text;
using System.Text.RegularExpressions;

namespace LeanWorker;

/// <summary>
/// Which files a run changed in its git working tree, and which of them fall outside the task's write scope.
/// A file counts as changed when its state (content hash, or deleted) differs between the snapshots taken
/// before and after the worker, so edits to files that were already dirty are caught too. A file the launcher
/// cannot read is recorded by its metadata instead, and a file that stays unreadable is reported as
/// unverified rather than counted as unchanged.
/// </summary>
internal static class WriteScope
{
    /// <summary>
    /// The dirty files of a working tree: path relative to the repository root → content hash. A
    /// snapshot of the repository's own git metadata carries the git dirs it was taken over as well
    /// (<see cref="GitDirs"/>): the after pass hashes exactly those dirs and runs no git at all.
    /// </summary>
    internal sealed record Snapshot(string Root, Dictionary<string, string> Dirty, GitMetadata.Dirs? GitDirs = null)
    {
        /// <summary>
        /// The pin of the git calls that read this tree: its own git dir, the tree itself and the
        /// common git dir, all as they were when the snapshot was taken. Null when the snapshot
        /// carries no git dir (the tree was not a git tree, or git could not resolve one).
        /// </summary>
        public Git.Pin? Pin() => GitDirs is { } dirs && dirs.Own is { } own ? new Git.Pin(own, Root, dirs.Common) : null;
    }

    /// <summary>
    /// Null when <paramref name="cwd"/> is not inside a git working tree (or git is missing). The
    /// excluded paths (the launcher's own bookkeeping) leave both snapshots of a comparison when
    /// they lie in the tree; an exclude outside it is ignored. The snapshot carries the git dirs
    /// of the tree as well (<see cref="Snapshot.GitDirs"/>): they are resolved now, before the
    /// worker runs, and the after pass is pinned to them.
    /// </summary>
    public static async Task<Snapshot?> TakeAsync(string cwd, params string?[] excludes)
    {
        string? root = (await Git.RunAsync(cwd, "rev-parse", "--show-toplevel").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        GitMetadata.Dirs? dirs = await GitMetadata.ResolveAsync(root).ConfigureAwait(false);
        string? status = await Git.RunAsync(root, "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        return new Snapshot(root, await DirtyAsync(root, status, excludes).ConfigureAwait(false), dirs);
    }

    /// <summary>
    /// The same snapshot, taken again after the worker ran: the call is pinned to the git dir and
    /// the work tree the before pass resolved, so it reads the repository the launcher started in
    /// and not the one a worker pointed it at (a ".git" file it rewrote, a git dir of its own with
    /// a clean filter in its config). The root is the one the before pass found: it is not looked
    /// up again, because a lookup would follow what the worker wrote.
    /// Null when the before snapshot carries no git dir to pin the call to.
    /// </summary>
    public static async Task<Snapshot?> TakeAsync(Snapshot before, params string?[] excludes)
    {
        Git.Pin? pin = before.Pin();
        if (pin is null)
        {
            return null;
        }

        string? status = await Git.RunPinnedAsync(pin,
            "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        return new Snapshot(before.Root, await DirtyAsync(before.Root, status, excludes).ConfigureAwait(false), before.GitDirs);
    }

    /// <summary>
    /// The dirty files of a status: path relative to the repository root → content hash. A file
    /// that is not there any more is recorded as deleted.
    /// </summary>
    private static async Task<Dictionary<string, string>> DirtyAsync(string root, string status, string?[] excludes)
    {
        string[] skips = [.. excludes.Where(e => e is not null)
            .Select(e => Relative(root, e!))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal),];
        Dictionary<string, string> dirty = new(StringComparer.Ordinal);
        string[] entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            string e = entries[i];
            if (e.Length < 4)
            {
                continue;
            }
            // A rename or copy is followed by its source path; a renamed source no longer exists, so it reads as deleted.
            string? source = e[0] is 'R' or 'C' && i + 1 < entries.Length ? entries[++i] : null;
            IEnumerable<string> paths = e[0] == 'R' && source is not null ? [e[3..], source] : new[] { e[3..] };
            foreach (string path in paths)
            {
                if (skips.Any(skip => path == skip || path.StartsWith(skip + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                string full = Path.Combine(root, path);
                dirty[path] = File.Exists(full) ? await FileHash.OfAsync(full).ConfigureAwait(false) : "-";
            }
        }

        return dirty;
    }

    /// <summary>
    /// The snapshot of the git metadata of a working tree: the git dirs git resolves and the hashes
    /// of the watched files (the config, the hooks, the attributes file, the per-worktree config
    /// and the files that redirect a worktree to its git dir). The write scope never sees them — it
    /// is git-status based — so they are watched on their own (issue #114). Null when the tree is
    /// not a git tree (or git is missing).
    /// </summary>
    public static async Task<Snapshot?> TakeGitMetadataAsync(string dir, string labelBase)
    {
        GitMetadata.Dirs? dirs = await GitMetadata.ResolveAsync(dir).ConfigureAwait(false);
        return dirs is null ? null : await TakeGitMetadataAsync(dirs, dir, labelBase).ConfigureAwait(false);
    }

    /// <summary>
    /// The same snapshot, taken again over the git dirs the before pass resolved: no git call at
    /// all, so a worker that rewrote the git dirs (the commondir, the gitdir file) cannot make the
    /// after pass hash the copy it made — the redirect itself is a watched file.
    /// </summary>
    public static async Task<Snapshot?> TakeGitMetadataAsync(Snapshot before, string labelBase)
    {
        GitMetadata.Dirs? dirs = before.GitDirs;
        return dirs is null ? null : await TakeGitMetadataAsync(dirs, before.Root, labelBase).ConfigureAwait(false);
    }

    /// <summary>
    /// The hashes of the watched files of the two git dirs. The root is the top level of the
    /// working tree: its ".git" is watched as well.
    /// </summary>
    private static async Task<Snapshot?> TakeGitMetadataAsync(GitMetadata.Dirs dirs, string root, string labelBase)
    {
        Dictionary<string, string>? files = await GitMetadata.HashAsync(dirs, labelBase, root).ConfigureAwait(false);
        return files is null ? null : new Snapshot(root, files, dirs);
    }

    /// <summary>
    /// The watched files that were readable before the run and cannot be read after it: the
    /// launcher cannot tell whether the run changed them.
    /// </summary>
    public static List<string> UnreadableNow(Snapshot before, Snapshot after)
    {
        return [.. after.Dirty.Keys.Intersect(before.Dirty.Keys, StringComparer.Ordinal)
            .Where(p => !FileHash.IsUnreadable(before.Dirty[p]) && FileHash.IsUnreadable(after.Dirty[p]))
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// The paths both snapshots saw as unreadable with the same metadata: the launcher could not
    /// read them, so it could not tell whether the run changed them. They are reported (never
    /// counted as unchanged silently).
    /// </summary>
    public static List<string> Unverified(Snapshot before, Snapshot after)
    {
        return [.. before.Dirty.Keys.Intersect(after.Dirty.Keys, StringComparer.Ordinal)
            .Where(p => before.Dirty[p] == after.Dirty[p] && FileHash.IsUnreadable(before.Dirty[p]))
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// Paths whose state differs between the two snapshots, in ordinal order.
    /// </summary>
    public static List<string> Changed(Snapshot before, Snapshot after) => Changed(before.Dirty, after.Dirty);

    /// <summary>
    /// The paths whose state differs between the two sets of watched files, in ordinal order.
    /// </summary>
    private static List<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        return [.. before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(p => !before.TryGetValue(p, out string? a) || !after.TryGetValue(p, out string? b) || a != b)
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// Whether a repository-relative path is in the scope. Patterns are globs relative to the repository root:
    /// <c>*</c> and <c>?</c> stay within one path segment, <c>**</c> crosses segments, and a pattern that names a
    /// directory (<c>src/Foo</c> or <c>src/Foo/</c>) covers everything under it.
    /// </summary>
    public static bool InScope(string path, IReadOnlyList<string> patterns) => patterns.Any(p => ToRegex(p).IsMatch(path));

    /// <summary>
    /// Builds the matcher for a single glob pattern. Exposed so other parts of the launcher
    /// (the trust check for <c>gate.trust</c> entries) can use exactly the same glob grammar the
    /// write-scope filter uses.
    /// </summary>
    internal static Regex BuildGlobRegex(string glob) => ToRegex(glob);

    private static Regex ToRegex(string glob)
    {
        string g = glob.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (g.StartsWith("./", StringComparison.Ordinal))
        {
            g = g[2..];
        }

        StringBuilder sb = new("^");
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                bool slash = i + 2 < g.Length && g[i + 2] == '/';
                _ = sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                _ = sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                _ = sb.Append("[^/]");
            }
            else
            {
                _ = sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return new Regex(sb.Append("(?:/.*)?$").ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string? Relative(string root, string path)
    {
        string rel = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
    }
}