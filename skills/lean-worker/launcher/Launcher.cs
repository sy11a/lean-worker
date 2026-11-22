// LeanWorker: runs one well-scoped task in a separate, minimal-context worker process (Claude Code `claude -p`
// or opencode `opencode run`) and prints a compact report with token usage and cost.
//
// Settings resolve in this order: command-line option > profile in <runs-root>/profiles.json > built-in default.
// Project notes (<runs-root>/project.md) are given to every worker unless --no-project-notes;
// a per-task --system file is appended after them.
//
// Spend is metered live from the worker's stream with the price book (prices.json). Past the wrap-up share of the
// budget a pre-tool hook blocks every tool call, so the worker's last message is a handoff; at the budget the
// launcher stops the worker.
//
// Exit codes: 0 = worker finished without error (or a gate chain ended clean), 1 = worker reported an error
// (or the provider's output limit truncated the run),
// 2 = launcher failed, 3 = worker wrapped up near its budget and left a handoff (continue with
// --continue-from <run-dir>), 4 = a gate chain ended stuck, 5 = a gate chain ended in an error.

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class Launcher
{
    internal static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    internal static readonly string[] PermissionModes = ["acceptEdits", "dontAsk", "plan", "manual", "auto", "bypassPermissions"];
    // git flags that write files or run programs whatever prefix allowed the command (deny beats allow).
    internal static readonly string[] DeniedFloor = ["Bash(git *--output*)", "Bash(git *--ext-diff*)", "Bash(git *--textconv*)"];
    internal const string ContinuationHeading = "## Continuation (lean-worker)";
    internal const string GateHeading = "## Gate report (lean-worker)";
    public const int RunSchemaVersion = 1;

    public static async Task<int> RunAsync(Options o)
    {
        if (o.Help)
        {
            await Console.Out.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return 0;
        }

        LaunchRun run = new(o, gateContext: null);
        int code = await run.RunAsync().ConfigureAwait(false);
        while (run.LastGate is { Decision: GateChain.Continue })
        {
            // The next round compares against round 1's before-worker trust snapshot and its fixed
            // path list, so nothing that changed on disk since then can become the new baseline.
            // The one patch: the declared outputs this round's gate was allowed to rewrite, hashed
            // after its clean after-gate check (PostGateOutputs) — they replace exactly those keys
            // of round 1's snapshot, everything else keeps round 1's baseline.
            IReadOnlyList<GateSpec> specs = run.InitialGateSpecs ?? throw new LaunchException("gate chain has no spec");
            GateContext nextContext = new(
                ChainId: run.ChainId,
                Round: run.Round + 1,
                Counts: [.. run.Counts],
                ChainCostUsd: run.ChainCostUsd,
                OriginalTask: run.OriginalTask,
                OriginalName: run.OriginalName,
                Feedback: run.LastGate.Result,
                Specs: specs,
                TrustSnapshot: run.TrustSnapshot ?? throw new LaunchException("gate chain has no trust snapshot"),
                TrustPaths: run.TrustPaths ?? throw new LaunchException("gate chain has no trust paths"),
                FixedSources: run.FixedSources ?? throw new LaunchException("gate chain has no trust path sources"),
                GatePathNames: run.GatePathNames ?? throw new LaunchException("gate chain has no gate PATH name sets"),
                PostGateOutputs: run.PostGateOutputs);
            run = new LaunchRun(o, gateContext: nextContext);
            code = await run.RunAsync().ConfigureAwait(false);
        }

        return ChainExitCode(run.LastGate, code);
    }

    /// <summary>
    /// The launcher's exit code after a round: the round's own code, or the gate chain's when the
    /// round's gate ended the chain (0 clean, 4 stuck, 5 error).
    /// </summary>
    internal static int ChainExitCode(GateOutcome? lastGate, int code)
    {
        if (lastGate is null)
        {
            return code;
        }

        return lastGate.Decision switch
        {
            GateChain.Clean => 0,
            GateChain.Stuck => 4,
            GateChain.Error => 5,
            _ => code,
        };
    }

    /// <summary>
    /// metered | subscription. Anthropic's "auto" is a subscription when the worker uses the login, not a key.
    /// </summary>
    public static string Billing(Provider p, string runtime, bool hasKey)
    {
        return p.Billing switch
        {
            "auto" when runtime is "claude" => hasKey ? "metered" : "subscription",
            "auto" => "metered",
            var b => b,
        };
    }

    internal static string? NextInChain(List<string> chain, string provider, string model)
    {
        int i = chain.FindIndex(c => PriceBook.Split(c) == (provider, model));
        return i >= 0 && i + 1 < chain.Count ? chain[i + 1] : null;
    }

    internal static JsonObject? QuotaDelta(QuotaReading? before, QuotaReading? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        JsonObject d = [];
        foreach (QuotaWindow w in after.Windows)
        {
            if (before.Windows.Find(b => b.Name == w.Name) is { } b)
            {
                d[w.Name] = w.Percent - b.Percent;
            }
        }

        return d;
    }

    /// <summary>
    /// Runs the worker (see <see cref="WorkerProcess.RunAsync"/>) and reports a worker stopped at the
    /// timeout with exit code -1, whatever code the kill left behind.
    /// </summary>
    internal static async Task<(int ExitCode, bool TimedOut, bool CapKilled)> RunWorkerAsync(Prepared prep, string stdin,
        string streamPath, string stderrPath, int timeoutMinutes, Func<string, bool> record, Func<string, bool> onLine,
        RunStatus? live = null)
    {
        (int exitCode, bool timedOut, bool capKilled) =
            await WorkerProcess.RunAsync(prep, stdin, streamPath, stderrPath, timeoutMinutes, record, onLine, live).ConfigureAwait(false);
        return (timedOut ? -1 : exitCode, timedOut, capKilled);
    }

    /// <summary>
    /// The worker's start info; see <see cref="WorkerProcess.StartInfo"/>.
    /// </summary>
    internal static ProcessStartInfo StartInfo(Prepared prep) => WorkerProcess.StartInfo(prep);

    /// <summary>
    /// The claude runtime reaches Anthropic and any provider with an Anthropic-compatible endpoint; others need opencode.
    /// </summary>
    internal static string DefaultRuntime(Provider p) => p.Name is "anthropic" || p.AnthropicBaseUrl is not null ? "claude" : "opencode";

    /// <summary>
    /// Searches PATH for <paramref name="command"/> and returns the first existing candidate as an
    /// absolute path. A relative PATH entry (e.g. <c>node_modules/.bin</c>) would otherwise yield a
    /// relative result, which .NET on Unix resolves against the launcher's own directory when it
    /// starts a process, and which no absolute trust-set key could ever match. Null when no PATH
    /// entry has the file.
    /// </summary>
    public static string? FindOnPath(string command)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                // A relative PATH entry resolves against the launcher's working directory — the same
                // directory the gate runs in — so the returned path is always absolute.
                candidate = Path.GetFullPath(Path.Combine(dir.Trim('"'), command));
            }
            catch (ArgumentException)
            {
                // A PATH entry that cannot form a valid path is skipped, like a non-existent one.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// First 12 hex chars of the SHA-256 of the UTF-8 bytes; the path is the same in every run of identical content.
    /// </summary>
    internal static string Sha12(string content)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Json.Utf8.GetBytes(content));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// A run name that is safe in a path: the letters, digits, dots, underscores and dashes of the
    /// name itself, every other character a dash. The run directory and the worktree of a run are
    /// named after it.
    /// </summary>
    internal static string SafeName(string name) =>
        new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')]);

    /// <summary>
    /// Makes the run dir &lt;runs&gt;/&lt;baseName&gt; (the stamp and the safe name) and returns its
    /// path; when an entry of that name is already there (another run of the same name in the same
    /// second, or anything else), &lt;baseName&gt;-&lt;launcherPid&gt; instead, and when that is there
    /// too, a launch error. The claim is the exclusive creation of the dir's task.md, so two
    /// launchers that make the same dir at the same moment never share it: the one that loses
    /// takes the next name. The stamp stays the first 15 characters of the name.
    /// </summary>
    internal static string ClaimRunDir(string runs, string baseName, int launcherPid)
    {
        _ = Directory.CreateDirectory(runs);
        string[] candidates = [Path.Combine(runs, baseName), Path.Combine(runs, baseName + "-" + launcherPid.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        foreach (string dir in candidates)
        {
            if (EntryExists(dir))
            {
                continue;
            }

            _ = Directory.CreateDirectory(dir);
            try
            {
                using FileStream claim = new(Path.Combine(dir, "task.md"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return dir;
            }
            catch (IOException) when (File.Exists(Path.Combine(dir, "task.md")))
            {
                // Another launcher made the same dir a moment earlier: the next name.
            }
        }

        throw new LaunchException($"run dir {candidates[0]} and {candidates[1]} already exist");
    }

    private static bool EntryExists(string path) => Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    internal static async Task<int> CountLinesAsync(string path)
    {
        int n = 0;
        await foreach (string _ in File.ReadLinesAsync(path, CancellationToken.None).ConfigureAwait(false))
        {
            n++;
        }
        return n;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically (temp + move), only if the file is absent.
    /// </summary>
    internal static void AtomicWrite(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, Json.Utf8);
            try { File.Move(temp, path); }
            catch (IOException) { /* lost the race; the other writer's content has the same hash */ }
        }
        finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> so a symlink planted at
    /// <paramref name="path"/> is replaced, not followed. The bytes go to a randomly named temp file
    /// in the same directory (an unpredictable name cannot be pre-planted), then a rename moves the
    /// temp over the path: rename swaps the directory entry, so an entry that is itself a symlink is
    /// replaced rather than written through. The temp file is deleted when anything fails.
    /// </summary>
    internal static async Task WritePlainAsync(string path, string content)
    {
        string temp = TempSibling(path);
        try
        {
            await File.WriteAllTextAsync(temp, content, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The synchronous twin of <see cref="WritePlainAsync"/> (temp sibling plus rename), for the
    /// stream thread's status writes.
    /// </summary>
    internal static void WritePlain(string path, string content)
    {
        string temp = TempSibling(path);
        try
        {
            File.WriteAllText(temp, content, Json.Utf8);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing like <c>new StreamWriter(path, append: false, encoding)</c>,
    /// but never follows a symlink planted at <paramref name="path"/>: the handle is created on a randomly
    /// named temp file in the same directory and the temp is renamed over the path while the handle is open,
    /// so the writer keeps writing the file the path now names (rename replaces the entry; the open handle
    /// follows the inode). See <see cref="WritePlainAsync"/> for the rationale. The temp file is deleted
    /// when the rename fails.
    /// </summary>
    internal static StreamWriter CreateWriter(string path, System.Text.Encoding encoding)
    {
        string temp = TempSibling(path);
        FileStream fs = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            fs.Dispose();
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return new StreamWriter(fs, encoding);
    }

    private static string TempSibling(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? ".", ".tmp-" + Guid.NewGuid().ToString("N"));
}