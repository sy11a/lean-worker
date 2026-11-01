// Sweeping the worktrees an agent session leaves behind (decision W5: worktrees have a lifecycle). A linked
// worktree that holds no work, whose branch landed in the default branch, that no process works in and that is
// old enough may go; everything else stays. The default is a dry run; --apply removes what may go.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LeanWorker;

/// <summary>
/// The sweep over the linked worktrees of a set of repositories: one line per worktree
/// (<c>REMOVE|KEEP &lt;reason&gt; &lt;path&gt;</c>) and a total line. Without <c>--apply</c> nothing is removed.
/// </summary>
internal static class WorktreeSweep
{
    /// <summary>
    /// The worktrees that are kept whatever they hold: the pinned launcher only. The release and task
    /// checkouts are protected by the caller, with <c>--protect &lt;glob&gt;</c>.
    /// </summary>
    private static readonly string[] _builtInProtect = ["*-pinned"];

    /// <summary>
    /// The files git leaves behind while a rebase, a merge, a cherry-pick, a revert or a bisect runs: the
    /// worktree holds work in progress then, and it stays.
    /// </summary>
    private static readonly string[] _inProgressFiles =
        ["rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG"];

    /// <summary>
    /// The git files that move with the worktree: its head, its index and the log of its head.
    /// </summary>
    private static readonly string[] _gitFiles = ["HEAD", "index", Path.Combine("logs", "HEAD")];

    /// <summary>
    /// How two paths are compared: the file system of the machine decides whether letters case.
    /// </summary>
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// How old a worktree must be before the sweep may remove it.
    /// </summary>
    private const int DefaultOlderThanDays = 3;

    /// <summary>
    /// How many files one comparison of the branch against the default branch holds.
    /// </summary>
    private const int DiffChunk = 200;

    private const string Remove = "REMOVE";
    private const string Keep = "KEEP";

    private const string Merged = "merged";
    private const string Landed = "landed";
    private const string Dirty = "dirty";
    private const string Unmerged = "unmerged";
    private const string Locked = "locked";
    private const string Protected = "protected";
    private const string Nested = "nested";
    private const string InUse = "in-use";
    private const string Young = "young";
    private const string Missing = "missing";

    /// <summary>
    /// The usage of the mode, printed by <c>--help</c>.
    /// </summary>
    internal const string Usage = """
        sweep-worktrees: sweep the linked worktrees of the repositories.
          --repo <path>          repository to sweep (repeatable); the default is every repository under --root
          --root <dir>           folder whose repositories are swept (default: the parent of this repository)
          --older-than-days <n>  how old a worktree must be before it may go (default 3)
          --protect <glob>       glob of worktrees that stay (repeatable); built in: *-pinned
          --apply                remove the worktrees that may go (the default is a dry run)
          --help, -h             print this text
        One line per linked worktree: REMOVE|KEEP <reason> <path>.
        Reasons: merged, landed, dirty, unmerged, locked, protected, nested, in-use, young, missing.
        """;

    /// <summary>
    /// One worktree of a repository, as <c>git worktree list --porcelain</c> reports it.
    /// </summary>
    internal sealed record Worktree(string Path, string Head, bool Locked);

    /// <summary>
    /// A repository to sweep and the worktrees git lists for it (the main checkout first).
    /// </summary>
    private sealed record Repo(string Path, List<Worktree> Worktrees);

    /// <summary>
    /// What the sweep decided about one worktree: the action, the reason and the path it is about.
    /// </summary>
    internal sealed record Decision(string Action, string Reason, string Path);

    /// <summary>
    /// The sweep options: the repositories, the protect globs, the age limit and the dry-run switch.
    /// </summary>
    private sealed class SweepOptions
    {
        public readonly List<string> Repos = [];
        public readonly List<string> Protect = [.. _builtInProtect];
        public string? Root;
        public int OlderThanDays = DefaultOlderThanDays;
        public bool Apply;
        public bool Help;
    }

