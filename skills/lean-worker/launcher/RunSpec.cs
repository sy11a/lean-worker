namespace LeanWorker;

/// <summary>
/// Everything a runtime needs to start one worker, resolved from options, profile and defaults.
/// </summary>
internal sealed record RunSpec(
    string RunDir, string Provider, string Model, string Effort, string? Variant,
    List<string> Tools, List<string> Allowed, List<string> Denied, decimal Budget, bool WrapUp, string PermissionMode,
    string? McpConfig, string? SystemFile, bool ReplaceSystemPrompt, string Mode, string CacheTtl,
    bool KeepClaudeMd, bool KeepMemory, bool KeepHooks, bool KeepUserEnv, string? ClaudeSettings, Provider ProviderInfo);