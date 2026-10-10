// The gate spec: what the runner needs. Resolved from a profile's "gate" object (or absent → null, meaning
// "no gate"). The validation matches the gate profile grammar in the task brief; failures throw LaunchException
// so the user sees a precise, named message.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal sealed partial record GateSpec(List<string> Command, Regex ReportFromLastLine, string CountPath, int FeedbackMaxChars, int TimeoutMinutes, int MaxRounds, decimal? MaxTotalUsd)
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\[\d+\])?$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CountPathSegmentRegex();

    [GeneratedRegex(@"^sarif: (?<report>.+)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DefaultReportFromLastLineRegex();

    /// <summary>
    /// Returns null when the profile has no gate key.
    /// </summary>
    public static GateSpec? FromProfile(JsonObject? profile)
    {
        if (profile?["gate"] is not JsonObject gate)
        {
            return null;
        }

        List<string> command = ReadCommand(gate);
        Regex reportFromLastLine = ReadReportRegex(gate);
        string countPath = ReadCountPath(gate);
        int feedbackMaxChars = ReadPositiveInt(gate, "feedbackMaxChars", 8000, "profile gate.feedbackMaxChars");
        int timeoutMinutes = ReadPositiveInt(gate, "timeoutMinutes", 30, "profile gate.timeoutMinutes");
        int maxRounds = ReadPositiveInt(gate, "maxRounds", 5, "profile gate.maxRounds");
        decimal? maxTotalUsd = ReadMaxTotalUsd(gate);

        return new GateSpec(command, reportFromLastLine, countPath, feedbackMaxChars, timeoutMinutes, maxRounds, maxTotalUsd);
    }

    private static List<string> ReadCommand(JsonObject gate)
    {
        if (gate["command"] is not JsonArray arr || arr.Count is 0)
        {
            throw new LaunchException("profile gate.command must be a non-empty array of strings");
        }

        List<string> command = new(arr.Count);
        foreach (JsonNode? node in arr)
        {
            if (node is not JsonValue v || !v.TryGetValue(out string? s) || string.IsNullOrEmpty(s))
            {
                throw new LaunchException("profile gate.command must be a non-empty array of non-empty strings");
            }

            command.Add(s);
        }

        return command;
    }

    private static Regex ReadReportRegex(JsonObject gate)
    {
        const string label = "profile gate.reportFromLastLine";
        if (!gate.TryGetPropertyValue("reportFromLastLine", out JsonNode? rn) || rn is null || rn is not JsonValue rv)
        {
            return DefaultReportFromLastLineRegex();
        }

        if (!rv.TryGetValue(out string? pattern) || string.IsNullOrEmpty(pattern))
        {
            throw new LaunchException($"{label} must be a regex string");
        }

        Regex compiled;
        try
        {
            compiled = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new LaunchException($"{label} is not a valid regex: {ex.Message}");
        }

        // GetGroupNames includes the whole-match group 0 plus any other named or numbered capture groups;
        // a regex with at least one capture group (named or not) yields more than one entry.
        if (compiled.GetGroupNames().Length <= 1)
        {
            throw new LaunchException($"{label} must have at least one capture group");
        }

        return compiled;
    }

    private static string ReadCountPath(JsonObject gate)
    {
        if (!gate.TryGetPropertyValue("countPath", out JsonNode? cn) || cn is null || cn is not JsonValue cv)
        {
            return "runs[0].results";
        }

        if (!cv.TryGetValue(out string? path) || string.IsNullOrEmpty(path))
        {
            throw new LaunchException("profile gate.countPath must be a non-empty string");
        }

        Regex segmentPattern = CountPathSegmentRegex();
        foreach (string segment in path.Split('.'))
        {
            if (!segmentPattern.IsMatch(segment))
            {
                throw new LaunchException($"profile gate.countPath has an invalid segment: '{segment}'");
            }
        }

        return path;
    }

    private static int ReadPositiveInt(JsonObject gate, string key, int defaultValue, string label)
    {
        if (!gate.TryGetPropertyValue(key, out JsonNode? n) || n is null)
        {
            return defaultValue;
        }

        if (n is JsonValue v && v.TryGetValue(out long asLong) && asLong > 0 && asLong <= int.MaxValue)
        {
            return (int)asLong;
        }

        throw new LaunchException($"{label} must be a positive integer");
    }

    private static decimal? ReadMaxTotalUsd(JsonObject gate)
    {
        decimal? parsed = Json.Dec(gate, "maxTotalUsd");
        if (parsed is null)
        {
            return null;
        }

        if (parsed.Value <= 0m)
        {
            throw new LaunchException("profile gate.maxTotalUsd must be a decimal greater than 0");
        }

        return parsed;
    }
}
