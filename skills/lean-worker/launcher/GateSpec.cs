// The gate spec: what the runner needs. Resolved from a profile's "gate" object (or absent → null, meaning
// "no gate"). The validation matches the gate profile grammar in the task brief; failures throw LaunchException
// so the user sees a precise, named message.
//
// A gate is one command ("gate": {"command": [...]}) or several ("gate": {"commands": [{...}, ...]}). The
// single-command form keeps this record exactly as it has always been (Name null); the commands form resolves
// to one record per entry, its Name the entry's "name" key. The chain-level keys (caps, trust, env, the PATH
// freeze) belong to the gate itself and are copied into every element, so a chain of specs is a list of
// self-contained records and nothing downstream has to pair them with a shared half.
//
// User-supplied `reportFromLastLine` patterns must include at least one capture group. The runner reads
// group 1 first; if that is empty it falls back to the first named group (excluding group 0), so a regex
// that uses a named capture for its only group — like the default `^sarif: (?<report>.+)$` — still works.
//
// `gate.outputs` is the operator's explicit declaration of the paths the gate writes (its
// report and any other artifacts). The launcher used to guess which argv entries were pure gate outputs —
// argv position and execute bits — so a gate could rewrite its own report file without tripping the trust
// check. Guessing cannot see through a wrapper such as `env VAR=1 sh gate.sh`: the report is neither
// argv[0] nor argv[1] and carries no execute bit, so the heuristic is dropped. No argv entry is excluded
// from a trust comparison by default; a gate that writes a file named in its argv needs that file declared
// here. A gate whose report is not named in its argv — a lint gate's default report under artifacts/, for
// example — needs no gate.outputs at all.
//
// `ResolvedExecutable` is the launcher's one resolution of the gate's argv[0] (round 1, before the worker
// runs): the absolute path the trust check hashes as TrustSource.Executable and Gate.BuildStartInfo starts
// as FileName. The frozen spec carries it into every round of the chain, so the gate runner never searches
// PATH again at gate time; it is null only in a spec no launcher has resolved.
//
// `GatePath` is the same kind of one-time freeze for the gate's PATH (round 1, before the worker runs): the
// launcher's PATH with only the absolute entries kept (deduped, order kept — the GateTrust header's rule),
// handed to the gate as its PATH in every round and watched by the listing trust check (names plus per-entry
// metadata). It is null only in a spec no launcher has resolved: an all-relative launcher PATH is refused as
// a launch error (exit 2) rather than frozen as an empty value, which would resolve bare command names in
// the gate's current directory.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeanWorker;