    /// <summary>
    /// Sweeps the worktrees: one line per linked worktree and a total. Without <c>--apply</c> the lines are
    /// the plan; with it, the worktrees that may go are removed.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        SweepOptions options = Parse(args);
        if (options.Help)
        {
            await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        await EnsureGitAsync().ConfigureAwait(false);

        // The working directories are read before the first git call: a git call of ours runs inside the
        // worktree it inspects, and that process must not make the worktree look in use.
        HashSet<string> workingDirectories = ProcessDirectories();
        List<Repo> repos = await ScanAsync(options).ConfigureAwait(false);

        // Every worktree of every repository, before any decision: a worktree that holds another worktree
        // may not go, or the removal would take that work with it.
        HashSet<string> allPaths = WorktreePaths(repos);
        DateTime cutoff = DateTime.UtcNow.AddDays(-options.OlderThanDays);
        int total = 0;
        int removable = 0;
        foreach (Repo repo in repos)
        {
            (int seen, int mayGo) = await SweepRepoAsync(repo, options, allPaths, workingDirectories, cutoff).ConfigureAwait(false);
            total += seen;
            removable += mayGo;
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"total: {total} worktrees, {removable} removable, {total - removable} kept");
        await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The repositories to sweep with their worktrees: the main checkout of every <c>--repo</c>, or every
    /// repository directly under <c>--root</c> (the parent folder of this repository by default).
    /// </summary>
    private static async Task<List<Repo>> ScanAsync(SweepOptions options)
    {
        List<Repo> repos = [];
        foreach (string main in await ResolveReposAsync(options).ConfigureAwait(false))
        {
            List<Worktree> worktrees = await ListWorktreesAsync(main).ConfigureAwait(false);
            repos.Add(new Repo(main, worktrees));
        }

        return repos;
    }

    /// <summary>
    /// The paths of every worktree of every repository, in the form the comparisons use.
    /// </summary>
    private static HashSet<string> WorktreePaths(List<Repo> repos)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (Repo repo in repos)
        {
            foreach (Worktree worktree in repo.Worktrees)
            {
                _ = paths.Add(Normalized(worktree.Path));
            }
        }

        return paths;
    }

    /// <summary>
    /// Fails when git is not there: the sweep is nothing without it.
    /// </summary>
    private static async Task EnsureGitAsync()
    {
        _ = await Git.RunResultAsync(".", "--version").ConfigureAwait(false)
            ?? throw new LaunchException("git is not available");
    }

    /// <summary>
    /// The options of the sweep: the repositories (each <c>--repo</c>, or the repositories under <c>--root</c>),
    /// the protect globs, the age limit and whether the sweep removes anything.
    /// </summary>
    private static SweepOptions Parse(string[] args)
    {
        SweepOptions options = new();
        ArgReader.Read(args, (arg, next) =>
        {
            switch (arg)
            {
                case "--repo": { options.Repos.Add(next()); break; }
                case "--protect": { options.Protect.Add(next()); break; }
                case "--root": { options.Root = next(); break; }
                case "--older-than-days": { options.OlderThanDays = Days(next()); break; }
                case "--apply": { options.Apply = true; break; }
                case "--help" or "-h": { options.Help = true; break; }
                default: return false;
            }

            return true;
        });

        return options;
    }

    private static int Days(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days < 0)
        {
            throw new LaunchException($"--older-than-days needs a number of days, not '{value}'");
        }

