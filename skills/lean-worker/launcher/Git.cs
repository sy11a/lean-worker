using System.Diagnostics;

namespace LeanWorker;

internal static class Git
{
    /// <summary>
    /// How a git call ended: its exit code, its standard output and its standard error. Null when git could
    /// not be run at all (not installed) or did not finish in time.
    /// </summary>
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// A git call bound to one repository: the git dir it reads, the work tree it compares it
    /// with and the common git dir it takes the shared files from. All are named, so a worker
    /// that rewrote a ".git" file (the "gitdir:" pointer of a linked worktree), a "commondir"
    /// file or a config cannot move the call to a repository of its own.
    /// </summary>
    internal sealed record Pin(string GitDir, string WorkTree, string Common);

    /// <summary>
    /// The options every git call of the launcher carries, before the subcommand (where git reads
    /// them): the fsmonitor hook and the hooks path are switched off. A worker that changed the
    /// repository's own config (core.fsmonitor, core.hooksPath) or wrote a hook of its own would
    /// otherwise have the launcher's own git calls — and the operator's next session — run it.
    /// These options stop the fsmonitor and the hooks only: a clean filter the worker planted
    /// (filter.&lt;x&gt;.clean) still runs under `git status`, which is why a run whose git
    /// metadata changed skips the git-status pass altogether (LaunchRun.SnapshotChangedAsync).
    /// </summary>
    private static readonly string[] _safe = ["-c", "core.fsmonitor=", "-c", "core.hooksPath=/dev/null"];

    /// <summary>
    /// The global config of the operator, read as an empty file: a pinned call must not pick up a
    /// filter, a hooks path or an fsmonitor hook from outside the repository it is pinned to. The
    /// system config is switched off the same way (GIT_CONFIG_NOSYSTEM).
    /// </summary>
    private static readonly string _globalConfig = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    internal static async Task<string?> RunAsync(string dir, params string[] args) =>
        Out(await RunResultAsync(dir, args).ConfigureAwait(false));

    /// <summary>
    /// The standard output of a call that succeeded: null when the call did not run at all or when
    /// it failed.
    /// </summary>
    private static string? Out(Result? result)
    {
        if (result is null)
        {
            return null;
        }

        return result.ExitCode is 0 ? result.Stdout : null;
    }

    /// <summary>
    /// Runs git and reports how it ended, for the calls whose exit code carries the answer
    /// (<c>merge-base --is-ancestor</c>, <c>diff --quiet</c>). Every call times out after 60 seconds.
    /// </summary>
    internal static async Task<Result?> RunResultAsync(string dir, params string[] args)
    {
        ProcessStartInfo psi = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (string safe in _safe)
        {
            psi.ArgumentList.Add(safe);
        }
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return await StartAsync(psi).ConfigureAwait(false);
    }

    /// <summary>
    /// A git call pinned to the git dir, the work tree and the common git dir that were resolved
    /// before the worker ran, for the calls the launcher makes after the worker exited.
    ///
    /// Why the pin: the worker is untrusted, and every file it could write may point git somewhere
    /// else. A worker that rewrites the ".git" of the working tree (the "gitdir:" file of a linked
    /// worktree) moves the working tree to a git dir of its own — one whose config carries a clean
    /// filter, which then runs under the launcher's own `git status`, with the worker's code, after
    /// the worker is gone. Naming the git dir and the work tree (--git-dir, --work-tree) keeps the
    /// call on the repository the launcher started in, whatever the worker wrote since; the global
    /// and the system config are switched off as well, so no config outside that repository can add
    /// a filter either. The common git dir is pinned in the environment (GIT_COMMON_DIR) for the
    /// same reason: git reads "$GIT_DIR/commondir" in every git dir, a main ".git" included, so a
    /// worker that wrote one there pointed the call at a copy of the git dir of its own — and the
    /// common dir in the environment wins over that file for the files git takes from it (the
    /// config, the hooks, the attributes).
    ///
    /// The refs are not covered by the environment: git's ref store ignores GIT_COMMON_DIR and
    /// still follows a planted "commondir" for them. The refs are covered by the metadata hash
    /// instead (both commondir files are watched, so a planted one is a finding) and by the tamper
    /// gate: a run whose git metadata changed is out of scope and no decision over the repository
    /// is made for it (issue #114). The calls made before the worker ran stay as they are: there
    /// the repository is still the one the operator handed over.
    /// </summary>
    internal static async Task<string?> RunPinnedAsync(Pin pin, params string[] args) =>
        Out(await RunPinnedResultAsync(pin, args).ConfigureAwait(false));

    /// <summary>
    /// The same pinned call, reporting how it ended: its exit code, its standard output and its
    /// standard error.
    /// </summary>
    internal static async Task<Result?> RunPinnedResultAsync(Pin pin, params string[] args)
    {
        ProcessStartInfo psi = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--git-dir=" + pin.GitDir);
        psi.ArgumentList.Add("--work-tree=" + pin.WorkTree);
        foreach (string safe in _safe)
        {
            psi.ArgumentList.Add(safe);
        }
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_CONFIG_GLOBAL"] = _globalConfig;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        // The common git dir of the pin: it is the directory git takes the shared files (the
        // config, the hooks, the attributes) from, and git reads "$GIT_DIR/commondir" in every git
        // dir — a main ".git" included. A worker that wrote one there moved the call to a copy of
        // the git dir of its own, whose config carries the clean filter the call then runs. The
        // common dir named in the environment wins over that file for those files (issue #114).
        // The refs are not covered by it: git's ref store ignores GIT_COMMON_DIR and still follows
        // a planted "commondir" for them. The refs are covered by the metadata hash instead (both
        // commondir files are watched, so a planted one is a finding) and by the tamper gate: a
        // run whose git metadata changed is out of scope and no decision over the repository is
        // made for it. A pin always names a common dir (there is no pin without one), so the
        // variable is never left out: a call without it would read the common dir git resolves
        // for itself, which is the one the worker may have redirected.
        psi.Environment["GIT_COMMON_DIR"] = pin.Common;

        return await StartAsync(psi).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs git and reports how it ended: its exit code, its standard output and its standard
    /// error. Null when git could not be run at all (not installed) or did not finish in time.
    /// </summary>
    private static async Task<Result?> StartAsync(ProcessStartInfo psi)
    {
        try
        {
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            Task<string> output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> error = p.StandardError.ReadToEndAsync(CancellationToken.None);
            if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
            return new Result(p.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // git is not installed
    }
}
