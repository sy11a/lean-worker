// One gate round's outcome plus the chain state at that point. Exposed on LaunchRun.LastGate for the
// launcher to drive the chain loop.

namespace LeanWorker;

/// <summary>
/// One gate round's outcome plus the chain state at that point. Exposed on <c>LaunchRun.LastGate</c>
/// for the launcher to drive the chain loop. <c>Result</c> is the round's aggregate (the one result
/// for the single-command form); <c>Commands</c> is the commands form's per-command results, in
/// profile order — the commands that ran, so it is shorter than the spec list when the round ended
/// at an error — and null for the single-command form.
/// </summary>
internal sealed record GateOutcome(
    string Decision,
    Gate.GateResult Result,
    string RunDir,
    IReadOnlyList<int> Counts,
    decimal ChainCostUsd,
    string? StuckReason,
    IReadOnlyList<Gate.GateResult>? Commands = null);