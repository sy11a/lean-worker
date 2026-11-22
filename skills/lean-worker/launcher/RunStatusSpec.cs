namespace LeanWorker;

/// <summary>
/// What the status file records about the run itself, fixed at its start.
/// </summary>
internal sealed record RunStatusSpec(
    string RunDir,
    string Name,
    string ChainId,
    int Round,
    string? Profile,
    string Runtime,
    string Provider,
    string Model,
    decimal BudgetUsd,
    decimal? WrapUpUsd,
    int TimeoutMinutes,
    DateTimeOffset StartedAt,
    string? Worktree);
