// One gate run's outcome plus the chain state at that point. Exposed on LaunchRun.Gate for the launcher
// to drive the chain loop.

namespace LeanWorker;

/// <summary>
/// One gate run's outcome plus the chain state at that point. Exposed on <c>LaunchRun.Gate</c> for the
/// launcher to drive the chain loop.
/// </summary>
internal sealed record GateOutcome(
    string Decision,
    Gate.GateResult Result,
    string RunDir,
    IReadOnlyList<int> Counts,
    decimal ChainCostUsd,
    string? StuckReason);