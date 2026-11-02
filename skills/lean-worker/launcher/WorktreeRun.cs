// The worktree a run owns (--worktree): the launcher creates it before the worker starts, the worker
// works in it, and the launcher removes it again when the run left nothing behind. The run's own
// bookkeeping stays in the repository the launcher was called in: the run directory, the ledger and
// the profiles are read from there, so the ledger holds one row per run and not one per worktree.
//
// The launcher never destroys work. It removes the worktree only when the branch and the worktree
// still point at the commit the branch was created from, when the working tree holds no change of
// the worker and when the only files in it are the ones the launcher itself copied in — and it
// deletes those itself, file by file. Every doubt (a commit, a change, a git error) keeps the
// worktree and says why.

using System.Globalization;

namespace LeanWorker;

/// <summary>
/// The worktree one run owns: the branch it checks out, the path it lives at and the ref it was
/// branched from. The launcher creates it before the worker starts, the worker and the gate run in
/// it, and after the worker the launcher decides whether it stays (it holds changes or commits) or
/// goes (it holds nothing).
/// </summary>
internal sealed class WorktreeRun
{
    /// <summary>
    /// The path of the worktree.
    /// </summary>
    public string WorktreePath { get; }

    /// <summary>
    /// The branch the worktree checks out.
    /// </summary>
    public string Branch { get; }

    /// <summary>
    /// The ref the branch was created from.
    /// </summary>
    public string Base { get; }

    /// <summary>
    /// The commit the base ref resolved to: the branch starts there, and the worktree is only
    /// removed while the branch and its HEAD still point at it.
    /// </summary>
    public string BaseSha { get; }

    /// <summary>
    /// What became of the worktree: removed or kept. Empty until the decision is made.
    /// </summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>
    /// Why the worktree was removed or kept: empty, changes, commits, or the git error that stopped
    /// the decision.
    /// </summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// What the decision left behind, when it left anything (a branch that could not be deleted).
    /// </summary>
    public string? Note { get; private set; }

    /// <summary>
    /// The repository the worktree belongs to: the git calls that remove it and its branch run there.
    /// </summary>
    private readonly string _repo;

    /// <summary>
    /// The files the launcher copied into the worktree: it deletes exactly these files again when
    /// it removes the worktree, and nothing else.
    /// </summary>
    private readonly List<SetupFile> _copied;

    /// <summary>
    /// Why the worktree cannot be judged empty at all (a base that cannot be resolved any more).
    /// Empty when it can.
    /// </summary>
    private readonly string _unverifiable;

    /// <summary>
    /// One file of the setup the launcher put into a worktree: where it sits there (relative to the
    /// root of the worktree) and the file it was copied from.
    /// </summary>
    private sealed record SetupFile(string Relative, string Source);

    private WorktreeRun(string repo, string path, string branch, string baseRef, string baseSha, List<SetupFile> copied, string unverifiable)
    {
        _repo = repo;
        WorktreePath = path;
        Branch = branch;
        Base = baseRef;
        BaseSha = baseSha;
        _copied = copied;
        _unverifiable = unverifiable;
    }

    /// <summary>
    /// The path the worktree of a run gets: the repository's own folder name and the run's name side
    /// by side, in the parent folder of the repository — the place a delegator would put it by hand.
    /// </summary>
    public static string PathFor(string repoRoot, string name)
    {
        string full = Path.GetFullPath(repoRoot);
        string parent = Path.GetDirectoryName(full) ?? full;
        return Path.Combine(parent, Path.GetFileName(full) + "-" + Launcher.SafeName(name));
    }

    /// <summary>
    /// The ref a worktree is branched from: the one the operator named, or the default branch of the
    /// repository — the same one the sweep compares the worktrees against.
    /// </summary>
    public static async Task<string> BaseRefAsync(string repoRoot, string? baseRef)
    {
        if (baseRef is { Length: > 0 })
        {
            return baseRef;
        }

        string? defaultBranch = await WorktreeSweep.DefaultBranchAsync(repoRoot).ConfigureAwait(false);
        return defaultBranch ?? throw new LaunchException($"no default branch in {repoRoot}; name the base with --worktree-base <ref>");
    }

