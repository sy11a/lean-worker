// Pure chain-decision logic, factored out so the launcher can test it without running a process.

using System.Globalization;

namespace LeanWorker;

/// <summary>
/// Pure chain-decision logic: "clean" / "error" pass through the gate outcome; "findings" is "stuck" when
/// the count did not decrease in two consecutive rounds, when the round number reached <c>MaxRounds</c>, or
/// when the summed worker cost is at least <c>MaxTotalUsd</c>; otherwise "continue". Factored out so it
/// can be tested without running a process.
/// </summary>
internal static class GateChain
{
    public const string Clean = "clean";
    public const string Continue = "continue";
    public const string Stuck = "stuck";
    public const string Error = "error";

    /// <summary>
    /// Returns the chain decision for one round. "clean" and "error" pass through the gate outcome unchanged;
    /// "findings" is "stuck" when the count did not decrease in two consecutive rounds, when the round number
    /// reached <paramref name="maxRounds"/>, or when the summed worker cost is at least
    /// <paramref name="maxTotalUsd"/>; otherwise "continue".
    /// </summary>
    public static string Decide(Gate.GateResult result, IReadOnlyList<int> countsIncludingThis, int round, int maxRounds, decimal chainCostIncludingThis, decimal maxTotalUsd)
    {
        return result.Outcome switch
        {
            Error => Error,
            Clean => Clean,
            _ => StuckReason(countsIncludingThis, round, maxRounds, chainCostIncludingThis, maxTotalUsd) is not null ? Stuck : Continue,
        };
    }

    /// <summary>
    /// The reason string shown in the "stuck" result block (no decrease | max rounds | cost cap $X).
    /// Returns null when not stuck. The checks are evaluated in the same order as <see cref="Decide"/>.
    /// </summary>
    public static string? StuckReason(IReadOnlyList<int> countsIncludingThis, int round, int maxRounds, decimal chainCostIncludingThis, decimal maxTotalUsd)
    {
        if (countsIncludingThis.Count >= 3)
        {
            int n = countsIncludingThis.Count;
            if (countsIncludingThis[n - 1] >= countsIncludingThis[n - 2] && countsIncludingThis[n - 2] >= countsIncludingThis[n - 3])
            {
                return "no decrease in 2 rounds";
            }
        }

        if (round >= maxRounds)
        {
            return "max rounds";
        }

        return maxTotalUsd > 0m && chainCostIncludingThis >= maxTotalUsd
            ? $"cost cap ${maxTotalUsd.ToString("0.####", CultureInfo.InvariantCulture)}"
            : null;
    }
}