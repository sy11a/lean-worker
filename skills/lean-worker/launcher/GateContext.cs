// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
// built by the launcher from the previous round's GateOutcome. The frozen Spec is the round-1 GateSpec —
// the launcher uses it instead of re-reading profiles.json, so a worker cannot swap the gate command for
// the next round. The frozen TrustSnapshot and TrustPaths are round 1's before-worker trust snapshot and
// its fixed trust-path list — every later round hashes against them, so a detached process cannot
// re-baseline the trust check by surviving into the next round.

namespace LeanWorker;

/// <summary>
/// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
/// built by the launcher from the previous round's <see cref="GateOutcome"/>. The frozen <c>Spec</c>
/// is the round-1 <see cref="GateSpec"/>: the launcher uses it instead of re-reading
/// <c>profiles.json</c>, so a worker cannot change the gate's command for the next round. The frozen
/// <c>TrustSnapshot</c> and <c>TrustPaths</c> are round 1's before-worker trust snapshot and its fixed
/// trust-path list: every later round compares against round 1's snapshot (before its worker, before
/// its gate, after its gate) instead of taking a fresh baseline that a tampering process could hide in.
/// The launcher always supplies both when it continues a chain; they are optional only so a
/// hand-built context can omit them (no trust checks run then).
/// </summary>
internal sealed record GateContext(
    string ChainId,
    int Round,
    List<int> Counts,
    decimal ChainCostUsd,
    string OriginalTask,
    string OriginalName,
    Gate.GateResult Feedback,
    GateSpec Spec,
    GateTrust.Snapshot? TrustSnapshot = null,
    IReadOnlyList<string>? TrustPaths = null);
