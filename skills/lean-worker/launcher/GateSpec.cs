// The gate spec: what the runner needs. Resolved from a profile's "gate" object (or absent → null, meaning
// "no gate"). The validation matches the gate profile grammar in the task brief; failures throw LaunchException
// so the user sees a precise, named message.
//
// User-supplied `reportFromLastLine` patterns must include at least one capture group. The runner reads
// group 1 first; if that is empty it falls back to the first named group (excluding group 0), so a regex
// that uses a named capture for its only group — like the default `^sarif: (?<report>.+)$` — still works.
//
// `gate.outputs` is the operator's explicit declaration of the repo-relative paths the gate writes (its
// report and any other artifacts). The launcher used to guess which argv entries were pure gate outputs —
// argv position and execute bits — so a gate could rewrite its own report file without tripping the trust
// check. Guessing cannot see through a wrapper such as `env VAR=1 sh gate.sh`: the report is neither
// argv[0] nor argv[1] and carries no execute bit, so the heuristic is dropped. No argv entry is excluded
// from a trust comparison by default; a gate that writes a file named in its argv needs that file declared
// here. A gate whose report is not named in its argv — a lint gate's default report under artifacts/, for
// example — needs no gate.outputs at all.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal sealed partial record GateSpec(List<string> Command, Regex ReportFromLastLine, string CountPath, int FeedbackMaxChars, int TimeoutMinutes, int MaxRounds, decimal? MaxTotalUsd, List<string> Env, List<string>? Trust = null, List<string>? Outputs = null, List<string>? Warnings = null)
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
        // The gate's report path cannot be checked against the trusted inputs here: the profile only
        // carries the reportFromLastLine regex, and the path itself arrives on the gate's stdout at
        // run time. The trusted-input check for it is therefore run-time only (LaunchRun's trust
        // checks via GateTrust.ReportPathViolation, after the gate runs and the path is resolved).
        // Declared gate.outputs entries, in contrast, are known up front and validated against the
        // fixed trusted inputs when round 1 collects them, before the worker starts.
        Regex reportFromLastLine = ReadReportRegex(gate);
        string countPath = ReadCountPath(gate);
        int feedbackMaxChars = ReadPositiveInt(gate, "feedbackMaxChars", 8000, "profile gate.feedbackMaxChars");
        int timeoutMinutes = ReadPositiveInt(gate, "timeoutMinutes", 30, "profile gate.timeoutMinutes");
        int maxRounds = ReadPositiveInt(gate, "maxRounds", 5, "profile gate.maxRounds");
        decimal? maxTotalUsd = ReadMaxTotalUsd(gate);
        List<string> env = ReadEnv(gate);
        List<string> trust = ReadTrust(gate);
        List<string> outputs = ReadOutputs(gate);

        return new GateSpec(command, reportFromLastLine, countPath, feedbackMaxChars, timeoutMinutes, maxRounds, maxTotalUsd, env, trust, outputs);
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

            // The directory refusal is an operator rule on later arguments; argv[0] without a directory
            // separator is a bare command the runner resolves on PATH at start (it may resolve to a
            // directory like `dotnet/` when the working directory happens to contain one).
            if (i is not 0 || HasDirectorySeparator(s))
            {
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
            }

            command.Add(s);
        }

        return command;
    }

    private static bool HasDirectorySeparator(string s)
    {
        return s.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || s.Contains(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal);
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

    /// <summary>
    /// Reads <c>gate.trust</c>: an optional list of repo-relative paths or globs the operator wants
    /// added to the trust set (the same paths the gate chain hashes). Entries are kept for
    /// <see cref="GateTrust.CollectChainPaths"/> to resolve relative to the git root (or the gate working
    /// directory when not in git) at start time, exactly like the gate command's arguments.
    /// </summary>
    private static List<string> ReadTrust(JsonObject gate)
    {
        if (!gate.TryGetPropertyValue("trust", out JsonNode? n) || n is null)
        {
            return [];
        }

        if (n is not JsonArray arr)
        {
            throw new LaunchException("profile gate.trust must be an array of repo-relative paths or globs");
        }

        List<string> trust = new(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? item = arr[i];
            // A non-empty string is required so the matcher can rely on the prefix-relative shape
            // (an empty entry would match every directory above the root, which is not what the
            // operator asked for).
            if (item is not JsonValue v || !v.TryGetValue(out string? s) || string.IsNullOrEmpty(s))
            {
                throw new LaunchException($"profile gate.trust[{i.ToString(CultureInfo.InvariantCulture)}] must be a non-empty string");
            }

            string normalized = s.Replace('\\', '/');
            // Absolute entries (any flavour of root) defeat the point of repo-relative paths: they
            // cannot cross the repo boundary on their own, but a worker that follows an absolute
            // path has a free channel for changing arbitrary system files. Reject up-front so the
            // error is at profile-load time, not during a trust check.
            if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
            {
                throw new LaunchException($"profile gate.trust[{i.ToString(CultureInfo.InvariantCulture)}] must be a relative path: {s}");
            }

            // No parent traversal: ".." would let an operator accidentally (or via a malformed
            // entry) trust files outside the repo, defeating the boundary.
            foreach (string segment in normalized.Split('/'))
            {
                if (segment is "..")
                {
                    throw new LaunchException($"profile gate.trust[{i.ToString(CultureInfo.InvariantCulture)}] must not contain a '..' segment: {s}");
                }
            }

            trust.Add(normalized);
        }

        return trust;
    }

    /// <summary>
    /// Reads <c>gate.outputs</c>: an optional array of repo-relative paths (relative to the git root, or
    /// the working directory when not in git) that the gate writes — its report and any other artifacts.
    /// This declaration replaces the dropped runnable-argv heuristic: whether an argv entry is a pure
    /// output cannot be inferred from its position or its execute bit (a wrapper such as
    /// <c>env VAR=1 sh gate.sh</c> hides both the script and the report), so nothing is guessed and the
    /// operator names the outputs instead. Entries must be non-empty strings, relative, without a
    /// <c>..</c> segment and without glob characters (a declared output names exactly one path; a glob
    /// would leave it ambiguous which files the gate may rewrite). A gate whose report is not named in
    /// its argv — a lint gate's default report under <c>artifacts/</c>, for example — needs no
    /// <c>gate.outputs</c>: an untrusted path needs no exclusion.
    /// </summary>
    private static List<string> ReadOutputs(JsonObject gate)
    {
        if (!gate.TryGetPropertyValue("outputs", out JsonNode? n) || n is null)
        {
            return [];
        }

        if (n is not JsonArray arr)
        {
            throw new LaunchException("profile gate.outputs must be an array of repo-relative paths");
        }

        List<string> outputs = new(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? item = arr[i];
            if (item is not JsonValue v || !v.TryGetValue(out string? s) || string.IsNullOrEmpty(s))
            {
                throw new LaunchException($"profile gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must be a non-empty string");
            }

            string normalized = s.Replace('\\', '/');
            // Absolute entries would let a profile aim the gate at a file outside the repo; the trust
            // set is repo-relative for the same reason.
            if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
            {
                throw new LaunchException($"profile gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must be a relative path: {s}");
            }

            // No parent traversal: ".." would let a declared output reach outside the repo boundary.
            foreach (string segment in normalized.Split('/'))
            {
                if (segment is "..")
                {
                    throw new LaunchException($"profile gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must not contain a '..' segment: {s}");
                }
            }

            // No glob characters: the exclusion of a declared output from the after-gate comparison is
            // only safe when it names exactly the path the gate writes (the same rule that keeps
            // gate.trust globs out of the report-path decision).
            if (normalized.IndexOfAny(['*', '?']) >= 0)
            {
                throw new LaunchException($"profile gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must not contain glob characters: {s}");
            }

            outputs.Add(normalized);
        }

        return outputs;
    }
}