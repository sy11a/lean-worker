// The gate runs in the working tree the worker has just changed, but the launcher treats some files as
// trusted inputs (the gate's command, the profile, the project notes, the price book, the run-root
// config, the .config/dotnet-tools.json at the work tree root). A worker that edits them can swap the
// gate's report path or its command for the next round. The launcher hashes these files before the
// worker starts and again before the gate runs; a difference ends the chain with `error` instead of
// running the gate against tampered inputs.

using System.Security.Cryptography;

namespace LeanWorker;

internal static class GateTrust
{
    /// <summary>
    /// One set of trusted files and their SHA-256 of the bytes. The sentinel "<c>-</c>" is recorded for
    /// every path that does not exist at hash time, so a file the worker created between the two hashes
    /// shows up as a difference just like a content change.
    /// </summary>
    internal sealed record Snapshot(Dictionary<string, string> Hashes);

    /// <summary>
    /// Reads each <paramref name="paths"/> entry and hashes its bytes. Absent files get the sentinel
    /// "<c>-</c>"; an unreadable file throws so a real permission problem is visible.
    /// </summary>
    public static Snapshot Hash(IEnumerable<string> paths)
    {
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            hashes[path] = File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : "-";
        }

        return new Snapshot(hashes);
    }

    /// <summary>
    /// Paths whose state differs between the two snapshots, in ordinal order. A path counts as changed
    /// when it is in one snapshot but not the other, or when its hash differs.
    /// </summary>
    public static List<string> Changed(Snapshot before, Snapshot after)
    {
        return [.. before.Hashes.Keys.Union(after.Hashes.Keys, StringComparer.Ordinal)
            .Where(p => !before.Hashes.TryGetValue(p, out string? a) || !after.Hashes.TryGetValue(p, out string? b) || a != b)
            .Order(StringComparer.Ordinal),];
    }

    /// <summary>
    /// The paths the gate chain treats as trusted: every top-level file in <paramref name="runsRoot"/>
    /// other than <c>runs.jsonl</c> (the <c>runs/</c>, <c>inbox/</c> and <c>system/</c> subtrees are
    /// scoped away by construction), <c>.config/dotnet-tools.json</c> at <paramref name="root"/>, and
    /// every argv of <paramref name="gateCommand"/> that is an existing file path under <paramref name="root"/>.
    /// </summary>
    public static List<string> CollectPaths(string runsRoot, string root, IReadOnlyList<string> gateCommand)
    {
        List<string> paths = [];
        if (Directory.Exists(runsRoot))
        {
            foreach (string file in Directory.EnumerateFiles(runsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(file) is "runs.jsonl")
                {
                    continue;
                }

                paths.Add(file);
            }
        }

        paths.Add(Path.Combine(root, ".config", "dotnet-tools.json"));

        foreach (string entry in gateCommand)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.IsPathRooted(entry) ? Path.GetFullPath(entry) : Path.GetFullPath(Path.Combine(root, entry));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!File.Exists(full))
            {
                continue;
            }

            string rel = Path.GetRelativePath(root, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            {
                continue;
            }

            paths.Add(full);
        }

        return paths;
    }
}