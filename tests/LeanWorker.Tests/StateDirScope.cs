namespace LeanWorker.Tests;

/// <summary>
/// Points the launcher's per-user run index at a folder of this test for as long as it lives:
/// LEAN_WORKER_STATE_DIR names a fresh temp directory (the index is <c>&lt;it&gt;/runs</c>), and the old value comes
/// back on dispose. Every test that launches (Launcher.RunAsync, LaunchRun, a started launcher process) holds
/// one, so no pointer file reaches the developer's own ~/.local/state/lean-worker. The variable is process-global:
/// use only in the "launcher-process-state" collection, whose tests run one at a time.
/// </summary>
internal sealed class StateDirScope : IDisposable
{
    public const string Variable = "LEAN_WORKER_STATE_DIR";

    private readonly string? _old;
    private readonly bool _owned;

    /// <summary>
    /// The scope over <paramref name="root"/>, or over a new temp directory (deleted on dispose) when it is null.
    /// </summary>
    public StateDirScope(string? root = null)
    {
        _owned = root is null;
        Root = root ?? Directory.CreateTempSubdirectory("lw-state").FullName;
        _old = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, Root);
    }

    /// <summary>
    /// The value of LEAN_WORKER_STATE_DIR while the scope lives.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// The index folder the launcher writes its pointers to.
    /// </summary>
    public string Index => Path.Combine(Root, "runs");

    /// <summary>
    /// The pointer files written so far, by name (none while the folder does not exist).
    /// </summary>
    public string[] Pointers() =>
        Directory.Exists(Index) ? [.. Directory.GetFiles(Index, "*.json").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)] : [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, _old);
        if (!_owned)
        {
            return;
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
