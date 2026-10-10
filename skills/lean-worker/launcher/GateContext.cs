// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
// built by the launcher from the previous round's GateOutcome. The frozen Spec is the round-1 GateSpec —
// the launcher uses it instead of re-reading profiles.json, so a worker cannot swap the gate command for
// the next round. The frozen TrustSnapshot, TrustPaths and FixedSources are round 1's before-worker trust
// snapshot, its fixed trust-path list and those paths' sources — every later round hashes against them, so
// a detached process cannot re-baseline the trust check by surviving into the next round. PostGateOutputs
// carries the previous round's post-gate re-hash of the declared outputs that are in the trusted set; the
// next round's baseline is round 1's snapshot with exactly those keys replaced.

namespace LeanWorker;

/// <summary>
/// What a gate round N+1 needs to know about the chain so far. Round 1 passes null; subsequent rounds are
/// built by the launcher from the previous round's <see cref="GateOutcome"/>. The frozen <c>Spec</c>
/// is the round-1 <see cref="GateSpec"/>: the launcher uses it instead of re-reading
/// <c>profiles.json</c>, so a worker cannot change the gate's command for the next round. The frozen
/// <c>TrustSnapshot</c>, <c>TrustPaths</c> and <c>FixedSources</c> are round 1's
/// before-worker trust snapshot, its fixed trust-path list and those paths' sources: every later round
/// compares against round 1's snapshot (before its worker, before its gate,
/// after its gate) instead of taking a fresh baseline that a tampering process could hide in, and the
/// report-path rule reads round 1's sources so a later round cannot re-derive them from a tree the
/// worker has already changed. <c>PostGateOutputs</c> is the one patch over that frozen baseline: the
/// declared gate outputs that are in the trusted set, re-hashed after the previous round's clean
/// after-gate check — the gate legitimately rewrote exactly those paths, so their keys carry the
/// post-gate values while everything else keeps round 1's value. The launcher always supplies the
/// trust fields when it continues a chain; they are optional only so a hand-built context can omit
/// them (no trust checks run then).
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
    IReadOnlyList<string>? TrustPaths = null,
    IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>>? FixedSources = null,
    GateTrust.Snapshot? PostGateOutputs = null);
