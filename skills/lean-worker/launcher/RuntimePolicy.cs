using System.Text.Json.Nodes;

namespace LeanWorker;

/// <summary>
/// Which runtime a model runs under: --runtime, then the profile's runtimeByProvider[provider], then the
/// profile's runtime, then the provider's default.
/// </summary>
internal sealed class RuntimePolicy
{
    private readonly string? _option;
    private readonly string? _profile;
    private readonly Dictionary<string, string> _byProvider = new(StringComparer.Ordinal);

    private RuntimePolicy(string? option, JsonObject? profile)
    {
        _option = option;
        _profile = Json.Str(profile, "runtime");
        ReadByProvider(profile?["runtimeByProvider"]);
    }

    /// <summary>
    /// The runtime before any model is picked.
    /// </summary>
    public string Initial => _option ?? _profile ?? "claude";

    /// <summary>
    /// Reads the policy and validates every runtime name in it, before any quota read.
    /// </summary>
    public static RuntimePolicy Load(string? option, JsonObject? profile)
    {
        RuntimePolicy policy = new(option, profile);
        foreach (string name in new[] { option, policy._profile }.Concat(policy._byProvider.Values).OfType<string>())
        {
            _ = Runtimes.Get(name);
        }

        return policy;
    }

    /// <summary>
    /// The runtime for a model of this provider, and whether it came from the provider's default.
    /// </summary>
    public (string Name, IRuntime Runtime, bool Defaulted) For(Provider info)
    {
        string? chosen = _option ?? (_byProvider.TryGetValue(info.Name, out string? mapped) ? mapped : null) ?? _profile;
        string name = chosen ?? Launcher.DefaultRuntime(info);
        return (name, Runtimes.Get(name), chosen is null);
    }

    private void ReadByProvider(JsonNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (node is not JsonObject obj)
        {
            throw new LaunchException("profile runtimeByProvider must be an object of provider name -> runtime name");
        }

        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (kv.Value is not JsonValue v || !v.TryGetValue(out string? runtime))
            {
                throw new LaunchException($"profile runtimeByProvider['{kv.Key}'] must be a runtime name");
            }

            _byProvider[kv.Key] = runtime;
        }
    }
}
