// The gate spec: what the runner needs. Resolved from a profile's "gate" object (or absent → null, meaning
// "no gate"). The validation matches the gate profile grammar in the task brief; failures throw LaunchException
// so the user sees a precise, named message.
//
// User-supplied `reportFromLastLine` patterns must include at least one capture group. The runner reads
// group 1 first; if that is empty it falls back to the first named group (excluding group 0), so a regex
// that uses a named capture for its only group — like the default `^sarif: (?<report>.+)$` — still works.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal sealed partial record GateSpec(List<string> Command, Regex ReportFromLastLine, string CountPath, int FeedbackMaxChars, int TimeoutMinutes, int MaxRounds, decimal? MaxTotalUsd, List<string> Env)
{
    // A count-path segment: a JSON property name optionally followed by one non-negative index in brackets.
    // `[0-9]` (not `\d`, which matches non-ASCII digits) and a leading-zero rule so the runtime walk can
    // resolve the index; the 9-digit cap bounds the value a profile can ask the runner to allocate.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\[(?:0|[1-9][0-9]{0,8})\])?$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CountPathSegmentRegex();

    [GeneratedRegex(@"^sarif: (?<report>.+)$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DefaultReportFromLastLineRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\*?$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnvNamePatternRegex();

    /// <summary>
    /// Returns null when the profile has no gate key. Any other value (a string, number, array, object
    /// other than the gate spec) throws so a mis-spelled gate key fails fast with a precise message.
    /// Uses the current process's working directory as the gate's working directory for the
    /// directory-refusal check on <c>gate.command</c> entries.
    /// </summary>
    public static GateSpec? FromProfile(JsonObject? profile) => FromProfile(profile, Directory.GetCurrentDirectory());

    /// <summary>
    /// Returns null when the profile has no gate key. Any other value (a string, number, array, object
    /// other than the gate spec) throws so a mis-spelled gate key fails fast with a precise message.
    /// </summary>
    /// <param name="profile">The profile JSON object (or null for "no profile").</param>
    /// <param name="gateWorkingDirectory">Directory the gate process will be started in
    /// (<c>ProcessStartInfo.WorkingDirectory</c>); argv entries are resolved against this directory for the
    /// directory-refusal check.</param>
    public static GateSpec? FromProfile(JsonObject? profile, string gateWorkingDirectory)
    {
        JsonNode? node = profile?["gate"];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject gate)
        {
            throw new LaunchException("profile gate must be an object");
        }

        List<string> command = ReadCommand(gate, gateWorkingDirectory);
        Regex reportFromLastLine = ReadReportRegex(gate);
        string countPath = ReadCountPath(gate);
        int feedbackMaxChars = ReadPositiveInt(gate, "feedbackMaxChars", 8000, "profile gate.feedbackMaxChars");
        int timeoutMinutes = ReadPositiveInt(gate, "timeoutMinutes", 30, "profile gate.timeoutMinutes");
        int maxRounds = ReadPositiveInt(gate, "maxRounds", 5, "profile gate.maxRounds");
        decimal? maxTotalUsd = ReadMaxTotalUsd(gate);
        List<string> env = ReadEnv(gate);

        return new GateSpec(command, reportFromLastLine, countPath, feedbackMaxChars, timeoutMinutes, maxRounds, maxTotalUsd, env);
    }

    private static List<string> ReadCommand(JsonObject gate, string gateWorkingDirectory)
    {
        if (gate["command"] is not JsonArray arr || arr.Count is 0)
        {
            throw new LaunchException("profile gate.command must be a non-empty array of strings");
        }

        List<string> command = new(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? node = arr[i];
            if (node is not JsonValue v || !v.TryGetValue(out string? s) || string.IsNullOrEmpty(s))
            {
                throw new LaunchException("profile gate.command must be a non-empty array of non-empty strings");
            }

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(s)
                    ? Path.GetFullPath(s)
                    : Path.GetFullPath(Path.Combine(gateWorkingDirectory, s));
            }
            catch (ArgumentException ex)
            {
                throw new LaunchException($"profile gate.command[{i.ToString(CultureInfo.InvariantCulture)}] is invalid: {ex.Message}");
            }

            if (Directory.Exists(resolved))
            {
                throw new LaunchException($"profile gate.command[{i.ToString(CultureInfo.InvariantCulture)}] is a directory: {resolved}");
            }

            command.Add(s);
        }

        return command;
    }

    private static Regex ReadReportRegex(JsonObject gate)
    {
        const string label = "profile gate.reportFromLastLine";
        if (!gate.TryGetPropertyValue("reportFromLastLine", out JsonNode? rn) || rn is null)
        {
            return DefaultReportFromLastLineRegex();
        }

        if (rn is not JsonValue rv || !rv.TryGetValue(out string? pattern) || string.IsNullOrEmpty(pattern))
        {
            throw new LaunchException($"{label} must be a regex string");
        }

        Regex compiled;
        try
        {
            compiled = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
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
        if (!gate.TryGetPropertyValue("countPath", out JsonNode? cn) || cn is null)
        {
            return "runs[0].results";
        }

        if (cn is not JsonValue cv || !cv.TryGetValue(out string? path) || string.IsNullOrEmpty(path))
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
        if (!gate.TryGetPropertyValue("maxTotalUsd", out JsonNode? n) || n is null)
        {
            return null;
        }

        // Reject non-numeric values: true / "abc" / "5" — only a numeric JsonValue is parsed.
        if (n is not JsonValue v || !v.TryGetValue(out decimal parsed))
        {
            throw new LaunchException("profile gate.maxTotalUsd must be a number");
        }

        if (parsed <= 0m)
        {
            throw new LaunchException("profile gate.maxTotalUsd must be a number greater than 0");
        }

        return parsed;
    }

    private static List<string> ReadEnv(JsonObject gate)
    {
        if (!gate.TryGetPropertyValue("env", out JsonNode? n) || n is null)
        {
            return [];
        }

        if (n is not JsonArray arr)
        {
            throw new LaunchException("profile gate.env must be an array of names or PREFIX* patterns");
        }

        Regex pattern = EnvNamePatternRegex();
        List<string> env = new(arr.Count);
        foreach (JsonNode? item in arr)
        {
            if (item is not JsonValue v || !v.TryGetValue(out string? s) || string.IsNullOrEmpty(s) || !pattern.IsMatch(s))
            {
                throw new LaunchException("profile gate.env entry must match [A-Za-z_][A-Za-z0-9_]*[*]?");
            }

            env.Add(s);
        }

        return env;
    }
}