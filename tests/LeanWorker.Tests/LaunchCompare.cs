using System.Text.Json.Nodes;

namespace LeanWorker.Tests;

/// <summary>
/// What two launches have to share to be "the same run": a summary or ledger row without what differs between any
/// two runs (the time and the duration), its paths and durations normalised the way the golden tests do, and
/// optionally without one note (the note a failed status write leaves).
/// </summary>
internal static class LaunchCompare
{
    public static string Comparable(string root, JsonObject row, string? dropNote = null)
    {
        JsonObject copy = row.DeepClone().AsObject();
        _ = copy.Remove("timestamp");
        _ = copy.Remove("duration_ms");
        JsonNode[] notes = [.. copy["notes"]!.AsArray().Where(n => !Mentions(n, dropNote)).Select(n => n!.DeepClone())];
        JsonNode[] evidence = [.. copy["evidence_lines"]!.AsArray().Where(l => !Mentions(l, dropNote)).Select(l => (JsonNode)RunAsyncGolden.Normalize(l!.GetValue<string>(), root))];
        copy["notes"] = new JsonArray(notes);
        copy["evidence_lines"] = new JsonArray(evidence);
        return RunAsyncGolden.Normalize(copy.ToJsonString(), root);
    }

    /// <summary>
    /// The printed block with its lines that mention <paramref name="dropNote"/> left out, normalised.
    /// </summary>
    public static string Block(string root, string stdout, string? dropNote = null)
    {
        string[] lines = stdout.Split('\n');
        return RunAsyncGolden.Normalize(string.Join('\n', lines.Where(l => dropNote is null || !l.Contains(dropNote, StringComparison.Ordinal))), root);
    }

    private static bool Mentions(JsonNode? node, string? text) =>
        text is not null && node?.GetValue<string>().Contains(text, StringComparison.Ordinal) is true;
}
