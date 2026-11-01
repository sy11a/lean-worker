using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Json
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions _lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Parses JSON that may contain comments and trailing commas (opencode.jsonc, hand-edited prices).
    /// </summary>
    public static JsonNode ParseLenient(string text) =>
        JsonNode.Parse(text, documentOptions: _lenient) ?? throw new JsonException("empty document");

    public static JsonObject? TryParseObject(string line)
    {
        if (line.Length is 0 || line[0] is not '{')
        {
            return null;
        }

        try { return JsonNode.Parse(line) as JsonObject; } catch (JsonException) { return null; }
    }

    /// <summary>
    /// Deep merge: objects merge key by key, everything else (arrays included) is replaced.
    /// </summary>
    public static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach ((string? key, JsonNode? value) in source)
        {
            if (value is JsonObject src && target[key] is JsonObject dst)
            {
                MergeInto(dst, src);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    public static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static decimal? Dec(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out decimal d))
        {
            return d;
        }

        return v.TryGetValue(out string? s) && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    public static bool Bool(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    public static long Num(JsonNode? n)
    {
        if (n is not JsonValue v)
        {
            return 0;
        }

        if (v.TryGetValue(out long l))
        {
            return l;
        }

        // A value built in memory keeps its own type: an int-backed one does not answer a long
        // request (a value parsed from text answers either).
        if (v.TryGetValue(out int i))
        {
            return i;
        }

        return v.TryGetValue(out double d) ? (long)d : 0;
    }

    public static List<string>? StrList(JsonObject? o, string key) =>
        o?[key] is JsonArray arr ? arr.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList() : null;
}