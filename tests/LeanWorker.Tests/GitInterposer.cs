namespace LeanWorker.Tests;

/// <summary>
/// A <c>git</c> in front of the real one on PATH, for the states that only exist between two git calls of the
/// launcher (a branch that moves after git made it, a git call that fails at one point). It hands every call to
/// the real git and runs the shell snippets of the test around the calls made with <c>-C &lt;repository&gt;</c>:
/// <c>before</c> runs ahead of the real git (it can <c>exit</c> to replace the call), <c>after</c> runs behind it
/// (the real exit code is kept). The snippets see <c>$real</c> (the real git), <c>$repo</c> and the arguments
/// (<c>$*</c>); they match the call they want with a <c>case " $* " in *" subcommand "*)</c>. Calls of other
/// repositories pass untouched, so a test running beside it is not disturbed. It changes PATH of the process:
/// use it only in the "launcher-process-state" collection and dispose it before the test calls git again.
/// </summary>
internal sealed class GitInterposer : IDisposable
{
    private readonly EnvScope _path;

    public GitInterposer(string folder, string repo, string before = ":", string after = ":")
    {
        string real = Launcher.FindOnPath("git") ?? throw new InvalidOperationException("git is not on PATH");
        string script = string.Join('\n',
            "#!/bin/sh",
            $"real='{real}'",
            $"repo='{repo}'",
            "case \" $* \" in",
            "*\" -C $repo \"*)",
            before,
            ";;",
            "esac",
            "\"$real\" \"$@\"",
            "rc=$?",
            "case \" $* \" in",
            "*\" -C $repo \"*)",
            after,
            ";;",
            "esac",
            "exit $rc",
            string.Empty);
        string file = Path.Combine(folder, "git");
        File.WriteAllText(file, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _path = new EnvScope(("PATH", folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")));
    }

    public void Dispose() => _path.Dispose();
}
