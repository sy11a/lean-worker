namespace LeanWorker.Tests;

/// <summary>
/// Process-global state for one Commands test: the user config, cache and data homes and the Claude config dir
/// point into a fresh temp directory (so no real ~/.config, ~/.cache or ~/.claude file is read), a stub bin
/// directory leads PATH, and Run captures Console.Out. Everything is restored on dispose. Use only in the
/// "launcher-process-state" collection.
/// </summary>
internal sealed class CommandSandbox : IDisposable
{
    private static readonly string[] _vars = ["XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "CLAUDE_CONFIG_DIR", "PATH"];
    private readonly Dictionary<string, string?> _old = new(StringComparer.Ordinal);

    public CommandSandbox()
    {
        Root = Directory.CreateTempSubdirectory("lw-cmd").FullName;
        RunsRoot = Path.Combine(Root, "runs");
        ConfigHome = Path.Combine(Root, "config");
        QuotaCacheDir = Path.Combine(Root, "cache", "lean-worker");
        ClaudeConfigDir = Path.Combine(Root, "claude");
        BinDir = Path.Combine(Root, "bin");
        foreach (string dir in new[] { RunsRoot, ConfigHome, QuotaCacheDir, ClaudeConfigDir, BinDir })
        {
            _ = Directory.CreateDirectory(dir);
        }

        foreach (string v in _vars)
        {
            _old[v] = Environment.GetEnvironmentVariable(v);
        }

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigHome);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(Root, "cache"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", ClaudeConfigDir);
        Environment.SetEnvironmentVariable("PATH", BinDir + Path.PathSeparator + _old["PATH"]);
    }

    public string Root { get; }

    public string RunsRoot { get; }

    public string ConfigHome { get; }

    public string QuotaCacheDir { get; }

    public string ClaudeConfigDir { get; }

    public string BinDir { get; }

    /// <summary>
    /// What the last Run wrote to Console.Out.
    /// </summary>
    public string Output { get; private set; } = string.Empty;

    public string[] Lines => Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Runs a subcommand with Console.Out captured into Output (also when it throws) and returns its exit code.
    /// </summary>
    public int Run(Func<int> command)
    {
        TextWriter oldOut = Console.Out;
        using StringWriter captured = new();
        Console.SetOut(captured);
        try
        {
            return command();
        }
        finally
        {
            Console.SetOut(oldOut);
            Output = captured.ToString();
        }
    }

    /// <summary>
    /// PATH holds the stub bin directory alone, so no real executable is found.
    /// </summary>
    public void PathIsBinDirOnly() => Environment.SetEnvironmentVariable("PATH", BinDir);

    /// <summary>
    /// Writes an executable shell script into the stub bin directory.
    /// </summary>
    public void Stub(string name, string script)
    {
        string path = Path.Combine(BinDir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _old)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must not fail the test that already finished.
        }
    }
}
