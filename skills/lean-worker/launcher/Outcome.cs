namespace LeanWorker;

/// <summary>
/// What the stream said besides usage: the report, the outcome and bookkeeping.
/// </summary>
internal sealed class Outcome
{
    public bool HasResult;
    public bool IsError;
    public string Report = string.Empty;
    public string? SessionId;
    public string? Subtype;
    public string? TerminalReason;
    public decimal? ReportedCost;
    public long Turns;
    public long Denials;
    public long Thinking;
    public string? LastMessageId;

    /// <summary>
    /// The provider's stop reason for the last step ("length" when the output limit cut it off).
    /// </summary>
    public string? LastStepReason;

    /// <summary>
    /// The last step's output tokens. Zero when the runtime does not report them, and zero for a
    /// step that produced nothing.
    /// </summary>
    public long LastStepOutput;

    public readonly List<string> Texts = [];

    /// <summary>
    /// Whether the provider cut the last step off at its output limit and that step produced
    /// nothing: the run was truncated, not finished, and must not be reported as a success. The
    /// stop reason is the runtime's own vocabulary ("length" for the output limit); "no output"
    /// is no final text or no output tokens.
    /// </summary>
    public bool Truncated =>
        (LastStepReason ?? TerminalReason) is "length" && (Report.Length is 0 || LastStepOutput is 0);
}