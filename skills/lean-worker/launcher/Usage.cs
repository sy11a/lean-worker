using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// One API call's token usage. Output excludes reasoning; both are billed at the output price.
/// </summary>
internal sealed record Usage(string Id, string Model, long Input, long Output, long Reasoning,
                             long CacheRead, long CacheWrite5m, long CacheWrite1h)
{
    public long Context => Input + CacheRead + CacheWrite5m + CacheWrite1h;

    /// <summary>
    /// The token totals of a run's calls as the summary and the status file record them: input,
    /// cache write (both lifetimes), cache read, output (with reasoning) and <paramref name="thinking"/>.
    /// </summary>
    public static JsonObject Totals(List<Usage> calls, long thinking)
    {
        return new JsonObject
        {
            ["input"] = calls.Sum(c => c.Input),
            ["cache_write"] = calls.Sum(c => c.CacheWrite5m + c.CacheWrite1h),
            ["cache_read"] = calls.Sum(c => c.CacheRead),
            ["output"] = calls.Sum(c => c.Output + c.Reasoning),
            ["thinking"] = thinking,
        };
    }
}