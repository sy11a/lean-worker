// The workspace root: where the worker (and the gate) run, anchored on the task's own location
// instead of the directory the launcher was started in. A delegating session hand-types the
// launcher command; a wrong working directory (a typo'd cd, a konsole that fell back to $HOME on a
// missing --workdir) must not silently become the worker's project directory — opencode reads it
// from PWD — or the anchor the write scope is resolved against.
//
// With --worktree the workspace is a worktree of the repository instead of the repository itself:
// the launcher creates it, copies the setup into it and removes it again when the run leaves it
// empty. The run's own bookkeeping (the run directory, the ledger) stays in the repository the
// launcher was called in.

namespace LeanWorker;

internal static class Workspace
{
    /// <summary>
    /// The workspace root: the git working tree containing <paramref name="anchorDir"/> (the
    /// task's own location) when there is one, else <paramref name="cwd"/> unchanged (the
    /// historical anchor, kept bit-for-bit so a task staged in a runs-root outside any checkout
    /// behaves exactly as before). When the task IS inside a working tree the tree root wins, so
    /// the workspace follows the work, never the shell the launcher was typed into; a relative
    /// <c>--write-scope</c> is documented "relative to the git root", and this anchor makes that
    /// true regardless of the launcher's own working directory.
    /// </summary>
    public static async Task<string> ResolveAsync(string anchorDir, string cwd)
    {
        string? root = await RepoToken.RootAsync(anchorDir).ConfigureAwait(false);
        return root ?? cwd;
    }

    /// <summary>
    /// The worktree a run owns (--worktree): created in the parent folder of the repository, named
    /// after the repository and the run, with the setup of the repository (<paramref name="setup"/>)
    /// copied into it. The worker, the write scope and the gate work in it, so it is the workspace
    /// root of the run. A later round of the gate chain reuses the worktree the first round created
    /// (<paramref name="reuse"/>).
    /// </summary>
    public static async Task<WorktreeRun> WorktreeAsync(Options o, string repoRoot, string setup, bool reuse)
    {
        (string branch, string name, string baseRef) = await WorktreeFactsAsync(o, repoRoot).ConfigureAwait(false);
        return reuse
            ? await WorktreeRun.ReuseAsync(repoRoot, branch, baseRef, name, setup).ConfigureAwait(false)
            : await WorktreeRun.CreateAsync(repoRoot, branch, baseRef, name, setup).ConfigureAwait(false);
    }

    /// <summary>
    /// The facts a worktree is built from: the branch it checks out, the name of the run (its
    /// folder is named after it) and the ref it is branched from.
    /// </summary>
    private static async Task<(string Branch, string Name, string Base)> WorktreeFactsAsync(Options o, string repoRoot)
    {
        string branch = o.Worktree ?? throw new LaunchException("--worktree needs a branch");
        string name = o.Name ?? throw new LaunchException("--worktree needs --name <name>: the worktree's folder is named after the run");
        string baseRef = await WorktreeRun.BaseRefAsync(repoRoot, o.WorktreeBase).ConfigureAwait(false);
        return (branch, name, baseRef);
    }

    /// <summary>
    /// A relative task / continue-from path that does not exist under <paramref name="cwd"/> is
    /// retried against the git working tree containing it, so a repo-root-relative
    /// <c>.lean-worker/inbox/.../task.md</c> resolves from any subdirectory of the checkout.
    /// Returns the input unchanged when nothing matches, so the caller's own not-found error names
    /// exactly what was given.
    /// </summary>
    public static async Task<string> ResolveRelativeAsync(string path, string cwd)
    {
        if (Path.IsPathRooted(path) || File.Exists(path) || Directory.Exists(path))
        {
            return path;
        }

        string? root = await RepoToken.RootAsync(cwd).ConfigureAwait(false);
        if (root is null)
        {
            return path;
        }

        string candidate = Path.Combine(root, path);
        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : path;
    }
}
