namespace LeanWorker;

/// <summary>
/// What `status` reads from its surroundings besides /proc and the clock (those are the reader's):
/// the environment variables that tell who the caller is (a lookup function) and the folder of the
/// per-user index. A test passes its own; <see cref="None"/> has no variables and no index.
/// </summary>
internal sealed record StatusEnvironment(Func<string, string?> Variable, string? IndexFolder)
{
    /// <summary>
    /// No environment at all: no caller identity, no index.
    /// </summary>
    public static StatusEnvironment None { get; } = new(_ => null, IndexFolder: null);

    /// <summary>
    /// This process's environment and the index folder it names.
    /// </summary>
    public static StatusEnvironment Host() => new(name => HostVariable(name), StatusIndex.Folder(name => HostVariable(name)));

    private static string? HostVariable(string name) => Environment.GetEnvironmentVariable(name);
}
