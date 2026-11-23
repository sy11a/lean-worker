// `status`: the read-only view of runs from their status.json files (docs/research/live-status/
// README.md, sections 4.1 to 4.3): the caller's own runs (the default, through the per-user
// index), one owner's (--owner), every indexed run (--all), this repository's runs (--here, a scan)
// or one run (--run <dir>), as a one-shot plain listing or --json, as a live table (--watch,
// StatusWatch) or as a wait until the runs ended (--wait, StatusWait); --whoami prints the caller's
// owner keys. The one-shot listing and --watch exit 0 when listed (even when runs failed); --wait
// exits with what the runs ended as; every mode exits 2 on a usage error or when no caller
// identity can be found. It writes nothing; a scope that reads the index deletes the pointers of
// runs that provably ended long ago (StatusIndexScan), on its first read only, and nothing else.

namespace LeanWorker;

internal static class StatusCommand
{
    public const string Usage = "status [--owner <key> | --all | --here [--runs-root <dir>] | --run <dir>] [--since <dur>] [--started-after <t>] "
                                + "[--json | --watch [--bell] | --wait [--expect <n>] [--any] [--timeout <dur>] [--grace <dur>]] [--interval <s>] "
                                + "| status --whoami [--key]";

    /// <summary>
    /// --since for ended and legacy rows of --here and --all when none is given.
    /// </summary>
    public static readonly TimeSpan DefaultSince = StatusScope.DefaultSince;

    /// <summary>
    /// What `status` says when nothing tells who the caller is.
    /// </summary>
    internal const string NoCaller = "cannot tell which session you are; pass --owner, --here or --all";

    /// <summary>
    /// The longest usage-error message printed (it may echo an argument).
    /// </summary>
    private const int MessageLimit = 300;

    /// <summary>
    /// The parsed arguments: the run dir (--run) or the scan (--here), its runs root, --since and
    /// --json; the index scopes (--all, --owner), the --whoami mode (with --key), the --wait and
    /// --watch modes and their options, and --started-after.
    /// </summary>
    internal sealed record StatusArgs(string? Run, bool Here, string? RunsRoot, string? Since, bool Json)
    {
        public bool All { get; init; }

        public string? Owner { get; init; }

        public bool Whoami { get; init; }

        public bool Key { get; init; }

        public bool Wait { get; init; }

        public bool Watch { get; init; }

        public bool Any { get; init; }

        public bool Bell { get; init; }

        /// <summary>
        /// --interval in seconds; null for the default.
        /// </summary>
        public int? Interval { get; init; }

        public TimeSpan? Timeout { get; init; }

        public TimeSpan? Grace { get; init; }

        public int? Expect { get; init; }

        public DateTimeOffset? StartedAfter { get; init; }
    }

