using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The worker's PWD must be the launcher's own working directory, never an inherited or foreign one:
/// opencode reads its project directory from PWD, so a caller with a foreign PWD would run the worker
/// against the wrong repo.
/// </summary>
[Collection("launcher-process-state")]
public class WorkerPwdTests
{
    [Fact]
    public void StartInfo_overrides_a_foreign_PWD_and_drops_OLDPWD()
    {
        string cwd = Directory.GetCurrentDirectory();
        string foreignDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Dictionary<string, string?> env = new(StringComparer.Ordinal)
        {
            ["PWD"] = foreignDir,
            ["OLDPWD"] = foreignDir,
        };
        Prepared prep = new(Executable: "echo", Args: [], Env: env, CommandText: "echo", ScratchDirectory: null);

        System.Diagnostics.ProcessStartInfo psi = Launcher.StartInfo(prep);

        Assert.Equal(cwd, psi.WorkingDirectory);
        Assert.Equal(cwd, psi.Environment["PWD"]);
        Assert.False(psi.Environment.ContainsKey("OLDPWD"));
    }

    /// <summary>
    /// Resolves symlinks in a path the same way the shell's "pwd -P" does, so captured and expected
    /// paths compare equal even if the OS temp directory is itself a symlink.
    /// </summary>
    private static async Task<string> ResolvedPathAsync(string path)
    {
        System.Diagnostics.ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", "cd \"$1\" && pwd -P", "sh", path },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return output.Trim();
    }

    /// <summary>
    /// Writes a python3 stub so the captured $PWD is exactly what the launcher passed in the child's
    /// environment: unlike /bin/sh, python does not re-derive PWD from getcwd() on startup, which
    /// would otherwise hide a wrong inherited PWD.
    /// </summary>
    private static string PythonStub(string pwdCapture, string pwdPCapture)
    {
        string dir = Directory.CreateTempSubdirectory("lw-stub").FullName;
        string path = Path.Combine(dir, "opencode");
        string script = $"""
            #!/usr/bin/env python3
            import os, subprocess, sys
            sys.stdin.read()
            open({Py(pwdCapture)}, "w").write(os.environ.get("PWD", ""))
            open({Py(pwdPCapture)}, "w").write(subprocess.run(["pwd", "-P"], capture_output=True, text=True).stdout)
            """ + "\nprint('{\"type\":\"text\",\"sessionID\":\"s1\",\"part\":{\"messageID\":\"m1\",\"text\":\"DONE\"}}')\n";
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    private static string Py(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool Python3IsOnPath()
    {
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, "python3")));
    }

    [Fact]
    public async Task Worker_PWD_is_the_launchers_working_directory_not_an_inherited_oneAsync()
    {
        Assert.SkipUnless(Python3IsOnPath(), "requires python3 on PATH");

        string runsRoot = Directory.CreateTempSubdirectory("lw-runs").FullName;
        string task = Path.Combine(runsRoot, "task.md");
        await File.WriteAllTextAsync(task, "do nothing", TestContext.Current.CancellationToken);
        string capture = Directory.CreateTempSubdirectory("lw-capture").FullName;
        string pwdCapture = Path.Combine(capture, "pwd.txt");
        string pwdPCapture = Path.Combine(capture, "pwd-p.txt");
        string workDir = Directory.CreateTempSubdirectory("lw-workdir").FullName;
        string foreignDir = Directory.CreateTempSubdirectory("lw-foreign").FullName;
        string stubDir = PythonStub(pwdCapture, pwdPCapture);

        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string? oldPwd = Environment.GetEnvironmentVariable("PWD");
        string oldCwd = Directory.GetCurrentDirectory();
        await using StringWriter outWriter = new();
        TextWriter oldOut = Console.Out;
        try
        {
            try
            {
                Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + oldPath);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-test-key");
                Environment.SetEnvironmentVariable("PWD", foreignDir);
                Directory.SetCurrentDirectory(workDir);
                Console.SetOut(outWriter);
                Options o = new() { TaskFile = task, RunsRoot = runsRoot, Runtime = "opencode", Model = "anthropic/claude-haiku-4-5", Mode = "auto", Name = Guid.NewGuid().ToString("N") };
                int code = await Launcher.RunAsync(o);
                Assert.Equal(0, code);
            }
            finally
            {
                Console.SetOut(oldOut);
                Directory.SetCurrentDirectory(oldCwd);
                Environment.SetEnvironmentVariable("PATH", oldPath);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
                Environment.SetEnvironmentVariable("PWD", oldPwd);
            }

            string resolvedWorkDir = await ResolvedPathAsync(workDir);
            string recordedPwd = await ResolvedPathAsync((await File.ReadAllTextAsync(pwdCapture, TestContext.Current.CancellationToken)).Trim());

            Assert.Equal(resolvedWorkDir, recordedPwd);
        }
        finally
        {
            Directory.Delete(stubDir, recursive: true);
            Directory.Delete(runsRoot, recursive: true);
            Directory.Delete(capture, recursive: true);
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(foreignDir, recursive: true);
        }
    }
}
