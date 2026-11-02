using System.Globalization;
using System.Security.Cryptography;
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
    /// The dirty files of a working tree: path relative to the repository root → content hash.
    /// </summary>
    internal sealed record Snapshot(string Root, Dictionary<string, string> Dirty);

    /// <summary>
    /// Null when <paramref name="cwd"/> is not inside a git working tree (or git is missing). The
    /// excluded paths (the launcher's own bookkeeping) leave both snapshots of a comparison when
    /// they lie in the tree; an exclude outside it is ignored.
    /// </summary>
    public static async Task<Snapshot?> TakeAsync(string cwd, params string?[] excludes)
    {
        string? root = (await Git.RunAsync(cwd, "rev-parse", "--show-toplevel").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        string? status = await Git.RunAsync(root, "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

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
                dirty[path] = File.Exists(full) ? await HashAsync(full).ConfigureAwait(false) : "-";
            }
        }
        return new Snapshot(root, dirty);
    }

    /// <summary>
    /// The snapshot of the git metadata of a working tree: the config and the hooks of the common
    /// git dir and the worktree's own config, hashed by name (".git/config" → its hash). The
    /// write scope never sees them — it is git-status based — so they are watched on their own
    /// (issue #114). The snapshot has the shape of every other one: the tree it was taken in and
    /// the hash of every watched file. Null when the tree is not a git tree (or git is missing).
    /// </summary>
    public static async Task<Snapshot?> TakeGitMetadataAsync(string dir, string labelBase)
    {
        Dictionary<string, string>? files = await GitMetadata.TakeAsync(dir, labelBase).ConfigureAwait(false);
        return files is null ? null : new Snapshot(dir, files);
    }

    /// <summary>
    /// The content hash of a file that the listing showed as present: a file that cannot be read
    /// (no permission, a directory in its way, an I/O error), or one that vanished between the
    /// listing and this read, is recorded by its metadata instead of throwing — an unreadable
    /// file must not crash the run. The marker differs from every real hash (and from <c>-</c>,
    /// deleted), so a file that is readable in only one of the two snapshots is reported as
    /// changed; a file unreadable in both snapshots is only equal when its metadata is equal, and
    /// is then reported as unverified (<see cref="Unverified"/>).
    /// </summary>
    private static async Task<string> HashAsync(string full)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(full, CancellationToken.None).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(full);
        }
    }

    /// <summary>
    /// The entry of a file the launcher could not read: the marker <c>"?"</c> plus the metadata
    /// that is still readable — the size, the mtime (in ticks) and the unix mode. A file that
    /// changed while it stayed unreadable (a write that kept the mode) changes its size or its
    /// mtime, so it counts as changed instead of slipping through as unchanged. When even the
    /// metadata cannot be read, the entry is the bare marker: nothing could be told about the
    /// file at all.
    /// </summary>
    private static string Unreadable(string full)
    {
        try
        {
            FileInfo info = new(full);
            return string.Create(CultureInfo.InvariantCulture, $"?|{info.Length}|{File.GetLastWriteTimeUtc(full).Ticks}|{Mode(full)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    /// <summary>
    /// The unix mode of a file, or <c>"-"</c> where the platform has none (Windows) or the mode
    /// cannot be read.
    /// </summary>
    private static string Mode(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            return "-";
        }

        try
        {
            return ((int)File.GetUnixFileMode(full)).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }

    /// <summary>
    /// The paths both snapshots saw as unreadable with the same metadata: the launcher could not
    /// read them, so it could not tell whether the run changed them. They are reported (never
    /// counted as unchanged silently).
    /// </summary>
    public static List<string> Unverified(Snapshot before, Snapshot after)
    {
        return [.. before.Dirty.Keys.Intersect(after.Dirty.Keys, StringComparer.Ordinal)
            .Where(p => before.Dirty[p] == after.Dirty[p] && before.Dirty[p].StartsWith('?'))
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// Paths whose state differs between the two snapshots, in ordinal order.
    /// </summary>
    public static List<string> Changed(Snapshot before, Snapshot after)
    {
        return [.. before.Dirty.Keys.Union(after.Dirty.Keys, StringComparer.Ordinal)
            .Where(p => !before.Dirty.TryGetValue(p, out string? a) || !after.Dirty.TryGetValue(p, out string? b) || a != b)
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