    /// <summary>
    /// Creates the worktree: the branch <paramref name="branch"/> at the path of
    /// <see cref="PathFor"/>, branched from the commit <paramref name="baseRef"/> names. Nothing is
    /// created when the path or the branch is taken already, or when the base does not resolve: the
    /// run refuses to start instead of taking over what is there. The setup of the repository the
    /// launcher was called in (<paramref name="setup"/>) is copied into the new worktree, so the
    /// worker finds the same profiles, project notes and system notes there.
    /// </summary>
    public static async Task<WorktreeRun> CreateAsync(string repoRoot, string branch, string baseRef, string name, string setup)
    {
        string path = PathFor(repoRoot, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new LaunchException($"the worktree path is taken: {path}");
        }

        if (await BranchExistsAsync(repoRoot, branch).ConfigureAwait(false))
        {
            throw new LaunchException($"the branch is taken: {branch}");
        }

        // The base is resolved once, in the repository the launcher was called in: the commit it
        // names is what the branch is created from and what the worktree must still point at when
        // it is removed. A base that does not resolve stops the run before anything is created.
        (string? sha, string error) = await ResolveBaseAsync(repoRoot, baseRef).ConfigureAwait(false);
        if (sha is null)
        {
            throw new LaunchException($"the worktree base '{baseRef}' does not resolve to a commit: {EvidenceBlock.OneLine(error)}");
        }

        Git.Result added = await Git.RunResultAsync(repoRoot, "worktree", "add", "-b", branch, path, sha).ConfigureAwait(false)
            ?? throw new LaunchException("git could not create the worktree: git could not be run");
        if (added.ExitCode is not 0)
        {
            throw new LaunchException($"git could not create the worktree: {EvidenceBlock.OneLine(added.Stderr.Trim())}");
        }

        try
        {
            HashSet<string> tracked = await TrackedAtAsync(repoRoot, sha).ConfigureAwait(false);
            List<SetupFile> copied = CopySetup(setup, path, tracked);
            return new WorktreeRun(repoRoot, path, branch, baseRef, sha, copied, string.Empty);
        }
        catch (Exception ex)
        {
            // The worktree exists and the branch is checked out in it: the run cannot go on, and the
            // worktree stays where it is — named in the error, never removed by force.
            throw new LaunchException($"the worktree {path} (branch {branch}) was left behind: {EvidenceBlock.OneLine(ex.Message)}");
        }
    }

    /// <summary>
    /// The worktree of a later gate round: the same path and the same branch as the first round's —
    /// the run keeps the worktree it started with. A worktree that is gone is a launch error: the
    /// round has nothing to work in.
    /// </summary>
    public static async Task<WorktreeRun> ReuseAsync(string repoRoot, string branch, string baseRef, string name, string setup)
    {
        string path = PathFor(repoRoot, name);
        if (!Directory.Exists(path))
        {
            throw new LaunchException($"the worktree of the previous round is gone: {path}");
        }

        // The base is resolved again: the commit it names is what the branch must still point at
        // when the worktree is removed. A base that cannot be resolved any more leaves the worktree
        // where it is — it cannot be judged empty.
        (string? sha, string error) = await ResolveBaseAsync(repoRoot, baseRef).ConfigureAwait(false);
        if (sha is null)
        {
            return new WorktreeRun(repoRoot, path, branch, baseRef, string.Empty, [], $"the base '{baseRef}' cannot be resolved: {EvidenceBlock.OneLine(error)}");
        }

        // The files of the setup the first round copied in: they are the launcher's own and go with
        // the worktree, everything else in it is the worker's work.
        HashSet<string> tracked = await TrackedAtAsync(repoRoot, sha).ConfigureAwait(false);
        List<SetupFile> copied =
        [
            .. SetupFiles(setup).Where(f => !tracked.Contains(f.Relative) && File.Exists(Path.Combine(path, f.Relative))),
        ];

        return new WorktreeRun(repoRoot, path, branch, baseRef, sha, copied, string.Empty);
    }

    /// <summary>
    /// The worktree stays for the next round of the gate chain.
    /// </summary>
    public void KeepForChain()
    {
        Action = "kept";
        Reason = "chain";
    }