        return days;
    }

    /// <summary>
    /// The repositories to sweep: the main checkout of every <c>--repo</c>, or every repository directly under
    /// <c>--root</c> (the parent folder of this repository by default). Only main checkouts are scanned; their
    /// linked worktrees come from the worktree list.
    /// </summary>
    private static async Task<List<string>> ResolveReposAsync(SweepOptions options)
    {
        List<string> repos = [];
        if (options.Repos.Count > 0)
        {
            foreach (string repo in options.Repos)
            {
                if (!Directory.Exists(repo))
                {
                    throw new LaunchException($"no such directory: {repo}");
                }

                string main = await MainCheckoutAsync(repo).ConfigureAwait(false)
                    ?? throw new LaunchException($"not a git repository: {repo}");
                repos.Add(main);
            }
        }
        else
        {
            string root = options.Root ?? await DefaultRootAsync().ConfigureAwait(false);
            if (!Directory.Exists(root))
            {
                throw new LaunchException($"no such directory: {root}");
            }

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                // A linked worktree keeps its .git in a file; only a main checkout has the .git folder.
                if (!Directory.Exists(Path.Combine(dir, ".git")))
                {
                    continue;
                }

                string? main = await MainCheckoutAsync(dir).ConfigureAwait(false);
                if (main is not null)
                {
                    repos.Add(main);
                }
            }
        }

        return [.. repos.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The folder that holds the current repository: the parent of its main checkout.
    /// </summary>
    private static async Task<string> DefaultRootAsync()
    {
        string main = await MainCheckoutAsync(Directory.GetCurrentDirectory()).ConfigureAwait(false)
            ?? throw new LaunchException("not inside a git repository; name the repositories with --repo or the folder with --root");
        return Path.GetDirectoryName(main) ?? throw new LaunchException($"no parent folder for {main}");
    }

    /// <summary>
    /// The main checkout of the repository that holds the path: the first entry of its worktree list. Null when
    /// the path is not inside a repository (or git is not there).
    /// </summary>
    private static async Task<string?> MainCheckoutAsync(string path)
    {
        Git.Result? result = await Git.RunResultAsync(path, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        List<Worktree> worktrees = result.ExitCode is 0 ? ParseWorktrees(result.Stdout) : [];
        return worktrees.Count > 0 ? worktrees[0].Path : null;
    }

    /// <summary>
    /// The worktrees of a repository, the main checkout first.
    /// </summary>
    private static async Task<List<Worktree>> ListWorktreesAsync(string repo)
    {
        Git.Result result = await Git.RunResultAsync(repo, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false)
            ?? throw new LaunchException("git is not available");
        if (result.ExitCode is not 0)
        {
            throw new LaunchException($"git worktree list failed in {repo}: {result.Stderr.Trim()}");
        }

        return ParseWorktrees(result.Stdout);
    }

    /// <summary>
    /// The worktrees of a repository, as <c>git worktree list --porcelain -z</c> prints them: the main
    /// checkout first, then the linked ones, one block per worktree. Every entry ends with a NUL, and an
    /// empty entry closes a worktree.
    /// </summary>
    private static List<Worktree> ParseWorktrees(string porcelain)
    {
        List<Worktree> worktrees = [];
        string? path = null;
        string head = string.Empty;
        bool locked = false;

        void Add()
        {
            if (path is not null)
            {
                worktrees.Add(new Worktree(path, head, locked));
            }

            path = null;
            head = string.Empty;
            locked = false;
        }

        foreach (string entry in porcelain.Split('\0'))
        {
            if (entry.Length is 0)
            {
                Add();
            }
            else if (entry.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Add();
                path = entry["worktree ".Length..];
            }
            else if (entry.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = entry["HEAD ".Length..].Trim();
            }
            else if (entry.StartsWith("locked", StringComparison.Ordinal))
            {
                locked = true;
            }
        }

        Add();
        return worktrees;
    }

    /// <summary>
    /// Decides about every linked worktree of the repository and reports it: one line per worktree. With
    /// <c>--apply</c> the worktrees that may go are removed and the ones whose folder is gone are pruned.
    /// </summary>
    private static async Task<(int Total, int Removable)> SweepRepoAsync(Repo repo, SweepOptions options,
        HashSet<string> allPaths, HashSet<string> workingDirectories, DateTime cutoff)
    {
        string? defaultBranch = await DefaultBranchAsync(repo.Path).ConfigureAwait(false);
        int total = 0;
        int removable = 0;
        List<Worktree> protectedMissing = [];
        foreach (Worktree worktree in repo.Worktrees.Skip(1))
        {
            Decision decision = await DecideAsync(repo.Path, worktree, options, defaultBranch, allPaths, workingDirectories, cutoff).ConfigureAwait(false);
            if (decision.Action is Remove && options.Apply && decision.Reason is not Missing)
            {
                // Right before the removal the world may have moved: a process may work in the worktree now.
                if (IsInUse(worktree.Path, ProcessDirectories()))
                {
                    decision = new Decision(Keep, InUse, worktree.Path);
                }
            }

            await Console.Out.WriteLineAsync($"{decision.Action} {decision.Reason} {decision.Path}").ConfigureAwait(false);
            total++;
            if (decision.Action is not Remove)
            {
                if (decision.Reason is Protected && !Directory.Exists(worktree.Path))
                {
                    protectedMissing.Add(worktree);
                }

                continue;
            }

            removable++;
            if (!options.Apply)
            {
                continue;
            }

            if (decision.Reason is Missing)
            {
                // The folder is gone: there is nothing to remove, the prune drops the record.
                continue;
            }

            await RemoveAsync(repo.Path, worktree.Path).ConfigureAwait(false);
        }

        if (options.Apply)
        {
            await PruneAsync(repo.Path, protectedMissing).ConfigureAwait(false);
        }

        return (total, removable);
    }

    /// <summary>
    /// Whether the worktree may go, and why it stays when it may not. A worktree goes when it is not the main
    /// one, not locked, not protected, not the home of another worktree, clean, its branch landed in the
    /// default branch, no process works in it and it is old enough.
    /// </summary>
    private static async Task<Decision> DecideAsync(string repo, Worktree worktree, SweepOptions options,
        string? defaultBranch, HashSet<string> allPaths, HashSet<string> workingDirectories, DateTime cutoff)
    {
        if (worktree.Locked)
        {
            return new Decision(Keep, Locked, worktree.Path);
        }

        if (IsProtected(worktree.Path, options.Protect))
        {
            return new Decision(Keep, Protected, worktree.Path);
        }

        if (IsNested(worktree.Path, allPaths))
        {
            // Another worktree lives inside it: removing the folder would take that work with it.
            return new Decision(Keep, Nested, worktree.Path);
        }

        if (!Directory.Exists(worktree.Path))
        {
            return new Decision(Remove, Missing, worktree.Path);
        }

        if (await InProgressAsync(worktree.Path).ConfigureAwait(false))
        {
            // A rebase, a merge, a cherry-pick, a revert or a bisect runs there: the work is in progress.
            return new Decision(Keep, Dirty, worktree.Path);
        }

        string? status = await Git.RunAsync(worktree.Path, "--no-optional-locks", "status", "--porcelain").ConfigureAwait(false);
        if (status?.Trim().Length is not 0)
        {
            return new Decision(Keep, Dirty, worktree.Path);
        }

        string landed = await LandedAsync(repo, worktree, defaultBranch).ConfigureAwait(false);
        if (landed is Unmerged)
        {
            return new Decision(Keep, Unmerged, worktree.Path);
        }

        if (IsInUse(worktree.Path, workingDirectories))
        {
            return new Decision(Keep, InUse, worktree.Path);
        }

        DateTime last = await LastChangeAsync(worktree.Path).ConfigureAwait(false);
        if (last > cutoff)
        {
            return new Decision(Keep, Young, worktree.Path);
        }

        return new Decision(Remove, landed, worktree.Path);
    }

    /// <summary>
    /// Whether a rebase, a merge, a cherry-pick, a revert or a bisect runs in the worktree: git leaves a file
    /// behind for each of them, and the worktree holds work in progress while it runs.
    /// </summary>
    private static async Task<bool> InProgressAsync(string worktreePath)
    {
        foreach (string name in _inProgressFiles)
        {
            string? path = await Git.RunAsync(worktreePath, "rev-parse", "--git-path", name).ConfigureAwait(false);
            if (path is null)
            {
                continue;
            }

            string file = path.Trim();
            if (file.Length is 0)
            {
                continue;
            }

            if (File.Exists(file) || Directory.Exists(file))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When the worktree last changed: the newest of the folder's write time and the write times of the git
    /// files that move with the work (the head, the index and the log of the head). Files that are not there
    /// are ignored.
    /// </summary>
    private static async Task<DateTime> LastChangeAsync(string worktreePath)
    {
        DateTime newest = Directory.GetLastWriteTimeUtc(worktreePath);
        string? gitDir = await Git.RunAsync(worktreePath, "rev-parse", "--git-dir").ConfigureAwait(false);
        if (gitDir is null)
        {
            return newest;
        }

        string dir = gitDir.Trim();
        if (dir.Length is 0)
        {
            return newest;
        }

        if (!Path.IsPathRooted(dir))
        {
            dir = Path.Combine(worktreePath, dir);
        }

        foreach (string name in _gitFiles)
        {
            string file = Path.Combine(dir, name);
            if (!File.Exists(file))
            {
                continue;
            }

            DateTime written = File.GetLastWriteTimeUtc(file);
            if (written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }

    /// <summary>
    /// Whether the branch of the worktree landed in the default branch: its head is an ancestor of it, or every
    /// file the branch changed since the merge base is the same there (a squash merge). Anything else is
    /// unmerged, and an empty file list is no landing at all.
    /// </summary>
    private static async Task<string> LandedAsync(string repo, Worktree worktree, string? defaultBranch)
    {
        if (defaultBranch is null)
        {
            return Unmerged;
        }

        Git.Result? ancestor = await Git.RunResultAsync(repo, "merge-base", "--is-ancestor", worktree.Head, defaultBranch).ConfigureAwait(false);
        if (ancestor?.ExitCode is 0)
        {
            return Merged;
        }

        string? mergeBase = await Git.RunAsync(repo, "merge-base", worktree.Head, defaultBranch).ConfigureAwait(false);
        if (mergeBase is null)
        {
            return Unmerged;
        }

        List<string> files = await ChangedFilesAsync(repo, mergeBase.Trim(), worktree.Head).ConfigureAwait(false);
        if (files.Count is 0)
        {
            return Unmerged;
        }

        return await SameFilesAsync(repo, worktree.Head, defaultBranch, files).ConfigureAwait(false) ? Landed : Unmerged;
    }

    /// <summary>
    /// Whether the files the branch changed are the same in the default branch: the work landed by a squash
    /// merge. They are compared in chunks, so a big change does not make one command line too long.
    /// </summary>
    private static async Task<bool> SameFilesAsync(string repo, string head, string defaultBranch, List<string> files)
    {
        for (int i = 0; i < files.Count; i += DiffChunk)
        {
            List<string> args = ["--literal-pathspecs", "diff", "--quiet", head, defaultBranch, "--"];
            args.AddRange(files.GetRange(i, Math.Min(DiffChunk, files.Count - i)));
            Git.Result? same = await Git.RunResultAsync(repo, [.. args]).ConfigureAwait(false);
            if (same?.ExitCode is not 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The files the branch changed since the merge base; empty when there are none or when git cannot tell.
    /// </summary>
    private static async Task<List<string>> ChangedFilesAsync(string repo, string mergeBase, string head)
    {
        Git.Result? changed = await Git.RunResultAsync(repo, "diff", "--no-renames", "--name-only", "-z", mergeBase, head).ConfigureAwait(false);
        if (changed is null)
        {
            return [];
        }

        return changed.ExitCode is 0 ? [.. changed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)] : [];
    }

    /// <summary>
    /// The branch the worktrees are compared against: the default branch of origin, as origin/HEAD names it,
    /// or origin/main or origin/master when origin/HEAD is not set.
    /// </summary>
    private static async Task<string?> DefaultBranchAsync(string repo)
    {
        string? head = await Git.RunAsync(repo, "symbolic-ref", "--short", "refs/remotes/origin/HEAD").ConfigureAwait(false);
        string? name = head?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        foreach (string candidate in new[] { "origin/main", "origin/master" })
        {
            Git.Result? result = await Git.RunResultAsync(repo, "rev-parse", "--verify", "--quiet", candidate + "^{commit}").ConfigureAwait(false);
            if (result?.ExitCode is 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Removes one worktree. Git refuses when the worktree still holds changes; the refusal is reported and
    /// the sweep carries on. No branch is deleted.
    /// </summary>
    private static async Task RemoveAsync(string repo, string path)
    {
        Git.Result? result = await Git.RunResultAsync(repo, "worktree", "remove", path).ConfigureAwait(false);
        await ReportAsync(result, $"could not remove {path}").ConfigureAwait(false);
    }

    /// <summary>
    /// Reports why a git call failed, when it failed.
    /// </summary>
    private static async Task ReportAsync(Git.Result? result, string what)
    {
        string? why = Failure(result);
        if (why is null)
        {
            return;
        }

        await Console.Error.WriteLineAsync($"{what}: {why}").ConfigureAwait(false);
    }

    /// <summary>
    /// Why a git call failed, or null when it did not fail.
    /// </summary>
    private static string? Failure(Git.Result? result)
    {
        if (result is null)
        {
            return "git failed";
        }

        return result.ExitCode is 0 ? null : result.Stderr.Trim();
    }

    /// <summary>
    /// Prunes the worktrees whose folder is gone. A protected one is locked for the prune and unlocked after:
    /// the prune would otherwise drop the record of a worktree the user asked to keep.
    /// </summary>
    private static async Task PruneAsync(string repo, List<Worktree> protectedMissing)
    {
        List<Worktree> locked = [];
        foreach (Worktree worktree in protectedMissing)
        {
            Git.Result? result = await Git.RunResultAsync(repo, "worktree", "lock", "--reason", "lean-worker sweep protects this worktree", worktree.Path).ConfigureAwait(false);
            if (Failure(result) is not null)
            {
                await Console.Error.WriteLineAsync($"kept {worktree.Path}: git could not lock it, so {repo} was not pruned").ConfigureAwait(false);
                await UnlockAsync(repo, locked).ConfigureAwait(false);
                return;
            }

            locked.Add(worktree);
        }

        Git.Result? prune = await Git.RunResultAsync(repo, "worktree", "prune").ConfigureAwait(false);
        await ReportAsync(prune, $"could not prune {repo}").ConfigureAwait(false);

        await UnlockAsync(repo, locked).ConfigureAwait(false);
    }

    private static async Task UnlockAsync(string repo, List<Worktree> locked)
    {
        foreach (Worktree worktree in locked)
        {
            _ = await Git.RunResultAsync(repo, "worktree", "unlock", worktree.Path).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the path matches a protect glob. The glob is matched against the whole path and against the
    /// folder's own name, so <c>*-pinned</c> covers the pinned worktree wherever it lives.
    /// </summary>
    private static bool IsProtected(string path, List<string> protects)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        foreach (string glob in protects)
        {
            Regex regex = GlobRegex(glob);
            if (regex.IsMatch(full) || regex.IsMatch(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The matcher for a protect glob: <c>*</c> and <c>?</c> match any character, <c>[...]</c> a character
    /// class. A glob that is no valid pattern matches itself.
    /// </summary>
    private static Regex GlobRegex(string glob)
    {
        StringBuilder pattern = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c is '*')
            {
                _ = pattern.Append(".*");
            }
            else if (c is '?')
            {
                _ = pattern.Append('.');
            }
            else if (c is '[')
            {
                int end = glob.IndexOf(']', i + 1);
                if (end < 0)
                {
                    _ = pattern.Append("\\[");
                }
                else
                {
                    _ = pattern.Append('[').Append(ClassContent(glob[(i + 1)..end])).Append(']');
                    i = end;
                }
            }
            else
            {
                _ = pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        _ = pattern.Append('$');
        try
        {
            return new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return new Regex("^" + Regex.Escape(glob) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// The content of a character class: a leading <c>!</c> negates the class and becomes <c>^</c>; a
    /// backslash is a backslash, so it is escaped.
    /// </summary>
    private static string ClassContent(string content)
    {
        string text = content.Length > 0 && content[0] is '!' ? "^" + content[1..] : content;
        return text.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a process works inside the worktree: its current directory is the worktree or below it.
    /// </summary>
    private static bool IsInUse(string path, HashSet<string> workingDirectories)
    {
        string root = Normalized(path);
        foreach (string directory in workingDirectories)
        {
            if (string.Equals(directory, root, _pathComparison) || directory.StartsWith(root + Path.DirectorySeparatorChar, _pathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether another worktree lives inside the worktree: the folder may not go, the removal would take
    /// that work with it. The test is the one of <see cref="IsInUse"/>: the other path must be under the
    /// folder, with the separator in between.
    /// </summary>
    private static bool IsNested(string path, HashSet<string> allPaths)
    {
        string root = Normalized(path);
        foreach (string other in allPaths)
        {
            if (string.Equals(other, root, _pathComparison))
            {
                continue; // the same path is not under itself
            }

            if (other.StartsWith(root + Path.DirectorySeparatorChar, _pathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The path as the comparisons use it: absolute and without a trailing separator.
    /// </summary>
    private static string Normalized(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
            return trimmed.Length is 0 ? full : trimmed;
        }
        catch (ArgumentException)
        {
            // a path that is no path at all matches nothing
            return path;
        }
    }

    /// <summary>
    /// The current directory of every process that can be read: the /proc entries that cannot be read are
    /// skipped.
    /// </summary>
    private static HashSet<string> ProcessDirectories()
    {
        HashSet<string> directories = new(StringComparer.Ordinal);
        if (!Directory.Exists("/proc"))
        {
            return directories;
        }

        foreach (string entry in ProcessEntries())
        {
            string? target = LinkTarget(Path.Combine(entry, "cwd"));
            if (target is null)
            {
                continue;
            }

            try
            {
                _ = directories.Add(Path.GetFullPath(target));
            }
            catch (ArgumentException)
            {
                // a path that is no path at all cannot be inside a worktree
            }
            catch (Exception e) when (e is IOException or NotSupportedException)
            {
                // a path that cannot be read (or is not supported) cannot be inside a worktree either
            }
        }

        return directories;
    }

    private static List<string> ProcessEntries()
    {
        try
        {
            return [.. Directory.GetDirectories("/proc")];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? LinkTarget(string link)
    {
        try
        {
            FileSystemInfo? info = File.ResolveLinkTarget(link, returnFinalTarget: true);
            if (info is null)
            {
                return null;
            }

            string target = info.FullName;
            return target.EndsWith(" (deleted)", StringComparison.Ordinal) ? target[..^" (deleted)".Length] : target;
        }
        catch (IOException)
        {
            return null; // the process of another user cannot be read
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
