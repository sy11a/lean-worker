// The `<repo>` token in allowedTools / deniedTools: the launcher fills it with `git rev-parse --show-toplevel`
// so a Bash rule can name the absolute repo path without profiles or the price book needing one.

namespace LeanWorker;

internal static class RepoToken
{
    public const string Token = "<repo>";

    /// <summary>
    /// The absolute path of the git working tree that contains <paramref name="cwd"/>; null when git is missing,
    /// fails, or <paramref name="cwd"/> is not inside a working tree.
    /// </summary>
    public static async Task<string?> RootAsync(string cwd)
    {
        string? root = await Git.RunAsync(cwd, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        root = root.Trim();
        return root.Length > 0 ? root : null;
    }

    /// <summary>
    /// Replaces every <see cref="Token"/> in each entry with <paramref name="root"/>. An entry containing
    /// <see cref="Token"/> is dropped when <paramref name="root"/> is not usable (never emit the literal token
    /// or a path that carries a shell or glob character). Order is kept; an entry equal to an earlier one after
    /// expansion is dropped (first kept).
    /// </summary>
    public static List<string> Expand(IEnumerable<string> entries, string? root)
        => ExpandCore(entries, root, static _ => null);

    /// <summary>
    /// Like <see cref="Expand"/>, but for deny lists: when <paramref name="root"/> is not usable, the first entry
    /// that contains <see cref="Token"/> stops the launch with a <see cref="LaunchException"/> (a dropped or
    /// broadened deny would lift a guard); entries without the token still pass through.
    /// </summary>
    public static List<string> ExpandDenied(IEnumerable<string> entries, string? root)
        => ExpandCore(entries, root, entry =>
            throw new LaunchException($"deniedTools entry {entry} needs {Token}, but the working tree has no usable root: {root ?? "none"}"));

    private static List<string> ExpandCore(IEnumerable<string> entries, string? root, Func<string, string?> onUnusableRoot)
    {
        bool usable = IsUsableRoot(root);
        List<string> kept = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string entry in entries)
        {
            string? value;
            if (entry.Contains(Token, StringComparison.Ordinal))
            {
                if (usable)
                {
                    value = entry.Replace(Token, root, StringComparison.Ordinal);
                }
                else
                {
                    value = onUnusableRoot(entry);
                    if (value is null)
                    {
                        continue;
                    }
                }
            }
            else
            {
                value = entry;
            }

            if (seen.Add(value))
            {
                kept.Add(value);
            }
        }
        return kept;
    }

    private static bool IsUsableRoot(string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (root[0] != '/')
        {
            return false;
        }

        if (root.Length is 1 || root[^1] == '/')
        {
            return false;
        }

        foreach (char c in root)
        {
            if (c is '*' or '?' or '[' or ']' or '(' or ')' or '{' or '}' or '\\' or '"' or '\'' or '$' or '`' or ' ' or '\t' or '\n' or '\r' or ':' or ',' or ';' or '|' or '&' or '<' or '>')
            {
                return false;
            }
        }
        return true;
    }
}