    /// <summary>
    /// The decision after the worker: the worktree goes when it holds no change and its branch no
    /// commit beyond the base, and it stays otherwise. A git error never removes anything: the
    /// worktree stays and the error is the reason.
    /// </summary>
    public async Task FinishAsync()
    {
        if (_unverifiable.Length > 0)
        {
            Kept(_unverifiable);
            return;
        }

        // The branch and the worktree must still point at the commit the branch was created from:
        // a commit the worker made is work that stays.
        if (!await AtBaseAsync().ConfigureAwait(false))
        {
            return;
        }

        // A copied path that is not a plain path inside the worktree (a symlink or reparse point on
        // the way, or one that leaves the worktree) stops the decision before anything is read or
        // any status is taken: the emptiness check below runs git status in the worktree and must
        // not do so through a link, and SameContent and the removal must neither read through nor
        // delete along one.
        if (UnsafeCopiedPath() is { } unsafePath)
        {
            KeptForUnsafeCopiedPath(unsafePath);
            return;
        }

        // The link check above passed, so every copied path is plain: the working tree must hold
        // nothing but the files the launcher itself copied in.
        if (!await EmptyAsync(Exempt()).ConfigureAwait(false))
        {
            return;
        }

        // A file the launcher copied in that the worker changed is work, not setup.
        if (_copied.Exists(f => !SameContent(f)))
        {
            Action = "kept";
            Reason = "changes";
            return;
        }

        await RemoveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the branch and the worktree still point at the commit the branch was created from.
    /// A commit the worker made is work that stays: the worktree is kept and the reason says why.
    /// </summary>
    private async Task<bool> AtBaseAsync()
    {
        if (!await OwnsGitDirAsync().ConfigureAwait(false))
        {
            return false;
        }

        Git.Result? head = await Git.RunResultAsync(WorktreePath, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        Git.Result? branch = await Git.RunResultAsync(_repo, "rev-parse", "--verify", "--end-of-options", "refs/heads/" + Branch).ConfigureAwait(false);
        if (head is null || branch is null)
        {
            Kept("git failed");
            return false;
        }

        if (head.ExitCode is not 0)
        {
            Kept(head.Stderr.Trim());
            return false;
        }

        if (branch.ExitCode is not 0)
        {
            Kept(branch.Stderr.Trim());
            return false;
        }

        if (head.Stdout.Trim().Equals(BaseSha, StringComparison.Ordinal) && branch.Stdout.Trim().Equals(BaseSha, StringComparison.Ordinal))
        {
            return true;
        }

        Action = "kept";
        Reason = "commits";
        return false;
    }

    /// <summary>
    /// Whether the worktree holds nothing but the exempt paths: the emptiness check. A git error
    /// keeps the worktree, and the error is the reason.
    /// </summary>
    private async Task<bool> EmptyAsync(IReadOnlySet<string> exempt)
    {
        if (!await OwnsGitDirAsync().ConfigureAwait(false))
        {
            return false;
        }

        Git.Result? status = await StatusAsync().ConfigureAwait(false);
        if (status is null)
        {
            Kept("git failed");
            return false;
        }

        if (status.ExitCode is not 0)
        {
            Kept(status.Stderr.Trim());
            return false;
        }

        if (!HasChanges(status.Stdout, exempt))
        {
            return true;
        }

        Action = "kept";
        Reason = "changes";
        return false;
    }

    /// <summary>
    /// The removal: the launcher's own files first, then the worktree (the branch is checked out in
    /// it and cannot be deleted while it is), then the branch. Nothing is removed by force: a
    /// worktree that git refuses to remove stays, and the error is the reason; a branch that stays
    /// behind after its worktree is gone is a note, not a reason.
    /// </summary>
    // The processes the worker left behind cannot race the decision here: the worker's own process
    // group is signalled when the worker exits, before this decision (issue #116). A process that
    // leaves the group on its own is out of the launcher's reach either way.
    private async Task RemoveAsync()
    {
        // The same refusal once more, at the moment of deletion: a path turned dirty after the
        // first check still deletes nothing.
        if (UnsafeCopiedPath() is { } unsafePath)
        {
            KeptForUnsafeCopiedPath(unsafePath);
            return;
        }

        try
        {
            DeleteCopied();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Kept(ex.Message);
            return;
        }

        // The emptiness check again: what is left after the launcher's own files are gone must be
        // nothing at all.
        if (!await EmptyAsync(new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false))
        {
            return;
        }

        // The ignored files (the build output of the run) go with the worktree: they are not work.
        int ignored = await CountIgnoredAsync().ConfigureAwait(false);

        Git.Result? removed = await Git.RunResultAsync(_repo, "worktree", "remove", WorktreePath).ConfigureAwait(false);
        if (removed is null)
        {
            Kept("git failed");
            return;
        }

        if (removed.ExitCode is not 0)
        {
            // No --force: a worktree that still holds something stays where it is, and git says why.
            Kept(removed.Stderr.Trim());
            return;
        }

        // The branch goes only while it still points at the commit it was created from: a branch
        // that moved holds work of its own and stays.
        Git.Result? deleted = await Git.RunResultAsync(_repo, "update-ref", "-d", "refs/heads/" + Branch, BaseSha).ConfigureAwait(false);
        if (deleted?.ExitCode is not 0)
        {
            Note = $"branch kept: {EvidenceBlock.OneLine(deleted?.Stderr.Trim() ?? "git failed")}";
        }

        Action = "removed";
        Reason = ignored > 0
            ? $"empty (ignored files discarded: {ignored.ToString(CultureInfo.InvariantCulture)})"
            : "empty";
    }

    /// <summary>
    /// A worktree that stays: the action, the reason and the git error that stopped the decision.
    /// </summary>
    private void Kept(string why)
    {
        Action = "kept";
        Reason = "kept: " + EvidenceBlock.OneLine(why);
    }

    /// <summary>
    /// The worktree stays because a copied path is not a plain path inside it: the ordinary
    /// "changes" reason, and the note names the worktree-path check that failed — nothing was read
    /// through the offending path and nothing was deleted along it.
    /// </summary>
    private void KeptForUnsafeCopiedPath(string path)
    {
        Action = "kept";
        Reason = "changes";
        Note = $"worktree path check failed: '{path}' is not a plain path inside the worktree; nothing read through it, nothing deleted";
    }

    /// <summary>
    /// The git directory this worktree must belong to: the invoking repository's common git dir,
    /// then <c>worktrees/</c>, then the worktree's own name — the registration
    /// <c>git worktree add</c> created for this run. Both the common dir and the name are resolved
    /// the way git resolves them (canonical, symlinks expanded), so a repository behind a symlinked
    /// home still compares equal. Empty when the common git dir cannot be read: the check then
    /// fails.
    /// </summary>
    private async Task<string> ExpectedGitDirAsync()
    {
        string? common = await Git.RunAsync(_repo, "rev-parse", "--git-common-dir").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(common))
        {
            return string.Empty;
        }

        GateTrust.CanonicalPath canonical = GateTrust.Canonical(Path.GetFullPath(Path.Combine(_repo, common.Trim())));
        return canonical.Resolved
            ? Path.Combine(canonical.Path, "worktrees", Path.GetFileName(WorktreePath))
            : string.Empty;
    }

    /// <summary>
    /// Whether the worktree directory is still the one git registered for this run:
    /// <c>git -C &lt;WorktreePath&gt; rev-parse --absolute-git-dir</c> — a path with symlinks
    /// resolved — must name the invoking repository's own <c>worktrees/&lt;name&gt;</c> directory.
    /// A directory swapped for another checkout, another worktree of the same repository or a
    /// foreign repository names something else, and every further git call would read a tree this
    /// decision cannot judge. Runs before every git call of <see cref="AtBaseAsync"/> and
    /// <see cref="EmptyAsync"/>.
    /// </summary>
    private async Task<bool> OwnsGitDirAsync()
    {
        string expected = await ExpectedGitDirAsync().ConfigureAwait(false);
        Git.Result? actual = await Git.RunResultAsync(WorktreePath, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        string actualDir = actual is { ExitCode: 0 } ? actual.Stdout.Trim() : string.Empty;
        if (expected.Length > 0
            && actualDir.Length > 0
            && GateTrust.Canonical(actualDir) is { Resolved: true } resolved
            && resolved.Path.Equals(expected, StringComparison.Ordinal))
        {
            return true;
        }

        string why;
        if (expected.Length is 0)
        {
            why = "the common git dir cannot be read";
        }
        else if (actualDir.Length is 0)
        {
            why = "git failed in the worktree";
        }
        else
        {
            why = $"expected '{expected}', got '{actualDir}'";
        }

        KeptForGitDirCheck(why);
        return false;
    }

    /// <summary>
    /// The worktree stays because its git directory is not the run's own: the ordinary "changes"
    /// reason, and the note names the git-dir check that failed. Nothing was deleted and no further
    /// git call ran on the unverified tree.
    /// </summary>
    private void KeptForGitDirCheck(string why)
    {
        Action = "kept";
        Reason = "changes";
        Note = $"git dir check failed ({why}): nothing deleted, no further git run";
    }

    /// <summary>
    /// The first copied path whose way from the worktree down to the file is not clean, or null
    /// when every copied path is plain and stays inside the worktree. The folders
    /// <see cref="DeleteCopied"/> removes are components of these file paths, so the file walk
    /// covers them too.
    /// </summary>
    private string? UnsafeCopiedPath()
    {
        foreach (string relative in _copied.Select(f => f.Relative))
        {
            if (FirstLinkedComponent(WorktreePath, relative) is { } offender)
            {
                return offender;
            }
        }

        return null;
    }

    /// <summary>
    /// The first component of the way from <paramref name="root"/> down through
    /// <paramref name="relative"/> (the file included) that is a symlink or reparse point — or the
    /// full path itself when it lands outside <paramref name="root"/>. Null when the way is clean.
    /// A component that cannot be examined counts as linked: nothing is read through, or deleted
    /// along, a path the launcher cannot prove plain.
    /// </summary>
    private static string? FirstLinkedComponent(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);

        // The root itself is the first component of every way down: a root that is a symlink, or
        // whose canonical path is not the recorded one (a link on the way to it, or a path that
        // resolves elsewhere), makes the whole way untrustworthy, so the root is the offender.
        if (IsLinked(root)
            || GateTrust.Canonical(root) is not { Resolved: true } rootCanonical
            || !rootCanonical.Path.Equals(fullRoot, StringComparison.Ordinal))
        {
            return root;
        }

        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!GateTrust.IsAtOrUnder(full, fullRoot))
        {
            return full;
        }

        string current = fullRoot;
        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsLinked(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the path is a symlink or reparse point: the link target is read without following
    /// the link (it answers even when the target is missing), and the reparse-point attribute is
    /// the second opinion. An unexaminable path counts as a link.
    /// </summary>
    private static bool IsLinked(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null
                || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether the repository already has the branch.
    /// </summary>
    private static async Task<bool> BranchExistsAsync(string repoRoot, string branch)
    {
        Git.Result? result = await Git.RunResultAsync(repoRoot, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).ConfigureAwait(false);
        return result?.ExitCode is 0;
    }

    /// <summary>
    /// The commit the base ref names, resolved in the repository the launcher was called in. The
    /// error is empty when it resolved.
    /// </summary>
    private static async Task<(string? Sha, string Error)> ResolveBaseAsync(string repo, string baseRef)
    {
        // --end-of-options: a base that starts with a dash is a ref name like any other, never an
        // option of git.
        Git.Result? result = await Git.RunResultAsync(repo, "rev-parse", "--verify", "--end-of-options", baseRef + "^{commit}").ConfigureAwait(false);
        if (result?.ExitCode is not 0)
        {
            return (null, result?.Stderr.Trim() ?? "git could not be run");
        }

        string sha = result.Stdout.Trim();
        return sha.Length > 0 ? (sha, string.Empty) : (null, "git said nothing");
    }

    /// <summary>
    /// The paths the base commit tracks: the files of the worktree the launcher must not overwrite.
    /// </summary>
    private static async Task<HashSet<string>> TrackedAtAsync(string repo, string sha)
    {
        Git.Result? result = await Git.RunResultAsync(repo, "ls-tree", "-r", "-z", "--name-only", sha).ConfigureAwait(false);
        if (result?.ExitCode is not 0)
        {
            throw new LaunchException($"git could not read the base commit: {EvidenceBlock.OneLine(result?.Stderr.Trim() ?? "git could not be run")}");
        }

        HashSet<string> tracked = new(StringComparer.Ordinal);
        foreach (string path in result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            _ = tracked.Add(path);
        }

        return tracked;
    }

    /// <summary>
    /// The status of the worktree: every changed, deleted and untracked path, one per line. The
    /// paths are printed raw (no quoting), so they can be compared with the paths the launcher
    /// recorded.
    /// </summary>
    private async Task<Git.Result?> StatusAsync() =>
        await Git.RunResultAsync(WorktreePath, "-c", "core.quotePath=false", "status", "--porcelain", "--untracked-files=all").ConfigureAwait(false);

    /// <summary>
    /// The ignored files of the worktree (the build output of the run): they are counted, not kept.
    /// </summary>
    private async Task<int> CountIgnoredAsync()
    {
        Git.Result? result = await Git.RunResultAsync(WorktreePath, "-c", "core.quotePath=false", "status", "--porcelain", "--ignored", "--untracked-files=all").ConfigureAwait(false);
        if (result?.ExitCode is not 0)
        {
            return 0;
        }

        int n = 0;
        foreach (string line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("!!", StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// The paths the launcher copied in itself: the only ones the emptiness check lets past.
    /// </summary>
    private HashSet<string> Exempt() =>
        new(_copied.Select(f => f.Relative), StringComparer.Ordinal);

    /// <summary>
    /// Whether the status holds a change: every line names a path, and only the paths the launcher
    /// copied in itself are exempt — and only while they are untracked.
    /// </summary>
    private static bool HasChanges(string porcelain, IReadOnlySet<string> exempt)
    {
        foreach (string line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4)
            {
                return true;
            }

            // The status code and a space, then the path.
            string code = line[..2];
            string path = line[3..];
            if (code is "??" && exempt.Contains(path))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a file the launcher copied in still holds the content it was copied with: a file the
    /// worker changed is work, not setup.
    /// </summary>
    private bool SameContent(SetupFile file)
    {
        string destination = Path.Combine(WorktreePath, file.Relative);
        if (!File.Exists(destination) || !File.Exists(file.Source))
        {
            return false;
        }

        return File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(file.Source));
    }

    /// <summary>
    /// Deletes the files the launcher copied into the worktree and the folders they leave empty:
    /// the files first, then the folders from the deepest one up, and only when they are empty.
    /// </summary>
    private void DeleteCopied()
    {
        foreach (SetupFile file in _copied)
        {
            string path = Path.Combine(WorktreePath, file.Relative);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // A folder that holds more than the copied files stays: only the empty ones go.
        IEnumerable<string> folders = _copied
            .Select(f => Path.GetDirectoryName(f.Relative) ?? string.Empty)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(f => f.Length);
        foreach (string folder in folders)
        {
            string path = Path.Combine(WorktreePath, folder);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
    }

    /// <summary>
    /// The files of the setup the launcher copies into a worktree: the profiles, the project notes,
    /// the system notes and the per-provider code notes, each of them only when it exists.
    /// </summary>
    private static List<SetupFile> SetupFiles(string setup)
    {
        List<SetupFile> files = [];
        if (!Directory.Exists(setup))
        {
            return files;
        }

        foreach (string name in new[] { "profiles.json", "project.md" })
        {
            string from = Path.Combine(setup, name);
            if (File.Exists(from))
            {
                files.Add(new SetupFile(".lean-worker/" + name, from));
            }
        }

        foreach (string file in FileTree.FilesUnder(setup, "system"))
        {
            files.Add(new SetupFile(Relative(setup, file), file));
        }

        foreach (string file in Directory.EnumerateFiles(setup, "code-*.md").Order(StringComparer.Ordinal))
        {
            files.Add(new SetupFile(".lean-worker/" + Path.GetFileName(file), file));
        }

        return files;
    }

    /// <summary>
    /// The path of a file of the setup inside the worktree: the path of the setup, rooted at the
    /// worktree, with the separators git prints.
    /// </summary>
    private static string Relative(string setup, string file) =>
        ".lean-worker/" + Path.GetRelativePath(setup, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Copies the setup into the worktree and records every file it created: the launcher deletes
    /// exactly those files again when it removes the worktree. A file the base tracks is never
    /// overwritten — it is skipped, and nothing is recorded for it. A symlink on the way to a
    /// destination (the base may track one at the .lean-worker folder, the system folder or the
    /// file itself) refuses the copy before anything is written: copying through it would write
    /// outside the worktree.
    /// </summary>
    private static List<SetupFile> CopySetup(string setup, string worktree, IReadOnlySet<string> tracked)
    {
        List<SetupFile> copied = [];
        foreach (SetupFile file in SetupFiles(setup))
        {
            if (tracked.Contains(file.Relative))
            {
                continue;
            }

            if (FirstLinkedComponent(worktree, file.Relative) is { } linked)
            {
                throw new LaunchException($"the base tracks a symlink at '{linked}'");
            }

            string destination = Path.Combine(worktree, file.Relative);
            if (File.Exists(destination))
            {
                continue;
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.Source, destination, overwrite: false);
            copied.Add(file);
        }

        return copied;
    }
}
