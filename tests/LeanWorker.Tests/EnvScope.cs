namespace LeanWorker.Tests;

/// <summary>
/// Sets process environment variables for as long as it lives (a null value removes the variable) and puts the old
/// values back on dispose. Process-global: use only in the "launcher-process-state" collection.
/// </summary>
internal sealed class EnvScope : IDisposable
{
    /// <summary>
    /// The variables the launcher reads to tell who owns a run or whether the writer is on. A test that
    /// launches clears them first (<see cref="Clean"/>), so what the machine's own session sets never leaks in.
    /// </summary>
    public static readonly string[] OwnerVariables =
    [
        "LEAN_WORKER_OWNER", "LEAN_WORKER_RUN_ID", "LEAN_WORKER_RUN_DIR", "CLAUDE_CODE_SESSION_ID", "CLAUDE_PID",
        "OPENCODE_SESSION_ID", "OPENCODE_PID", "MSG_RELAY_NAME", "LEAN_WORKER_STATUS",
    ];

    private readonly Dictionary<string, string?> _old = new(StringComparer.Ordinal);

    public EnvScope(params (string Name, string? Value)[] variables)
    {
        foreach ((string name, string? value) in variables)
        {
            if (!_old.ContainsKey(name))
            {
                _old[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// A scope with every <see cref="OwnerVariables"/> entry removed, then the given ones set.
    /// </summary>
    public static EnvScope Clean(params (string Name, string? Value)[] variables) =>
        new([.. OwnerVariables.Select(name => (name, (string?)null)), .. variables]);

    public void Dispose()
    {
        foreach ((string name, string? value) in _old)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
