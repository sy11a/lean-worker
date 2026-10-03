namespace LeanWorker;

internal sealed record Prepared(string Executable, List<string> Args, Dictionary<string, string?> Env, string CommandText, string? ScratchDirectory);