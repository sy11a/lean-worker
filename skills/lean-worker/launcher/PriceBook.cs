// Price book: list prices per model, loaded from the shipped prices.json with the user's and the project's
// files merged over it, so a new model or a price change never needs a rebuild.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class PriceBook
{
    private readonly JsonObject _doc;
    public List<string> Sources { get; } = [];
    public List<string> Warnings { get; } = [];
    private static readonly string[] _keyRouting = ["anthropicBaseUrl", "keyEnv"];
    private static readonly string[] _quotaRouting = ["url", "adapter"];

    private static IEnumerable<string> StripKeyRouting(JsonObject doc, string file)
    {
        List<string> dropped = [];
        foreach ((string? name, JsonNode? node) in (doc["providers"] as JsonObject)?.ToList() ?? [])
        {
            if (node is not JsonObject p)
            {
                continue;
            }

            foreach (string? k in _keyRouting.Where(k => p.ContainsKey(k))) { _ = p.Remove(k); dropped.Add($"{name}.{k}"); }
            if (p["quota"] is JsonObject q)
            {
                foreach (string? k in _quotaRouting.Where(k => q.ContainsKey(k))) { _ = q.Remove(k); dropped.Add($"{name}.quota.{k}"); }
            }
        }
        return dropped.Count is 0 ? [] : [$"ignored in {file} (set them in {UserFile()}): {string.Join(", ", dropped)}"];
    }

    private PriceBook(JsonObject doc) => _doc = doc;

    public string? AsOf => Json.Str(_doc, "asOf");
    public string UnknownModel => Json.Str(_doc, "unknownModel") ?? "dearest";

    /// <summary>
    /// Shipped file, then the user's, then the project's (or --prices), each merged over the last.
    /// </summary>
    public static PriceBook Load(string runsRoot, string? explicitFile = null)
    {
        List<string> files =
        [
            Path.Combine(AppContext.BaseDirectory, "prices.json"),
            UserFile(),
            explicitFile ?? Path.Combine(runsRoot, "prices.json"),
        ];
        if (explicitFile is not null && !File.Exists(explicitFile))
        {
            throw new LaunchException($"prices file not found: {explicitFile}");
        }

        PriceBook book = new([]);
        for (int i = 0; i < files.Count; i++)
        {
            string f = files[i];
            if (!File.Exists(f))
            {
                continue;
            }

            JsonObject o;
            try { o = Json.ParseLenient(File.ReadAllText(f)).AsObject(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new LaunchException($"{f} is not valid JSON: {ex.Message}"); }
            // A project file travels with the repository, so it may not say where keys are sent or which
            // variable holds one: those settings come only from the shipped and the personal file.
            if (i == files.Count - 1)
            {
                book.Warnings.AddRange(StripKeyRouting(o, f));
            }

            Json.MergeInto(book._doc, o);
            book.Sources.Add(Path.GetFullPath(f));
        }
        if (book.Sources.Count is 0)
        {
            throw new LaunchException($"no prices.json found (expected one next to the launcher: {files[0]})");
        }

        return book;
    }

    internal static PriceBook FromJson(JsonObject doc) => new(doc);

    public static string UserFile()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(root, "lean-worker", "prices.json");
    }

    public Provider Provider(string name) => new(name, _doc["providers"]?[name] as JsonObject);

    /// <summary>
    /// Splits "provider/model"; a bare model id belongs to anthropic.
    /// </summary>
    public static (string Provider, string Model) Split(string id)
    {
        int slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? (id[..slash], id[(slash + 1)..]) : ("anthropic", id);
    }

    /// <summary>
    /// The price for a model id reported by the runtime, or null when no key matches.
    /// </summary>
    public ModelPrice? Find(string provider, string model)
    {
        if (_doc["models"] is not JsonObject models)
        {
            return null;
        }

        string prefix = Provider(provider).PriceAs + "/";
        IEnumerable<KeyValuePair<string, JsonNode?>> candidates = models.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                                            && model.StartsWith(kv.Key[prefix.Length..], StringComparison.OrdinalIgnoreCase));
        KeyValuePair<string, JsonNode?> best = candidates.OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
        return best.Value is JsonObject o ? Parse(best.Key, o) : null;
    }

    /// <summary>
    /// True for a Claude family alias: sonnet, opus, haiku or fable (case-insensitive), optionally followed by a
    /// "[...]" suffix; no provider check.
    /// </summary>
    internal static bool IsAlias(string model)
    {
        int familyEnd = model.Length;
        if (model.Length > 0 && model[^1] == ']')
        {
            int open = model.IndexOf('[', StringComparison.Ordinal);
            if (open >= 0 && model.Length - open >= 3)
            {
                familyEnd = open;
            }
        }
        string family = model[..familyEnd];
        return family.Equals("sonnet", StringComparison.OrdinalIgnoreCase)
            || family.Equals("opus", StringComparison.OrdinalIgnoreCase)
            || family.Equals("haiku", StringComparison.OrdinalIgnoreCase)
            || family.Equals("fable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a Claude family alias to the newest matching model id in the price book
    /// (anthropic/claude-&lt;family&gt;-&lt;rest&gt; where &lt;rest&gt; is all-int), so a profile's alias keeps pointing
    /// at the current version without anyone editing it. Returns the input unchanged for non-aliases, for
    /// providers priced as anyone other than anthropic, or when no key matches.
    /// </summary>
    public string ResolveAlias(string provider, string model)
    {
        if (!IsAlias(model) || Provider(provider).PriceAs is not "anthropic")
        {
            return model;
        }

        int bracket = model.IndexOf('[', StringComparison.Ordinal);
        string family = (bracket >= 0 ? model[..bracket] : model).ToLowerInvariant();
        string prefix = "anthropic/claude-" + family + "-";
        string? best = null;
        List<int>? bestParts = null;
        if (_doc["models"] is JsonObject models)
        {
            foreach ((string? key, _) in models)
            {
                if (key is not { } k || !k.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string rest = k[prefix.Length..];
                string[] raw = rest.Split('-');
                List<int> parts = [];
                bool ok = true;
                foreach (string p in raw)
                {
                    if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    {
                        ok = false;
                        break;
                    }
                    parts.Add(n);
                }
                if (!ok)
                {
                    continue;
                }

                if (bestParts is null || CompareVersions(parts, bestParts) > 0)
                {
                    best = "claude-" + family + "-" + rest;
                    bestParts = parts;
                }
            }
        }
        return best ?? model;
    }

    private static int CompareVersions(List<int> a, List<int> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c is not 0)
            {
                return c;
            }
        }
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>
    /// The traits of a model id without its provider: the longest "modelTraits" key the id starts with (case
    /// insensitive), so one entry covers the model from every provider that serves it.
    /// </summary>
    public ModelTraits? Traits(string model)
    {
        if (_doc["modelTraits"] is not JsonObject all)
        {
            return null;
        }

        KeyValuePair<string, JsonNode?> best = all.Where(kv => !kv.Key.StartsWith('_') && model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
        return best.Value is JsonObject o ? new ModelTraits(best.Key, Json.StrList(o, "allowedTools") ?? [], Json.Str(o, "note")) : null;
    }

    /// <summary>
    /// Applies the unknownModel policy: "dearest", "error", or a model key to price as.
    /// </summary>
    public ModelPrice Resolve(string provider, string model, out string? note)
    {
        note = null;
        ModelPrice? found = Find(provider, model);
        if (found is not null)
        {
            return found;
        }

        string policy = UnknownModel;
        if (policy is "error")
        {
            throw new LaunchException($"no price for {provider}/{model}; add it to .lean-worker/prices.json");
        }

        note = $"no price for {provider}/{model}; priced as {policy}";
        if (policy is not "dearest" && _doc["models"]?[policy] is JsonObject named)
        {
            return Parse(policy, named);
        }

        return Dearest() ?? throw new LaunchException("prices.json has no models");
    }

    public ModelPrice? Dearest()
    {
        return (_doc["models"] as JsonObject)?
            .Where(kv => kv.Value is JsonObject)
            .Select(kv => Parse(kv.Key, (JsonObject)kv.Value!))
            .OrderByDescending(p => p.Base.Output).FirstOrDefault();
    }

    private ModelPrice Parse(string key, JsonObject o)
    {
        string currency = Json.Str(o, "currency") ?? "USD";
        decimal rate = Json.Dec(_doc["rates"] as JsonObject, currency)
                   ?? throw new LaunchException($"prices.json: no rate for currency {currency} (model {key})");
        JsonObject? above = o["above"] as JsonObject;
        return new ModelPrice(key, ParsePrice(o), above is null ? null : (long?)(Json.Dec(above, "tokens") ?? 0),
                              above is null ? null : ParsePrice(above, ParsePrice(o)), rate);
    }

    // Cache reads and writes cost the input price unless set; a 1-hour write costs 2x input (Anthropic's rule).
    private static Price ParsePrice(JsonObject o, Price? fallback = null)
    {
        decimal input = Json.Dec(o, "input") ?? fallback?.Input ?? 0;
        return new Price(input,
            Json.Dec(o, "output") ?? fallback?.Output ?? 0,
            Json.Dec(o, "cacheRead") ?? fallback?.CacheRead ?? input,
            Json.Dec(o, "cacheWrite") ?? fallback?.CacheWrite ?? input,
            Json.Dec(o, "cacheWrite1h") ?? fallback?.CacheWrite1h ?? (input * 2));
    }

    public List<string> QuotaProviders() => (_doc["providers"] as JsonObject)?.Where(kv => kv.Value?["quota"] is JsonObject).Select(kv => kv.Key).ToList() ?? [];

    public IEnumerable<(string Key, string? AsOf)> Entries() => (_doc["models"] as JsonObject)?.Select(kv => (kv.Key, Json.Str(kv.Value as JsonObject, "asOf") ?? AsOf)) ?? [];
}