internal sealed partial record GateSpec(List<string> Command, Regex ReportFromLastLine, string CountPath, int FeedbackMaxChars, int TimeoutMinutes, int MaxRounds, decimal? MaxTotalUsd, List<string> Env, List<string>? Trust = null, List<string>? Outputs = null, List<string>? Warnings = null, string? ResolvedExecutable = null, string? GatePath = null, string? Name = null)
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

    // A command entry's name: lowercase, digits and dashes, at most 32 chars. It lands in file names
    // (gate-<name>.log) and section headings, so it must be readable and path-safe as-is.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandNamePatternRegex();

    /// <summary>
    /// Returns null when the profile has no gate key; otherwise every command the gate runs: one
    /// spec for the single <c>gate.command</c> form (its <c>Name</c> null), one per entry for the
    /// <c>gate.commands</c> form. Any other value (a string, number, array) throws so a mis-spelled
    /// gate key fails fast with a precise message.
    /// </summary>
    /// <param name="profile">The profile JSON object (or null for "no profile").</param>
    /// <param name="gateWorkingDirectory">Directory the gate process will be started in
    /// (<c>ProcessStartInfo.WorkingDirectory</c>); argv entries are resolved against this directory for the
    /// directory-refusal check.</param>
    public static List<GateSpec>? FromProfileAll(JsonObject? profile, string gateWorkingDirectory)
    {
        JsonObject? gate = GateObject(profile);
        if (gate is null)
        {
            return null;
        }

        return gate["commands"] is null
            ? [FromProfileObject(gate, gateWorkingDirectory)]
            : FromProfileCommands(gate, gateWorkingDirectory);
    }

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
        JsonObject? gate = GateObject(profile);
        return gate is null ? null : FromProfileObject(gate, gateWorkingDirectory);
    }

    /// <summary>
    /// The profile's <c>gate</c> object, or null when the profile has no gate key. Any other value
    /// (a string, number, array) throws so a mis-spelled gate key fails fast with a precise message.
    /// </summary>
    private static JsonObject? GateObject(JsonObject? profile)
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

        return gate;
    }

    /// <summary>
    /// The chain-level keys every command of a gate shares: the feedback cap, the default timeout,
    /// the round and cost caps, the extra env and the trust list. The commands form copies these
    /// into every spec; the single-command form puts them on its one spec.
    /// </summary>
    private sealed record SharedKeys(int FeedbackMaxChars, int TimeoutMinutes, int MaxRounds, decimal? MaxTotalUsd, List<string> Env, List<string> Trust);

    private static SharedKeys ReadSharedKeys(JsonObject gate)
    {
        return new SharedKeys(
            ReadPositiveInt(gate, "feedbackMaxChars", 8000, "profile gate.feedbackMaxChars"),
            ReadPositiveInt(gate, "timeoutMinutes", 30, "profile gate.timeoutMinutes"),
            ReadPositiveInt(gate, "maxRounds", 5, "profile gate.maxRounds"),
            ReadMaxTotalUsd(gate),
            ReadEnv(gate),
            ReadTrust(gate));
    }

    /// <summary>
    /// The single-command form: one spec, <c>Name</c> null, read from the gate-level keys exactly as
    /// before the <c>commands</c> form existed (readers label their errors "profile gate.*" so the
    /// messages stay byte-identical).
    /// </summary>
    private static GateSpec FromProfileObject(JsonObject gate, string gateWorkingDirectory)
    {
        List<string> command = ReadCommand(gate, gateWorkingDirectory, "profile gate");
        // The gate's report path cannot be checked against the trusted inputs here: the profile only
        // carries the reportFromLastLine regex, and the path itself arrives on the gate's stdout at
        // run time. The trusted-input check for it is therefore run-time only (LaunchRun's trust
        // checks via GateTrust.ReportPathViolation, after the gate runs and the path is resolved).
        // Declared gate.outputs entries, in contrast, are known up front and validated against the
        // fixed trusted inputs when round 1 collects them, before the worker starts.
        Regex reportFromLastLine = ReadReportRegex(gate, "profile gate");
        string countPath = ReadCountPath(gate, "profile gate");
        List<string> outputs = ReadOutputs(gate, "profile gate");
        SharedKeys shared = ReadSharedKeys(gate);

        return new GateSpec(command, reportFromLastLine, countPath, shared.FeedbackMaxChars, shared.TimeoutMinutes, shared.MaxRounds, shared.MaxTotalUsd, shared.Env, shared.Trust, outputs);
    }

    /// <summary>
    /// The <c>gate.commands</c> form: the shared chain-level keys read once from the gate object, the
    /// per-command keys from each entry, the shared values copied into every spec. The shared-only and
    /// command-only keys are kept apart by <see cref="RefuseSharedKeys"/> and
    /// <see cref="RefuseGateLevelCommandKeys"/> so a key on the wrong level is a named refusal, not a
    /// silently ignored setting.
    /// </summary>
    private static List<GateSpec> FromProfileCommands(JsonObject gate, string gateWorkingDirectory)
    {
        if (gate["command"] is not null)
        {
            throw new LaunchException("profile gate cannot have both command and commands");
        }

        RefuseGateLevelCommandKeys(gate);
        SharedKeys shared = ReadSharedKeys(gate);

        List<JsonObject> entries = ReadCommands(gate);
        HashSet<string> names = new(StringComparer.Ordinal);
        List<GateSpec> specs = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            specs.Add(ReadCommandSpec(entries[i], i, gateWorkingDirectory, shared, names));
        }

        return specs;
    }

    private static GateSpec ReadCommandSpec(JsonObject entry, int index, string gateWorkingDirectory, SharedKeys shared, HashSet<string> names)
    {
        string label = $"profile gate.commands[{index.ToString(CultureInfo.InvariantCulture)}]";
        RefuseSharedKeys(entry, label);
        string name = ReadName(entry, label, names);
        List<string> command = ReadCommand(entry, gateWorkingDirectory, label);
        Regex reportFromLastLine = ReadReportRegex(entry, label);
        string countPath = ReadCountPath(entry, label);
        List<string> outputs = ReadOutputs(entry, label);
        int timeoutMinutes = ReadPositiveInt(entry, "timeoutMinutes", shared.TimeoutMinutes, $"{label}.timeoutMinutes");
        return new GateSpec(command, reportFromLastLine, countPath, shared.FeedbackMaxChars, timeoutMinutes, shared.MaxRounds, shared.MaxTotalUsd, shared.Env, shared.Trust, outputs, Name: name);
    }

    private static List<JsonObject> ReadCommands(JsonObject gate)
    {
        if (gate["commands"] is not JsonArray arr || arr.Count is 0)
        {
            throw new LaunchException("profile gate.commands must be a non-empty array of objects");
        }

        List<JsonObject> entries = new(arr.Count);
        foreach (JsonNode? item in arr)
        {
            if (item is not JsonObject entry)
            {
                throw new LaunchException("profile gate.commands must be a non-empty array of objects");
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static string ReadName(JsonObject entry, string label, HashSet<string> names)
    {
        if (!IsNonEmptyString(entry["name"], out string? name))
        {
            throw new LaunchException($"{label}.name is required and must be a non-empty string");
        }

        if (!CommandNamePatternRegex().IsMatch(name))
        {
            throw new LaunchException($"{label}.name must match ^[a-z][a-z0-9-]{{0,31}}$: '{name}'");
        }

        if (!names.Add(name))
        {
            throw new LaunchException($"{label}.name '{name}' is already used by an earlier command");
        }

        return name;
    }

    private static void RefuseSharedKeys(JsonObject entry, string label)
    {
        string[] sharedKeys = ["maxRounds", "maxTotalUsd", "trust", "env", "feedbackMaxChars", "commands"];
        foreach (string key in sharedKeys)
        {
            if (entry[key] is not null)
            {
                throw new LaunchException($"{label}.{key} is set on gate, not on a command");
            }
        }
    }

    private static void RefuseGateLevelCommandKeys(JsonObject gate)
    {
        string[] commandKeys = ["outputs", "reportFromLastLine", "countPath"];
        foreach (string key in commandKeys)
        {
            if (gate[key] is not null)
            {
                throw new LaunchException($"profile gate.{key} cannot be set when gate.commands is; it belongs on a command");
            }
        }
    }

    private static List<string> ReadCommand(JsonObject holder, string gateWorkingDirectory, string label)
    {
        if (holder["command"] is not JsonArray arr || arr.Count is 0)
        {
            throw new LaunchException($"{label}.command must be a non-empty array of strings");
        }

        List<string> command = new(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (!IsNonEmptyString(arr[i], out string? s))
            {
                throw new LaunchException($"{label}.command must be a non-empty array of non-empty strings");
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
                    throw new LaunchException($"{label}.command[{i.ToString(CultureInfo.InvariantCulture)}] is invalid: {ex.Message}");
                }

                if (Directory.Exists(resolved))
                {
                    throw new LaunchException($"{label}.command[{i.ToString(CultureInfo.InvariantCulture)}] is a directory: {resolved}");
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

    /// <summary>
    /// Whether <paramref name="node"/> is a JSON string that is neither null nor empty — the shape
    /// every string-list reader (command argv, names, trust and output entries) requires of its
    /// items. The caller keeps its own precise refusal message.
    /// </summary>
    private static bool IsNonEmptyString(JsonNode? node, [NotNullWhen(true)] out string? value)
    {
        if (node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s))
        {
            value = s;
            return true;
        }

        value = null;
        return false;
    }

    private static Regex ReadReportRegex(JsonObject holder, string label)
    {
        if (!holder.TryGetPropertyValue("reportFromLastLine", out JsonNode? rn) || rn is null)
        {
            return DefaultReportFromLastLineRegex();
        }

        if (rn is not JsonValue rv || !rv.TryGetValue(out string? pattern) || string.IsNullOrEmpty(pattern))
        {
            throw new LaunchException($"{label}.reportFromLastLine must be a regex string");
        }

        Regex compiled;
        try
        {
            compiled = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new LaunchException($"{label}.reportFromLastLine is not a valid regex: {ex.Message}");
        }

        // GetGroupNames includes the whole-match group 0 plus any other named or numbered capture groups;
        // a regex with at least one capture group (named or not) yields more than one entry.
        if (compiled.GetGroupNames().Length <= 1)
        {
            throw new LaunchException($"{label}.reportFromLastLine must have at least one capture group");
        }

        return compiled;
    }

    private static string ReadCountPath(JsonObject holder, string label)
    {
        if (!holder.TryGetPropertyValue("countPath", out JsonNode? cn) || cn is null)
        {
            return "runs[0].results";
        }

        if (cn is not JsonValue cv || !cv.TryGetValue(out string? path) || string.IsNullOrEmpty(path))
        {
            throw new LaunchException($"{label}.countPath must be a non-empty string");
        }

        Regex segmentPattern = CountPathSegmentRegex();
        foreach (string segment in path.Split('.'))
        {
            if (!segmentPattern.IsMatch(segment))
            {
                throw new LaunchException($"{label}.countPath has an invalid segment: '{segment}'");
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
            if (!IsNonEmptyString(item, out string? s))
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
    /// Reads <c>gate.outputs</c>: an optional array of paths that the gate writes — its report and any
    /// other artifacts. A relative entry resolves against the git root (or the working directory when
    /// not in git), the same anchor the trust entries resolve against; an absolute entry (the report
    /// may live outside the repo — an argv-named file in the user's cache, for example) is used as-is.
    /// This declaration replaces the dropped runnable-argv heuristic: whether an argv entry is a pure
    /// output cannot be inferred from its position or its execute bit (a wrapper such as
    /// <c>env VAR=1 sh gate.sh</c> hides both the script and the report), so nothing is guessed and the
    /// operator names the outputs instead. Entries must be non-empty strings, without glob characters
    /// (a declared output names exactly one path; a glob would leave it ambiguous which files the gate
    /// may rewrite) and, when relative, without a <c>..</c> segment (a relative path must not cross the
    /// repo boundary; an absolute entry's <c>..</c> segments are resolved away by
    /// <c>Path.GetFullPath</c>, so the path stays exactly the one the operator named). A gate whose
    /// report is not named in its argv — a lint gate's default report under <c>artifacts/</c>, for
    /// example — needs no <c>gate.outputs</c>: an untrusted path needs no exclusion.
    /// </summary>
    private static List<string> ReadOutputs(JsonObject holder, string label)
    {
        if (!holder.TryGetPropertyValue("outputs", out JsonNode? n) || n is null)
        {
            return [];
        }

        if (n is not JsonArray arr)
        {
            throw new LaunchException($"{label}.outputs must be an array of paths");
        }

        List<string> outputs = new(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? item = arr[i];
            if (!IsNonEmptyString(item, out string? s))
            {
                throw new LaunchException($"{label}.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must be a non-empty string");
            }

            string normalized = s.Replace('\\', '/');
            // A relative entry resolves against the git root (or the working directory when not in
            // git), so it stays inside the repo; an absolute entry is used as-is (normalised by
            // LaunchRun.ResolveOutputEntry), which is how a gate report outside the repo is declared.
            if (!Path.IsPathRooted(normalized))
            {
                // No parent traversal in a relative entry: ".." would let a declared output reach
                // outside the repo boundary.
                foreach (string segment in normalized.Split('/'))
                {
                    if (segment is "..")
                    {
                        throw new LaunchException($"{label}.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must not contain a '..' segment: {s}");
                    }
                }
            }

            // No glob characters: the exclusion of a declared output from the after-gate comparison is
            // only safe when it names exactly the path the gate writes (the same rule that keeps
            // gate.trust globs out of the report-path decision).
            if (normalized.IndexOfAny(['*', '?']) >= 0)
            {
                throw new LaunchException($"{label}.outputs[{i.ToString(CultureInfo.InvariantCulture)}] must not contain glob characters: {s}");
            }

            outputs.Add(normalized);
        }

        return outputs;
    }
}