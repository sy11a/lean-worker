// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
// built by the launcher from the previous round's GateOutcome. The frozen Spec is the round-1 GateSpec —
// the launcher uses it instead of re-reading profiles.json, so a worker cannot swap the gate command for
// the next round.

namespace LeanWorker;

/// <summary>
/// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
/// built by the launcher from the previous round's <see cref="GateOutcome"/>. The frozen <c>Spec</c>
/// is the round-1 <see cref="GateSpec"/>: the launcher uses it instead of re-reading
/// <c>profiles.json</c>, so a worker cannot change the gate's command for the next round.
/// </summary>
internal sealed record GateContext(
    string ChainId,
    int Round,
    List<int> Counts,
    decimal ChainCostUsd,
    string OriginalTask,
    string OriginalName,
    Gate.GateResult Feedback,
    GateSpec Spec);