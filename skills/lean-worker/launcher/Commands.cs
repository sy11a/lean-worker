// Subcommands besides launching a worker: the worker's pre-tool hook, subscription quota, pricing a manual
// session, run statistics and the merged price book.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuotaApi = LeanWorker.Quota;

namespace LeanWorker;

internal static partial class Commands
{
    private static readonly CultureInfo _ic = CultureInfo.InvariantCulture;

    private static Dictionary<string, string?> Flags(string[] args, params string[] booleans)
    {
        Dictionary<string, string?> d = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new LaunchException($"unexpected argument '{args[i]}'");
            }

            if (booleans.Contains(args[i], StringComparer.Ordinal))
            {
                d[args[i]] = null;
            }
            else
            {
                d[args[i]] = i + 1 < args.Length ? args[++i] : throw new LaunchException($"{args[i]} needs a value");
            }
        }
        return d;
    }

    /// <summary>
    /// PreToolUse hook: once the launcher has written wrapup.json, deny every tool call with its reason.
    /// A hook failure must never block the worker; the launcher's budget still applies.
    /// </summary>
    public static int Hook(string[] args)
    {
        try
        {
            _ = Console.In.ReadToEnd();
            string? runDir = Flags(args).GetValueOrDefault("--run-dir");
            if (runDir is null)
            {
                return 0;
            }

            File.AppendAllText(Path.Combine(runDir, "hook.log"), DateTimeOffset.Now.ToString("o") + Environment.NewLine);
            string marker = Path.Combine(runDir, "wrapup.json");
            if (!File.Exists(marker))
            {
                return 0;
            }

            string reason = Json.Str(Json.ParseLenient(File.ReadAllText(marker)).AsObject(), "reason") ?? Meter.HandoffInstruction;
            Console.Out.Write(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason,
                },
            }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or LaunchException) { }
        return 0;
    }

    public static int Quota(string[] args)
    {
        Dictionary<string, string?> f = Flags(args, "--json");
        PriceBook prices = PriceBook.Load(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", f.GetValueOrDefault("--prices"));
        TimeSpan? maxAge = f.TryGetValue("--max-age", out string? s) && s is not null ? TimeSpan.FromSeconds(int.Parse(s, _ic)) : null;
        List<string> names = f.TryGetValue("--provider", out string? p) && p is not null ? [p] : prices.QuotaProviders();
        if (names.Count is 0)
        {
            throw new LaunchException("no provider in prices.json has a quota adapter");
        }

        JsonArray readings = [];
        foreach (string name in names)
        {
            Provider provider = prices.Provider(name);
            try
            {
                QuotaReading q = QuotaApi.Read(provider, maxAge);
                if (f.ContainsKey("--json"))
                {
                    readings.Add(q.ToJson());
                }
                else
                {
                    Console.Out.WriteLine($"{name}{(q.Level is null ? string.Empty : $" ({q.Level})")}: {q.Line()}");
                    foreach (QuotaWindow? w in q.Windows.Where(w => w.Detail is not null))
                    {
                        Console.Out.WriteLine($"  {w.Name}: {w.Detail}");
                    }

                    (bool ok, string? why) = QuotaApi.Headroom(provider, q);
                    Console.Out.WriteLine($"  headroom for workers: {(ok ? "yes" : "no")} ({why})");
                }
            }
            catch (Exception ex) when (QuotaApi.IsReadFailure(ex) && ex is not LaunchException)
            {
                throw new LaunchException($"quota for {name}: {ex.Message}");
            }
        }
        if (f.ContainsKey("--json"))
        {
            Console.Out.WriteLine(readings.ToJsonString(Json.Indented));
        }

        return 0;
    }

    public static int Cost(string[] args)
    {
        Dictionary<string, string?> f = Flags(args);
        PriceBook prices = PriceBook.Load(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", f.GetValueOrDefault("--prices"));
        List<(string Provider, Usage Usage)> calls;
        string label;
        if (f.GetValueOrDefault("--claude") is { } claude)
        {
            (calls, label) = ClaudeCalls(claude, f.GetValueOrDefault("--provider") ?? "anthropic");
        }
        else if (f.GetValueOrDefault("--opencode") is { } session)
        {
            calls = OpencodeCalls(session);
            label = $"opencode session {session}";
        }
        else
        {
            throw new LaunchException("cost needs --claude <session-id|file> or --opencode <session-id>");
        }

        decimal total = 0;
        HashSet<string> notes = new(StringComparer.Ordinal);
        Dictionary<string, (int Calls, decimal Cost, long Tokens)> perModel = new(StringComparer.Ordinal);
        foreach ((string? prov, Usage? u) in calls)
        {
            decimal c = prices.Resolve(prov, u.Model, out string? note).Cost(u);
            if (note is not null)
            {
                _ = notes.Add(note);
            }

            total += c;
            string key = $"{prov}/{u.Model}";
            (int Calls, decimal Cost, long Tokens) cur = perModel.GetValueOrDefault(key);
            perModel[key] = (cur.Calls + 1, cur.Cost + c, cur.Tokens + u.Context + u.Output + u.Reasoning);
        }
        Console.Out.WriteLine($"{label}: {calls.Count} API calls, ${total.ToString("0.0000", _ic)} at list price");
        foreach ((string? key, (int Calls, decimal Cost, long Tokens) v) in perModel.OrderByDescending(kv => kv.Value.Cost))
        {
            string billing = Launcher.Billing(prices.Provider(PriceBook.Split(key).Provider), "claude", hasKey: false);
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {key}: {v.Calls} calls, {v.Tokens.ToString("N0", _ic)} tokens, ${v.Cost.ToString("0.0000", _ic)} ({billing})"));
        }
        foreach (string n in notes)
        {
            Console.Out.WriteLine($"  note: {n}");
        }

        return 0;
    }

    private static string FindClaudeTranscript(string sessionId)
    {
        string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string projects = Path.Combine(dir, "projects");
        string? hit = Directory.Exists(projects) ? Directory.EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;
        return hit ?? throw new LaunchException($"no transcript {sessionId}.jsonl under {projects}");
    }

    private static (List<(string Provider, Usage Usage)> Calls, string Label) ClaudeCalls(string claude, string provider)
    {
        string path = File.Exists(claude) ? claude : FindClaudeTranscript(claude);
        ClaudeRuntime rt = new();
        Dictionary<string, Usage> byId = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal) || Json.TryParseObject(line) is not { } obj)
            {
                continue;
            }

            if (rt.Parse(obj, new Outcome()) is { } u)
            {
                byId[u.Id] = u; // the last record of a message wins
            }
        }

        return ([.. byId.Values.Select(u => (provider, u))], path);
    }

    private static List<(string, Usage)> OpencodeCalls(string session)
    {
        if (!MyRegex().IsMatch(session))
        {
            throw new LaunchException($"not an opencode session id: {session}");
        }

        string opencode = Launcher.FindOnPath("opencode") ?? throw new LaunchException("'opencode' is not on PATH.");
        string sql = "select id, json_extract(data,'$.providerID') as provider, json_extract(data,'$.modelID') as model, " +
                  $"json_extract(data,'$.tokens') as tokens from message where session_id='{session}' " +
                  "and json_extract(data,'$.role')='assistant' order by time_created";
        ProcessStartInfo psi = new(opencode) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string? a in new[] { "db", sql, "--format", "json" })
        {
            psi.ArgumentList.Add(a);
        }
        // opencode reads its project dir from the inherited PWD env var, so pin it to the launcher's cwd
        psi.Environment["PWD"] = Directory.GetCurrentDirectory();
        _ = psi.Environment.Remove("OLDPWD");

        using Process p = Process.Start(psi) ?? throw new LaunchException("could not start opencode");
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode is not 0)
        {
            throw new LaunchException($"opencode db failed: {p.StandardError.ReadToEnd().Trim()}");
        }

        JsonArray rows = Json.ParseLenient(output) as JsonArray ?? [];
        List<(string, Usage)> calls = [];
        foreach (JsonObject row in rows.OfType<JsonObject>())
        {
            if (Json.Str(row, "tokens") is not { } t || Json.ParseLenient(t) is not JsonObject tok)
            {
                continue;
            }

            JsonObject? cache = tok["cache"] as JsonObject;
            calls.Add((Json.Str(row, "provider") ?? string.Empty, new Usage(Json.Str(row, "id") ?? string.Empty, Json.Str(row, "model") ?? string.Empty,
                Json.Num(tok["input"]), Json.Num(tok["output"]), Json.Num(tok["reasoning"]),
                Json.Num(cache?["read"]), Json.Num(cache?["write"]), 0)));
        }
        if (calls.Count is 0)
        {
            throw new LaunchException($"no assistant messages with usage in opencode session {session}");
        }

        return calls;
    }

    internal static List<StatsRow> StatsRows(IEnumerable<JsonObject> runs)
    {
        return [.. runs.GroupBy(r => (Json.Str(r, "profile") ?? "-", $"{Json.Str(r, "provider") ?? "anthropic"}/{Json.Str(r, "model")}"))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal)
            .Select(g =>
            {
                // A row counts as a success when no gate ran and the worker succeeded, or when the gate ran
                // and decided clean. Gate rounds that decided continue are in-progress (or stuck/error), not successes.
                int ok = g.Count(r => r["gate"] is JsonObject gate
                    ? Json.Str(gate, "decision") is GateChain.Clean
                    : Json.Str(r, "status") is "success");
                decimal cost = g.Sum(r => Json.Dec(r, "total_cost_usd") ?? 0);
                List<decimal> quota = [.. g.Select(r => r["quota_used_pct"] as JsonObject).OfType<JsonObject>().Select(q => q.Select(kv => Json.Dec(q, kv.Key) ?? 0).DefaultIfEmpty(0).Max())];
                int wrappedUp = g.Count(r => Json.Str(r, "status") is "wrapped-up");
                int escalations = g.Count(r => Json.Str(r, "escalate_to") is not null);
                decimal? costPerSuccess = ok > 0 ? cost / ok : null;
                decimal? quotaAvg = quota.Count > 0 ? quota.Average() : null;
                return new StatsRow(g.Key.Item1, g.Key.Item2, g.Count(), ok, wrappedUp, escalations, cost, costPerSuccess, quotaAvg);
            }),
        ];
    }

    public static int Stats(string[] args)
    {
        Dictionary<string, string?> f = Flags(args, "--json");
        string path = Path.Combine(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", "runs.jsonl");
        if (!File.Exists(path))
        {
            throw new LaunchException($"no runs recorded yet ({path})");
        }

        DateTimeOffset since = f.GetValueOrDefault("--since") is { } s ? DateTimeOffset.Parse(s, _ic) : DateTimeOffset.MinValue;
        List<JsonObject> runs = [.. File.ReadLines(path).Select(line => Json.TryParseObject(line)).OfType<JsonObject>().Where(r => Json.Str(r, "timestamp") is { } t && DateTimeOffset.Parse(t, _ic) >= since)];
        List<StatsRow> rows = StatsRows(runs);
        if (f.ContainsKey("--json"))
        {
            JsonArray groups = new([.. rows.Select(r => (JsonNode)new JsonObject
            {
                ["profile"] = r.Profile,
                ["model"] = r.Model,
                ["runs"] = r.Runs,
                ["success"] = r.Success,
                ["wrapped_up"] = r.WrappedUp,
                ["escalations"] = r.Escalations,
                ["cost_usd"] = decimal.Round(r.CostUsd, 6, MidpointRounding.ToEven),
                ["cost_per_success_usd"] = r.CostPerSuccessUsd is { } c ? decimal.Round(c, 6, global::System.MidpointRounding.ToEven) : null,
                ["quota_pct_per_run"] = r.QuotaPctPerRun is { } q ? decimal.Round(q, 2, global::System.MidpointRounding.ToEven) : null,
            }),
            ]);
            JsonObject doc = new()
            {
                ["schema_version"] = Launcher.RunSchemaVersion,
                ["runs_file"] = Path.GetFullPath(path),
                ["since"] = since == DateTimeOffset.MinValue ? null : since.ToString("o"),
                ["cost_basis"] = "list price; list-price equivalent for subscriptions",
                ["groups"] = groups,
            };
            Console.Out.WriteLine(doc.ToJsonString(Json.Indented));
            return 0;
        }
        Console.Out.WriteLine($"{"profile",-16} {"model",-34} {"runs",4} {"ok",4} {"wrap",4} {"esc",4} {"cost",9} {"$/success",9} {"quota%/run",10}");
        foreach (StatsRow r in rows)
        {
            string successCost = r.CostPerSuccessUsd is { } c ? "$" + c.ToString("0.000", _ic) : "-";
            string quotaText = r.QuotaPctPerRun is { } q ? q.ToString("0.0", _ic) : "-";
            string costText = "$" + r.CostUsd.ToString("0.000", _ic);
            string row = string.Create(CultureInfo.InvariantCulture, $"{r.Profile,-16} {r.Model,-34} {r.Runs,4} {r.Success,4} {r.WrappedUp,4} {r.Escalations,4} {costText,9} {successCost,9} {quotaText,10}");
            Console.Out.WriteLine(row);
        }

        Console.Out.WriteLine("cost = list price (list-price equivalent for subscriptions); esc = runs that offered the next model; quota%/run = largest window increase per run.");
        return 0;
    }

    public static int Prices(string[] args)
    {
        Dictionary<string, string?> f = Flags(args);
        PriceBook prices = PriceBook.Load(f.GetValueOrDefault("--runs-root") ?? ".lean-worker", f.GetValueOrDefault("--prices"));
        Console.Out.WriteLine("sources (later ones override earlier):");
        foreach (string src in prices.Sources)
        {
            Console.Out.WriteLine($"  {src}");
        }

        Console.Out.WriteLine($"unknown models: {prices.UnknownModel}");
        foreach ((string? key, string? asOf) in prices.Entries())
        {
            bool stale = asOf is not null && DateTimeOffset.TryParse(asOf, _ic, DateTimeStyles.AssumeUniversal, out DateTimeOffset d) && DateTimeOffset.Now - d > TimeSpan.FromDays(90);
            Console.Out.WriteLine($"  {key,-36} as of {asOf ?? "?"}{(stale ? "  <- older than 90 days, check it" : string.Empty)}");
        }
        return 0;
    }

    [GeneratedRegex("^ses_[A-Za-z0-9]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MyRegex();
}