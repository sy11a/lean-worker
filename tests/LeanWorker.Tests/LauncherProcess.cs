using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The launcher as a child process of the test (dotnet LeanWorker.dll), for what only a real process shows: signals
/// and the exit codes of Program. Its environment is edited on the start info, never in this process, and the run
/// index points at a folder of the test (LEAN_WORKER_STATE_DIR).
/// </summary>
internal static class LauncherProcess
{
    /// <summary>
    /// What a finished child printed and how it ended.
    /// </summary>
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// The dotnet host that runs the launcher's dll: the one that started this build, else the one next to this
    /// runtime, else the one on PATH.
    /// </summary>
    public static string DotnetHost()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is { Length: > 0 } && File.Exists(host))
        {
            return host;
        }

        string beside = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        return File.Exists(beside) ? beside : "dotnet";
    }

    /// <summary>
    /// A start info for the launcher with <paramref name="args"/>, in <paramref name="cwd"/>: the machine's owner
    /// and kill-switch variables removed, the run index in <paramref name="stateDir"/>, then <paramref name="env"/>
    /// applied (a null value removes the variable).
    /// </summary>
    public static ProcessStartInfo StartInfo(string[] args, string cwd, string stateDir, params (string Name, string? Value)[] env)
    {
        ProcessStartInfo psi = new(DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LeanWorker.dll"));
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (string name in EnvScope.OwnerVariables)
        {
            _ = psi.Environment.Remove(name);
        }

        psi.Environment[StateDirScope.Variable] = stateDir;
        foreach ((string name, string? value) in env)
        {
            if (value is null)
            {
                _ = psi.Environment.Remove(name);
            }
            else
            {
                psi.Environment[name] = value;
            }
        }

        return psi;
    }

    /// <summary>
    /// Runs the launcher to its end under a hard time limit (it is killed when the limit passes).
    /// </summary>
    public static async Task<Result> RunAsync(ProcessStartInfo psi, TimeSpan limit)
    {
        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(limit, TimeProvider.System, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the launcher did not end within " + limit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        }

        return new Result(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Sends <paramref name="signal"/> (TERM, HUP, KILL ...) to a process, or, with a negative pid, to its group, with the
    /// shell's own kill.
    /// </summary>
    public static async Task SignalAsync(string signal, int pid)
    {
        ProcessStartInfo psi = new("sh") { UseShellExecute = false, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("kill -s " + signal + " -- " + pid.ToString(CultureInfo.InvariantCulture) + " 2>/dev/null; exit 0");
        using Process kill = Process.Start(psi)!;
        await kill.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes an executable shell script named <paramref name="name"/> into a new temp folder and returns the folder.
    /// </summary>
    public static async Task<string> StubAsync(TempDirs dirs, string name, string body)
    {
        string dir = dirs.Create("lw-proc-stub");
        string path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }
}
