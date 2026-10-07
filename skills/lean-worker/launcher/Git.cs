using System.Diagnostics;

namespace LeanWorker;

internal static class Git
{
    internal static async Task<string?> RunAsync(string dir, params string[] args)
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
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
            if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode is 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // git is not installed
    }
}