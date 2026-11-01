using System.Diagnostics;

namespace LeanWorker;

internal static class Git
{
    /// <summary>
    /// How a git call ended: its exit code, its standard output and its standard error. Null when git could
    /// not be run at all (not installed) or did not finish in time.
    /// </summary>
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    internal static async Task<string?> RunAsync(string dir, params string[] args)
    {
        Result? result = await RunResultAsync(dir, args).ConfigureAwait(false);
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
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

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