    /// <summary>
    /// The command with its inputs: the output, the reader (its /proc, clock, boot id and scan
    /// budget; the machine's own is <see cref="StatusReader.Host"/>), the directory it is called
    /// in, the environment (the caller's variables and the index folder; the process's own is
    /// <see cref="StatusEnvironment.Host"/>) and, for --wait and --watch, the host (the sleep, the
    /// terminal and a token; the process's own is <see cref="StatusHost.For"/>; without one, both
    /// are refused with exit 2). Without an environment
    /// (<see cref="StatusEnvironment.None"/>) there is no caller identity and no index, so only
    /// --run and --here list anything.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, StatusReader reader, string cwd, StatusEnvironment? env = null,
        StatusHost? host = null)
    {
        env ??= StatusEnvironment.None;
        StatusArgs a;
        TimeSpan? since;
        try
        {
            a = Parse(args);
            since = a.Since is { } s ? ParseDuration(s) : null;
        }
        catch (LaunchException ex)
        {
            // The message may echo an argument (an unknown option, a --since value).
            await output.WriteLineAsync($"lean-worker status: {TerminalText.Clean(ex.Message, MessageLimit)}; usage: {Usage}").ConfigureAwait(false);
            return 2;
        }

        if (a.Whoami)
        {
            return await WhoamiAsync(output, a.Key, OwnerKeys.CallerOf(env.Variable, reader.Proc)).ConfigureAwait(false);
        }

        if ((a.Wait || a.Watch) && host is null)
        {
            // A caller that gives no host (no sleep, no terminal) gets the one-shot modes only.
            await output.WriteLineAsync($"lean-worker status: {(a.Wait ? "--wait" : "--watch")} needs a host to sleep and draw on; usage: {Usage}").ConfigureAwait(false);
            return 2;
        }

        DateTimeOffset now = reader.Clock.GetUtcNow();
        if (await StatusScope.ReadAsync(a, cwd, reader, env, now, since, reap: true).ConfigureAwait(false) is not { } listing)
        {
            string message = a.Run is not null
                ? $"no run dir {TerminalText.Clean(Path.GetFullPath(a.Run, cwd), StatusText.ScopeLimit)}; usage: {Usage}"
                : NoCaller;
            await output.WriteLineAsync("lean-worker status: " + message).ConfigureAwait(false);
            return 2;
        }

        if (host is not null && (a.Wait || a.Watch))
        {
            // Every later read of --wait and --watch reaps nothing.
            async Task<StatusScope.Listing?> ReadAgainAsync() =>
                await StatusScope.ReadAsync(a, cwd, reader, env, reader.Clock.GetUtcNow(), since, reap: false).ConfigureAwait(false);
            return await RepeatAsync(a, listing, async () => await ReadAgainAsync().ConfigureAwait(false), new Inputs(output, reader, env, host)).ConfigureAwait(false);
        }

        StatusListing.Shown shown = StatusListing.Select(listing, reader, now, a.StartedAfter);
        await StatusListing.PrintAsync(output, a.Json, listing, shown, now, a.Since ?? "24h").ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// What --wait and --watch run with.
    /// </summary>
    private sealed record Inputs(TextWriter Output, StatusReader Reader, StatusEnvironment Env, StatusHost Host);

    /// <summary>
    /// --wait (StatusWait) or --watch (StatusWatch) from the first read of the scope.
    /// </summary>
    private static async Task<int> RepeatAsync(StatusArgs a, StatusScope.Listing first, Func<Task<StatusScope.Listing?>> read, Inputs inputs)
    {
        TimeSpan interval = a.Interval is { } seconds ? TimeSpan.FromSeconds(seconds) : StatusWait.DefaultInterval;
        if (a.Wait)
        {
            StatusWait.Settings wait = new(interval, a.Timeout ?? StatusWait.DefaultTimeout, a.Grace ?? StatusWait.DefaultGrace, a.StartedAfter, a.Expect, a.Any);
            return await new StatusWait(inputs.Reader, inputs.Host, inputs.Output, wait).RunAsync(first, read).ConfigureAwait(false);
        }

        StatusWatch.Settings watch = new(interval, a.StartedAfter, a.Bell, Color: string.IsNullOrEmpty(inputs.Env.Variable("NO_COLOR")));
        return await new StatusWatch(inputs.Reader, inputs.Host, inputs.Output, watch).RunAsync(first, read).ConfigureAwait(false);
    }

    private static async Task<int> WhoamiAsync(TextWriter output, bool key, OwnerKeys.Caller caller)
    {
        if (caller.Keys.Count is 0 || caller.Key is null)
        {
            await output.WriteLineAsync("lean-worker status: " + NoCaller).ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<string> keys = key ? [caller.Key] : caller.Keys;
        foreach (string k in keys)
        {
            await output.WriteLineAsync(TerminalText.Clean(k, OwnerKeys.MaxLength)).ConfigureAwait(false);
        }

        return 0;
    }

    internal static StatusArgs Parse(string[] args) => StatusOptions.Parse(args);

    /// <summary>
    /// A duration: a whole number and a unit, s, m, h or d (e.g. 90m, 24h, 7d).
    /// </summary>
    internal static TimeSpan ParseDuration(string text) => StatusOptions.ParseDuration(text);
}
