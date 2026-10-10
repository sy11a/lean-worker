// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
// built by the launcher from the previous round's GateOutcome.

namespace LeanWorker;

/// <summary>
/// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
/// built by the launcher from the previous round's <see cref="GateOutcome"/>.
/// </summary>
internal sealed record GateContext(
    string ChainId,
    int Round,
    List<int> Counts,
    decimal ChainCostUsd,
    string OriginalTask,
    string OriginalName,
    Gate.GateResult Feedback);