using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// The owner of a run as the launcher captured it at launch (docs/research/live-status/README.md,
/// section 4.4): the keys a reader matches against (<see cref="Keys"/>), the session keys a nested
/// launcher inherited and records without matching them (<see cref="InheritedKeys"/>), which rule
/// set the owner (<see cref="Source"/>), the owner's runtime, its relay name (display only) and the
/// run this launcher runs inside (<see cref="ParentRun"/>, from <c>LEAN_WORKER_RUN_ID</c>).
/// </summary>
internal sealed record RunOwner(
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> InheritedKeys,
    string Source,
    string? Runtime,
    string? RelayName,
    string? ParentRun)
{
    /// <summary>
    /// No owner at all: no option, no environment value.
    /// </summary>
    public static RunOwner None { get; } = new([], [], OwnerKeys.SourceNone, Runtime: null, RelayName: null, ParentRun: null);

    /// <summary>
    /// The <c>owner</c> object of status.json.
    /// </summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["keys"] = Array(Keys),
            ["inherited_keys"] = Array(InheritedKeys),
            ["source"] = Source,
            ["runtime"] = Runtime,
            ["relay_name"] = RelayName,
        };
    }

    /// <summary>
    /// A JSON array of strings.
    /// </summary>
    internal static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(k => (JsonNode?)k)]);
}
