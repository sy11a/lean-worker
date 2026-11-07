using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A profile's runtimeByProvider maps a provider to a runtime, resolved per model in the chain:
/// --runtime, then the map, then the profile's runtime, then the provider's default (issue #125).
/// </summary>
[Collection("launcher-process-state")]
public class RuntimeByProviderTests
{
    private const string KeyEnv = "LW_TEST_RBP_KEY";

    // mmx and mmx2 have an Anthropic-compatible endpoint, so their default runtime is claude; plain has none (default opencode).
    private static string Book(string quotaUrl)
    {
        return """
        {"rates":{"USD":1},
         "providers":{
           "mmx":{"billing":"subscription","anthropicBaseUrl":"http://127.0.0.1:1","keyEnv":"@KEY@","quota":{"adapter":"minimax","url":"@URL@"}},
           "plain":{"billing":"metered"}},
         "models":{"mmx/m1":{"input":1,"output":1},"plain/p1":{"input":1,"output":1},"anthropic/claude-haiku-4-5":{"input":1,"output":1}}}
        """.Replace("@KEY@", KeyEnv, StringComparison.Ordinal).Replace("@URL@", quotaUrl, StringComparison.Ordinal);
    }

    private static string Quota(int remainingPercent)
    {
        return """
        {"model_remains":[{"model_name":"general","start_time":1790712000000,"end_time":1790730000000,
          "current_interval_remaining_percent":@P@,"current_weekly_remaining_percent":@P@}],
         "base_resp":{"status_code":0}}
        """.Replace("@P@", remainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static async Task<(string Stdout, JsonObject Summary)> RunAsync(string profileBody, int quotaRemaining, Action<Options>? configure = null)
    {
        await using QuotaServer server = new(Quota(quotaRemaining));
        string root = RunAsyncGolden.NewRoot();
        // Endpoint and quota routing are honoured only from the user's prices.json, so the book lives under a temporary XDG_CONFIG_HOME.
        string xdg = Directory.CreateTempSubdirectory("lw-rbp-xdg").FullName;
        _ = Directory.CreateDirectory(Path.Combine(xdg, "lean-worker"));
        await File.WriteAllTextAsync(Path.Combine(xdg, "lean-worker", "prices.json"), Book(server.Url), TestContext.Current.CancellationToken);
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(xdg, "cache"));
        await File.WriteAllTextAsync(Path.Combine(root, "profiles.json"), "{\"profiles\":{\"p\":{" + profileBody + "}}}", TestContext.Current.CancellationToken);

        string openCodeStub = Directory.CreateTempSubdirectory("lw-rbp-oc").FullName;
        string stub = Path.Combine(openCodeStub, "opencode");
        await File.WriteAllTextAsync(stub, "#!/bin/sh\ncat >/dev/null\nexit 0\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", openCodeStub + Path.PathSeparator + oldPath);
        Environment.SetEnvironmentVariable(KeyEnv, "k");
        try
        {
            (_, string stdout) = await RunAsyncGolden.RunAsync(root, "exit 0", o =>
            {
                o.Model = null;
                o.Profile = "p";
                o.Mode = "auto";
                configure?.Invoke(o);
            });
            return (stdout, RunAsyncGolden.Summary(RunAsyncGolden.RunDirFrom(stdout)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", oldCache);
            Directory.Delete(xdg, recursive: true);
            Environment.SetEnvironmentVariable(KeyEnv, value: null);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(openCodeStub, recursive: true);
        }
    }

    [Fact]
    public async Task A_mapped_provider_runs_under_the_mapped_runtime_not_its_defaultAsync()
    {
        (_, JsonObject s) = await RunAsync("""
            "model":["mmx/m1","anthropic/claude-haiku-4-5"],"runtimeByProvider":{"mmx":"opencode"}
            """, quotaRemaining: 80);

        Assert.Equal("mmx", Json.Str(s, "provider"));
        Assert.Equal("opencode", Json.Str(s, "runtime"));
        Assert.DoesNotContain(RunAsyncGolden.Notes_(s), n => n.Contains("no Anthropic-compatible endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mixed_chain_falls_through_from_the_mapped_provider_to_anthropic_on_the_claude_runtimeAsync()
    {
        (_, JsonObject s) = await RunAsync("""
            "model":["mmx/m1","anthropic/claude-haiku-4-5"],"runtimeByProvider":{"mmx":"opencode"}
            """, quotaRemaining: 0);

        Assert.Equal("anthropic", Json.Str(s, "provider"));
        Assert.Equal("claude", Json.Str(s, "runtime"));
        Assert.Contains(RunAsyncGolden.Notes_(s), n => n.StartsWith("skipped mmx/m1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_map_beats_the_profiles_runtime_for_the_providers_it_names_and_the_profile_holds_for_the_restAsync()
    {
        (_, JsonObject fallThrough) = await RunAsync("""
            "model":["mmx/m1","anthropic/claude-haiku-4-5"],"runtime":"opencode","runtimeByProvider":{"anthropic":"claude"}
            """, quotaRemaining: 0);
        Assert.Equal("anthropic", Json.Str(fallThrough, "provider"));
        Assert.Equal("claude", Json.Str(fallThrough, "runtime"));

        (_, JsonObject unmapped) = await RunAsync("""
            "model":["mmx/m1","anthropic/claude-haiku-4-5"],"runtime":"opencode","runtimeByProvider":{"anthropic":"claude"}
            """, quotaRemaining: 80);
        Assert.Equal("mmx", Json.Str(unmapped, "provider"));
        Assert.Equal("opencode", Json.Str(unmapped, "runtime"));
    }

    [Fact]
    public async Task The_runtime_option_still_beats_the_mapAsync()
    {
        (_, JsonObject s) = await RunAsync("""
            "model":["mmx/m1","anthropic/claude-haiku-4-5"],"runtimeByProvider":{"mmx":"opencode"}
            """, quotaRemaining: 80, o => o.Runtime = "claude");

        Assert.Equal("mmx", Json.Str(s, "provider"));
        Assert.Equal("claude", Json.Str(s, "runtime"));
    }

    [Fact]
    public async Task A_single_model_run_as_a_continuation_or_escalation_resolves_through_the_map_tooAsync()
    {
        (_, JsonObject s) = await RunAsync("""
            "runtimeByProvider":{"mmx":"opencode"}
            """, quotaRemaining: 80, o => o.Model = "mmx/m1");

        Assert.Equal("mmx", Json.Str(s, "provider"));
        Assert.Equal("opencode", Json.Str(s, "runtime"));
    }

    [Fact]
    public async Task A_profile_without_the_key_keeps_the_provider_defaultsAsync()
    {
        (_, JsonObject viaEndpoint) = await RunAsync("""
            "model":["mmx/m1"]
            """, quotaRemaining: 80);
        Assert.Equal("claude", Json.Str(viaEndpoint, "runtime"));

        (_, JsonObject plain) = await RunAsync("""
            "model":["plain/p1"]
            """, quotaRemaining: 80);
        Assert.Equal("opencode", Json.Str(plain, "runtime"));
        Assert.Contains(RunAsyncGolden.Notes_(plain), n => n.Contains("no Anthropic-compatible endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_runtime_name_in_the_map_is_refused_at_loadAsync()
    {
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync("""
            "model":["mmx/m1"],"runtimeByProvider":{"mmx":"nosuchruntime"}
            """, quotaRemaining: 80));

        Assert.Contains("nosuchruntime", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"runtimeByProvider\":[\"opencode\"]")]
    [InlineData("\"runtimeByProvider\":\"opencode\"")]
    [InlineData("\"runtimeByProvider\":{\"mmx\":7}")]
    [InlineData("\"runtimeByProvider\":{\"mmx\":null}")]
    public async Task A_map_that_is_not_provider_name_to_runtime_name_is_refused_at_loadAsync(string key)
    {
        LaunchException ex = await Assert.ThrowsAsync<LaunchException>(async () => await RunAsync($"\"model\":[\"mmx/m1\"],{key}", quotaRemaining: 80));

        Assert.Contains("runtimeByProvider", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A loopback HTTP server answering every request with one MiniMax quota body.
    /// </summary>
    private sealed class QuotaServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public QuotaServer(string body)
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            string portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Url = "http://127.0.0.1:" + portText + "/quota";
            _listener.Prefixes.Add("http://127.0.0.1:" + portText + "/");
            _listener.Start();
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        HttpListenerContext ctx = await _listener.GetContextAsync();
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
                        ctx.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            }, TestContext.Current.CancellationToken);
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
