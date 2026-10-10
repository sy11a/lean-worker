// LeanWorker: a single run. Each phase is its own method; RunAsync drives them in order and returns the exit code.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal sealed class LaunchRun
{
    private readonly Options _o;

    // LoadProfileAsync
    private string _runsRoot = string.Empty;
    private JsonObject? _prevSummary;
    private string? _profileName;
    private JsonObject? _profile;

    // ResolveSettings (price book + runtime + chain)
    private PriceBook? _prices;
    private List<string> _notes = [];
    private string? _explicitRuntime;
    private string _runtimeName = string.Empty;
    private List<string> _chain = [];

    // ResolveSettings (options)
    private string _effort = string.Empty;
    private string? _variant;
    private List<string> _tools = [];
    private List<string> _allowed = [];
    private List<string> _denied = [];
    private List<string>? _writeScope;
    private decimal _budget;
    private decimal _wrapUpAt;
    private GateSpec? _gate;

    // ResolveSettings (hooks)
    private string _permissionMode = string.Empty;
    private string? _mcpConfig;
    private bool _keepHooks;
    private string _cacheTtl = string.Empty;

    // ValidateInputsAsync
    private bool _hasKey;

    // PickModel
    private string _provider = string.Empty;
    private string _model = string.Empty;
    private string _pickReason = string.Empty;
    private QuotaReading? _quotaBefore;

    // Model / runtime resolution
    private Provider? _providerInfo;
    private IRuntime? _runtime;
    private ModelTraits? _traits;

    // ResolveMode
    private string _mode = string.Empty;
    private bool _wrapUp;
    private string _billing = string.Empty;

    // PrepareTaskAsync
    private string _taskText = string.Empty;
    private string _name = string.Empty;
    private DateTimeOffset _started;
    public string RunDir { get; private set; } = string.Empty;

    // WriteSystemAsync
    private string? _runSystem;

    // ExecuteAsync
    private Meter? _meter;
    private readonly Outcome _outcome = new();
    private string _stderrPath = string.Empty;
    private int _exitCode;
    private bool _timedOut;
    private bool _capKilled;
    private TimeSpan _elapsed;
    private List<string>? _changed;
    private List<string>? _outOfScope;
    private QuotaReading? _quotaAfter;

    // Status
    private string _status = string.Empty;

    // Gate chain (round 1 has no GateContext; round N+1 builds it from round N's GateOutcome).
    /// <summary>
    /// The chain loop reads <c>LastGate</c> to decide whether to start the next round; the rest are the chain
    /// state at this round (chain id, round number, all counts so far, summed worker cost, original task text
    /// and name, and the round-1 GateSpec propagated to later rounds). All are null/empty/zero for the first
    /// round until the gate runs.
    /// </summary>
    public GateContext? GateContext { get; }
    public Gate.GateResult? GateResult { get; private set; }
    public GateOutcome? LastGate { get; private set; }
    public string ChainId { get; private set; } = string.Empty;
    public int Round { get; } = 1;
    public IReadOnlyList<int> Counts { get; private set; } = [];
    public decimal ChainCostUsd { get; private set; }
    public string OriginalTask { get; private set; } = string.Empty;
    public string OriginalName { get; private set; } = string.Empty;
    public GateSpec? InitialGateSpec { get; private set; }

    // Trust boundary. Round 1 collects the fixed part of the trust set (gate.trust globs, argv file
    // entries, the resolved gate executable, the prices file) and hashes everything before its worker;
    // both are frozen into GateContext, and every later round compares against round 1's snapshot
    // (before its worker, before its gate, after its gate). The fixed paths' per-path sources come
    // along, frozen too: they tell the report-path rule where a path is trusted from. Nothing is
    // excluded from a comparison by default — not even entries of the gate's own argv; only the
    // profile's declared gate.outputs leave the after-gate comparison, because the gate is expected
    // to write exactly those paths. After a clean after-gate check the declared outputs that are in
    // the trusted set are re-hashed (PostGateOutputs): the gate legitimately rewrote them after
    // round 1's baseline was taken, so the next round's baseline is round 1's snapshot with exactly
    // those keys replaced. Everything else keeps round 1's value.
    // The gate's PATH directories get the same treatment without hashing: round 1 records the entries
    // directly in each (the frozen GatePath, the GateTrust header's rule) into GatePathNames, and
    // every pre-gate and after-gate check compares the listing again — an added or removed name, a
    // changed length, mtime or link target, and any change of a directory's own state is a trust
    // violation.
    public GateTrust.Snapshot? TrustSnapshot { get; private set; }
    public IReadOnlyList<string>? TrustPaths { get; private set; }
    public IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>>? FixedSources { get; private set; }

    /// <summary>
    /// Round 1's listing of the gate PATH's directories, the baseline the pre-gate and
    /// after-gate listing checks compare against; frozen into the gate context for later rounds.
    /// Null when no gate PATH was resolved.
    /// </summary>
    public GateTrust.PathNamesSnapshot? GatePathNames { get; private set; }

    /// <summary>
    /// The declared gate outputs that are trusted inputs, re-hashed after this round's clean
    /// after-gate check. The chain loop hands it to the next round's <see cref="GateContext"/>, whose
    /// baseline is round 1's snapshot with exactly these keys replaced; null when there is no trust
    /// snapshot or no declared output in the trusted set.
    /// </summary>
    public GateTrust.Snapshot? PostGateOutputs { get; private set; }

    // BuildSummary (stats)
    private List<Usage> _calls = [];
    private JsonObject _tok = [];
    private long _first;
    private long _peak;
    private Usage? _firstCall;
    private double? _firstCallCacheReadShare;
    private int _hookChecks;
    private string? _next;
    private JsonObject _summary = [];
    private string _report = string.Empty;

    public LaunchRun(Options o, GateContext? gateContext = null)
    {
        _o = o;
        GateContext = gateContext;
        Round = gateContext?.Round ?? 1;
        Counts = gateContext?.Counts ?? [];
        ChainCostUsd = gateContext?.ChainCostUsd ?? 0m;
        ChainId = gateContext?.ChainId ?? string.Empty;
        OriginalTask = gateContext?.OriginalTask ?? string.Empty;
        OriginalName = gateContext?.OriginalName ?? string.Empty;
        InitialGateSpec = gateContext?.Spec;
    }

    public async Task<int> RunAsync()
    {
        await LoadProfileAsync().ConfigureAwait(false);
        ResolveSettings();
        await ValidateInputsAsync().ConfigureAwait(false);
        PickModel();
        ApplyModelTraits();
        await ResolveRepoTokenAsync().ConfigureAwait(false);
        ResolveMode();
        await SnapshotTrustAsync().ConfigureAwait(false);
        await PrepareTaskAsync().ConfigureAwait(false);
        await WriteSystemAsync().ConfigureAwait(false);
        await ExecuteAsync().ConfigureAwait(false);
        _status = Status();
        await RunGateAsync().ConfigureAwait(false);
        await ComputeSummaryStatsAsync().ConfigureAwait(false);
        BuildSummary();
        await WriteSummaryAsync().ConfigureAwait(false);
        await PrintAsync().ConfigureAwait(false);
        return _status switch { "success" => 0, "wrapped-up" => 3, _ => 1 };
    }

    private async Task LoadProfileAsync()
    {
        _runsRoot = _o.RunsRoot ?? ".lean-worker";
        JsonObject? prevSummary = null;
        if (_o.ContinueFrom is not null)
        {
            string prevSummaryPath = Path.Combine(_o.ContinueFrom, "summary.json");
            if (!File.Exists(prevSummaryPath) || !File.Exists(Path.Combine(_o.ContinueFrom, "task.md")))
            {
                throw new LaunchException($"--continue-from needs a finished run dir (summary.json + task.md): {_o.ContinueFrom}");
            }

            prevSummary = Json.ParseLenient(await File.ReadAllTextAsync(prevSummaryPath, CancellationToken.None).ConfigureAwait(false)).AsObject();
        }
        _prevSummary = prevSummary;
        JsonObject? profile = null;
        _profileName = _o.Profile ?? Json.Str(prevSummary, "profile");
        string profilesPath = Path.Combine(_runsRoot, "profiles.json");
        if (File.Exists(profilesPath))
        {
            JsonObject doc;
            try { doc = Json.ParseLenient(await File.ReadAllTextAsync(profilesPath, CancellationToken.None).ConfigureAwait(false)).AsObject(); }
            catch (Exception ex) { throw new LaunchException($"profiles.json is not valid JSON: {ex.Message}"); }
            _profileName ??= Json.Str(doc, "defaultProfile");
            if (_profileName is not null)
            {
                profile = doc["profiles"]?[_profileName] as JsonObject
                    ?? throw new LaunchException($"profile '{_profileName}' not found in {profilesPath}");
            }
        }
        else if (_profileName is not null && _o.ContinueFrom is null)
        {
            throw new LaunchException($"--profile given but {profilesPath} does not exist");
        }
        _profile = profile;
    }

    private void ResolveSettings()
    {
        ResolvePriceBookAndChain();
        ResolveOptions();
        ResolveHookSettings();
    }

    // The explicit --prices / profile "prices" path (null = the default <runs-root>/prices.json, which the
    // trust walk already covers); hashed with the trusted set when it lies outside the runs root.
    private string? _pricesFile;

    private void ResolvePriceBookAndChain()
    {
        _pricesFile = _o.PricesFile ?? Json.Str(_profile, "prices");
        _prices = PriceBook.Load(_runsRoot, _pricesFile);
        _notes = [.. _prices.Warnings];
        // An explicit runtime holds for every model in the chain; otherwise each model gets the runtime its provider allows.
        _explicitRuntime = _o.Runtime ?? Json.Str(_profile, "runtime");
        if (_explicitRuntime is not null)
        {
            _ = Runtimes.Get(_explicitRuntime); // validates the name before any quota read
        }

        _runtimeName = _explicitRuntime ?? "claude";
        if (_o.Model is not null)
        {
            _chain = [_o.Model];
        }
        else if (_profile?["model"] is JsonArray arr)
        {
            _chain = new List<string>([.. arr.Select(x => x!.GetValue<string>())]);
        }
        else
        {
            _chain = [Json.Str(_profile, "model") ?? "claude-sonnet-5"];
        }
    }

    private void ResolveOptions()
    {
        _effort = _o.Effort ?? Json.Str(_profile, "effort") ?? "medium";
        _variant = _o.Variant ?? Json.Str(_profile, "variant");
        _tools = _o.Tools ?? Json.StrList(_profile, "tools") ?? ["Read", "Edit", "Write", "Glob", "Grep", "Bash"];
        _allowed = _o.AllowedTools.Count > 0 ? _o.AllowedTools : Json.StrList(_profile, "allowedTools") ?? [];
        // The deny floor + the profile's optional deniedTools (no duplicates, floor first); nothing removes the floor.
        if (_tools.Contains("Bash", StringComparer.OrdinalIgnoreCase))
        {
            _denied = [.. Launcher.DeniedFloor];
            foreach (string entry in Json.StrList(_profile, "deniedTools") ?? [])
            {
                if (!_denied.Contains(entry, StringComparer.Ordinal))
                {
                    _denied.Add(entry);
                }
            }
        }
        // The paths the task may write; a continuation keeps its original run's scope.
        _writeScope = _o.WriteScope.Count > 0 ? _o.WriteScope
            : Json.StrList(_profile, "writeScope") ?? Json.StrList(_prevSummary, "write_scope");
        _budget = _o.MaxBudgetUsd ?? Json.Dec(_profile, "maxBudgetUsd") ?? 2m;
        // Share of the budget after which the wrap-up hook blocks tools; 0 turns it off.
        _wrapUpAt = _o.WrapUpAt ?? Json.Dec(_profile, "wrapUpAt") ?? 0.8m;
        if (_wrapUpAt is < 0 or >= 1)
        {
            throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"invalid wrap-up share {_wrapUpAt} (0 = off, else below 1)"));
        }

        ResolveGate();
    }

    private void ResolveGate()
    {
        // Round N+1: use the round-1 spec the launcher passed in. The worker cannot change the gate's
        // command for the next round, so we never re-read profiles.json here.
        GateSpec spec;
        if (InitialGateSpec is not null)
        {
            spec = InitialGateSpec;
        }
        else
        {
            if (_o.NoGate)
            {
                return;
            }

            GateSpec? fromProfile = GateSpec.FromProfile(_profile, Directory.GetCurrentDirectory());
            if (fromProfile is null)
            {
                if (_o.GateMaxRounds is not null)
                {
                    throw new LaunchException("--gate-max-rounds needs a profile with a gate");
                }

                return;
            }

            spec = fromProfile;
            if (_o.GateMaxRounds is { } rounds)
            {
                if (rounds <= 0)
                {
                    throw new LaunchException("--gate-max-rounds must be a positive integer");
                }

                spec = spec with { MaxRounds = rounds };
            }
        }

        _gate = spec with { MaxTotalUsd = spec.MaxTotalUsd ?? (3m * _budget) };
        FreezeRound1GateResolution();
        InitialGateSpec = _gate;
        NoteGate(_gate);
    }

    /// <summary>
    /// Round 1's one resolution of the gate's argv[0] and PATH, frozen on the spec the chain carries
    /// into every round. Later rounds return immediately: the frozen values are reused unchanged.
    /// </summary>
    private void FreezeRound1GateResolution()
    {
        GateSpec gate = NonNull(_gate);
        // Round 1 resolves the gate's argv[0] exactly once, to the absolute path the trust check
        // hashes as TrustSource.Executable (CollectFixedPathsAsync adds exactly this string); later
        // rounds reuse the value frozen on the round-1 spec, which the chain carries in
        // GateContext.Spec. Gate.BuildStartInfo starts exactly this path — there is no PATH search at
        // gate time — so an executable the worker plants in an earlier PATH directory after this
        // lookup can never be swapped in for a later round. An unresolvable argv[0] is a launch error
        // (exit 2) before the worker starts.
        //
        // The same freeze covers the gate's PATH: built once from the launcher's PATH by keeping only
        // the absolute entries (the GateTrust header's rule), it is what every round hands the gate
        // as PATH and what the listing trust check watches; a PATH re-read at gate time would let a
        // later round pick up directories the worker created. A PATH with no absolute entry left is
        // refused here (exit 2): an empty PATH would make the gate resolve bare command names in its
        // current directory. The early return below requires both frozen values: a spec carrying only
        // one of them (a hand-built spec) is resolved the same way round 1 is.
        if (gate.ResolvedExecutable is not null && gate.GatePath is not null)
        {
            return;
        }

        _gate = gate with
        {
            ResolvedExecutable = GateTrust.ResolveExecutableStrict(gate.Command, Directory.GetCurrentDirectory()),
            GatePath = GateTrust.BuildGatePath(),
        };
    }

    private void NoteGate(GateSpec? spec)
    {
        if (spec is null)
        {
            return;
        }

        _notes.Add(string.Create(CultureInfo.InvariantCulture, $"gate: {string.Join(' ', spec.Command)} (rounds {spec.MaxRounds}, ${spec.MaxTotalUsd:0.####})"));
    }

    private void ResolveHookSettings()
    {
        _permissionMode = _o.PermissionMode ?? Json.Str(_profile, "permissionMode") ?? "acceptEdits";
        _mcpConfig = _o.McpConfig ?? Json.Str(_profile, "mcpConfig");
        // Lean mode keeps the user's hooks, plugins and settings out of the worker unless the profile keeps them.
        _keepHooks = _o.KeepHooks || Json.Bool(_profile, "keepHooks");
        // Worker calls follow each other within seconds, so the 5-minute cache is enough. A subscription login
        // would otherwise write the cache with the 1-hour TTL, which costs 2x base input instead of 1.25x.
        _cacheTtl = _o.CacheTtl ?? Json.Str(_profile, "cacheTtl") ?? "5m";
        if (_cacheTtl is not ("5m" or "1h" or "default"))
        {
            throw new LaunchException($"invalid cache TTL '{_cacheTtl}' (5m | 1h | default)");
        }

        if (!Launcher.Efforts.Contains(_effort, StringComparer.Ordinal))
        {
            throw new LaunchException($"invalid effort '{_effort}'");
        }

        if (Launcher.PermissionModes.Contains(_permissionMode, StringComparer.Ordinal))
        {
            return;
        }

        throw new LaunchException($"invalid permission mode '{_permissionMode}'");
    }

    private async Task ValidateInputsAsync()
    {
        if (_o.TaskFile is null && _o.ContinueFrom is null)
        {
            throw new LaunchException("--task <file> or --continue-from <run-dir> is required");
        }

        if (_o.TaskFile is not null && !File.Exists(_o.TaskFile))
        {
            throw new LaunchException($"task file not found: {_o.TaskFile}");
        }

        if (_o.SystemFile is not null && !File.Exists(_o.SystemFile))
        {
            throw new LaunchException($"system file not found: {_o.SystemFile}");
        }

        if (_mcpConfig is not null && !File.Exists(_mcpConfig))
        {
            throw new LaunchException($"MCP config not found: {_mcpConfig}");
        }

        if (_o.ClaudeSettings is not null && !File.Exists(_o.ClaudeSettings))
        {
            throw new LaunchException($"settings file not found: {_o.ClaudeSettings}");
        }
        // An API key: the environment, or an apiKeyHelper in --claude-settings. Any other settings file is not a key.
        _hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                 || (_o.ClaudeSettings is not null && Json.ParseLenient(await File.ReadAllTextAsync(_o.ClaudeSettings, CancellationToken.None).ConfigureAwait(false))["apiKeyHelper"] is not null);
    }

    private void PickModel()
    {
        // Pick the model: the first in the chain with quota headroom.
        PriceBook prices = NonNull(_prices);
        string provider = string.Empty;
        string model = string.Empty;
        string pickReason = string.Empty;
        QuotaReading? quotaBefore = null;
        for (int i = 0; i < _chain.Count; i++)
        {
            (string? prov, string? mdl) = PriceBook.Split(_chain[i]);
            Provider info = prices.Provider(prov);
            provider = prov ?? string.Empty;
            model = mdl ?? string.Empty;
            pickReason = (_chain.Count, i) switch
            {
                (0, _) or (1, _) => string.Empty,
                (_, 0) => "first in chain",
                _ => "next in chain",
            };
            quotaBefore = null;
            _runtimeName = _explicitRuntime ?? Launcher.DefaultRuntime(info);
            if (Launcher.Billing(info, _runtimeName, _hasKey) is not "subscription" || info.Quota is null)
            {
                break;
            }

            try
            {
                quotaBefore = Quota.Read(info);
                (bool ok, string? why) = Quota.Headroom(info, quotaBefore);
                pickReason = why ?? string.Empty;
                if (ok)
                {
                    break;
                }

                if (i == _chain.Count - 1)
                {
                    _notes.Add($"every model in the chain is over its quota threshold; using the last ({why})");
                }
                else
                {
                    _notes.Add($"skipped {_chain[i]}: {why}");
                }
            }
            catch (Exception ex) when (Quota.IsReadFailure(ex))
            {
                _notes.Add($"quota check for {prov} failed ({ex.Message}); assuming headroom");
                break;
            }
        }
        _provider = provider;
        _model = model;
        _pickReason = pickReason;
        _quotaBefore = quotaBefore;
    }

    private void ApplyModelTraits()
    {
        PriceBook prices = NonNull(_prices);
        _providerInfo = prices.Provider(_provider);
        _runtime = Runtimes.Get(_runtimeName);
        if (_explicitRuntime is null && _runtimeName is not "claude")
        {
            _notes.Add($"runtime {_runtimeName}: provider {_provider} has no Anthropic-compatible endpoint in the price book");
        }
        // What the model needs, whichever provider serves it: extra pre-approved commands and a note.
        ModelTraits? traits = prices.Traits(_model);
        if (traits is not null)
        {
            List<string> added = new([.. traits.AllowedTools.Where(t => !_allowed.Contains(t))]);
            if (added.Count > 0 && _tools.Contains("Bash", StringComparer.OrdinalIgnoreCase))
            {
                _allowed = new List<string>([.. _allowed, .. added]);
            }
            else
            {
                added.Clear();
            }

            _notes.Add($"model traits {traits.Key}: {(added.Count > 0 ? $"+{added.Count} allowed command pattern(s)" : "no allowlist change")}" +
                      (traits.Note is { Length: > 0 } tn ? $"; {tn}" : string.Empty));
        }
        _traits = traits;
        _ = prices.Resolve(_provider, _model, out string? priceNote); // fails early under unknownModel: "error"
        if (priceNote is null)
        {
            return;
        }

        _notes.Add(priceNote);
    }

    private async Task ResolveRepoTokenAsync()
    {
        string? root = await RepoToken.RootAsync(Directory.GetCurrentDirectory()).ConfigureAwait(false);
        _allowed = RepoToken.Expand(_allowed, root);
        _denied = RepoToken.ExpandDenied(_denied, root);
    }

    private void ResolveMode()
    {
        Provider providerInfo = NonNull(_providerInfo);
        // claude runtime: bare = `claude --bare` (API key only, skips all hooks, so no wrap-up);
        // lean = the same minimal profile from flags, for a subscription login, a key, or another provider.
        string mode = "n/a";
        if (_runtimeName is "claude")
        {
            mode = _o.Mode switch
            {
                "auto" => _provider is not "anthropic" || _wrapUpAt > 0 || !_hasKey ? "lean" : "bare",
                "bare" or "lean" => _o.Mode,
                _ => throw new LaunchException($"invalid mode '{_o.Mode}' (auto | bare | lean)"),
            };
            if (mode is "bare" && (!_hasKey || _provider is not "anthropic"))
            {
                throw new LaunchException("--mode bare needs ANTHROPIC_API_KEY (or --claude-settings with an apiKeyHelper) and an Anthropic model: --bare never reads " +
                                          "OAuth or the keychain. With a subscription login (e.g. Enterprise), use --mode lean or leave --mode auto.");
            }
        }
        _mode = mode;
        _wrapUp = _wrapUpAt > 0 && mode is not "bare";
        if (_wrapUpAt > 0 && mode is "bare")
        {
            _notes.Add("bare mode skips hooks, so wrap-up is off; the budget is still enforced");
        }

        _billing = Launcher.Billing(providerInfo, _runtimeName, _hasKey);
    }

    private async Task PrepareTaskAsync()
    {
        string taskText;
        string name;
        if (GateContext is not null)
        {
            // Gate round: original task text (cut at any continuation/gate headings) plus a fresh gate section.
            taskText = BuildGateTaskText(GateContext);
            name = $"{GateContext.OriginalName}-gate{GateContext.Round.ToString(CultureInfo.InvariantCulture)}";
        }
        else if (_o.ContinueFrom is not null)
        {
            // Fresh worker, not a resumed session: the original task plus the previous worker's report.
            string prevTask = await File.ReadAllTextAsync(Path.Combine(_o.ContinueFrom, "task.md"), Json.Utf8, CancellationToken.None).ConfigureAwait(false);
            int cut = prevTask.IndexOf(Launcher.ContinuationHeading, StringComparison.Ordinal);
            if (cut >= 0)
            {
                prevTask = prevTask[..cut];
            }

            cut = prevTask.IndexOf(Launcher.GateHeading, StringComparison.Ordinal);
            if (cut >= 0)
            {
                prevTask = prevTask[..cut];
            }

            string prevReport = File.Exists(Path.Combine(_o.ContinueFrom, "report.md")) ? (await File.ReadAllTextAsync(Path.Combine(_o.ContinueFrom, "report.md"), Json.Utf8, CancellationToken.None).ConfigureAwait(false)).Trim() : string.Empty;
            string nl = Environment.NewLine;
            taskText = prevTask.TrimEnd() + nl + nl + Launcher.ContinuationHeading + nl + nl +
                       $"A previous worker on this task stopped before finishing (status: {Json.Str(_prevSummary, "status")}). Its report is below. " +
                       "Check the current state first (for example `git status` and `git diff --stat`) and do not redo finished work." +
                       nl + nl + (prevReport.Length > 0 ? prevReport : "(the previous worker left no report)") + nl;
            name = _o.Name ?? Json.Str(_prevSummary, "name") ?? "continuation";
        }
        else
        {
            string taskPath = Path.GetFullPath(_o.TaskFile!);
            taskText = await File.ReadAllTextAsync(taskPath, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
            name = _o.Name ?? new DirectoryInfo(Path.GetDirectoryName(taskPath)!).Name;
        }
        _taskText = taskText;
        _name = name;
        if (GateContext is null)
        {
            // Round 1: the chain id is this run's directory; the original task and name come from the file.
            OriginalTask = taskText;
            OriginalName = name;
        }
        string safeName = new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')]);
        _started = DateTimeOffset.Now;
        RunDir = Path.Combine(_runsRoot, "runs", string.Create(CultureInfo.InvariantCulture, $"{_started:yyyyMMdd-HHmmss}-{safeName}"));
        if (GateContext is null)
        {
            // Round 1 anchors the chain; later rounds inherit round 1's run-dir name as their chain id.
            ChainId = Path.GetFileName(RunDir);
        }
        _ = Directory.CreateDirectory(RunDir);
        await File.WriteAllTextAsync(Path.Combine(RunDir, "task.md"), taskText, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
    }

    private static string BuildGateTaskText(GateContext context)
    {
        string nl = Environment.NewLine;
        string baseText = context.OriginalTask ?? string.Empty;
        int cut = baseText.IndexOf(Launcher.ContinuationHeading, StringComparison.Ordinal);
        if (cut >= 0)
        {
            baseText = baseText[..cut];
        }

        cut = baseText.IndexOf(Launcher.GateHeading, StringComparison.Ordinal);
        if (cut >= 0)
        {
            baseText = baseText[..cut];
        }

        baseText = baseText.TrimEnd();
        Gate.GateResult feedback = context.Feedback;
        int findings = feedback.Count ?? 0;
        int previousRound = context.Round - 1;
        string reportPath = feedback.ReportPath is { Length: > 0 } ? feedback.ReportPath : "(no report)";
        string instruction = string.Create(CultureInfo.InvariantCulture,
            $"The previous worker finished, but the gate command still reports {findings} finding(s) (round {previousRound.ToString(CultureInfo.InvariantCulture)}). Fix them; do not redo finished work. Check the current state first (git status, git diff --stat).");
        return baseText + nl + nl + Launcher.GateHeading + nl + nl + instruction + nl + nl +
               $"Full report: {reportPath}" + nl + nl + feedback.Feedback;
    }

    private async Task WriteSystemAsync()
    {
        // The worker's system notes = project notes + per-task system file. The path passed to the worker is
        // content-addressed, so it is the same in every run with identical content: opencode prints the file
        // path into the system prompt, and a per-run path breaks Anthropic's prompt cache for repeated tasks.
        List<string> parts = [];
        string projectNotes = Path.Combine(_runsRoot, "project.md");
        if (!_o.NoProjectNotes && File.Exists(projectNotes))
        {
            parts.Add(await File.ReadAllTextAsync(projectNotes, Json.Utf8, CancellationToken.None).ConfigureAwait(false));
        }

        if (_o.SystemFile is not null)
        {
            parts.Add(await File.ReadAllTextAsync(_o.SystemFile, Json.Utf8, CancellationToken.None).ConfigureAwait(false));
        }

        string? runSystem = null;
        if (parts.Count > 0)
        {
            string content = string.Join(Environment.NewLine + Environment.NewLine, parts);
            string sha12 = Launcher.Sha12(content);
            runSystem = Path.GetFullPath(Path.Combine(_runsRoot, "system", $"{sha12}.md"));
            Launcher.AtomicWrite(runSystem, content);
            await File.WriteAllTextAsync(Path.Combine(RunDir, "system.md"), content, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
        }
        _runSystem = runSystem;
    }

    private async Task ExecuteAsync()
    {
        Provider providerInfo = NonNull(_providerInfo);
        IRuntime runtime = NonNull(_runtime);
        PriceBook prices = NonNull(_prices);
        RunSpec spec = new(RunDir, _provider, _model, _effort, _variant, _tools, _allowed, _denied, _budget, _wrapUp, _permissionMode,
            _mcpConfig, _runSystem, _o.ReplaceSystemPrompt, _mode, _cacheTtl, _o.KeepClaudeMd, _o.KeepMemory, _keepHooks,
            !_o.NoUserEnv, _o.ClaudeSettings, providerInfo);
        Prepared prepared = runtime.Prepare(spec);
        // Locals declared before the try: needed by code after the try and the lambda inside.
        _meter = new Meter(prices, _provider, RunDir, _budget, _wrapUp ? _wrapUpAt : null, Meter.HandoffInstruction);
        Meter meter = _meter;
        string streamPath = Path.Combine(RunDir, "stream.jsonl");
        _stderrPath = Path.Combine(RunDir, "stderr.txt");
        WriteScope.Snapshot? treeBefore;
        Outcome outcome = _outcome;
        string model = _model;
        int exitCode;
        bool timedOut;
        bool capKilled;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(RunDir, "command.txt"), prepared.CommandText, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
            treeBefore = await WriteScope.TakeAsync(Directory.GetCurrentDirectory(), _runsRoot).ConfigureAwait(false);
            (exitCode, timedOut, capKilled) = await Launcher.RunWorkerAsync(prepared, _taskText, streamPath, _stderrPath, _o.TimeoutMinutes, line => runtime.Record(line), line =>
                OnWorkerLine(line, runtime, outcome, meter, model)).ConfigureAwait(false);
        }
        finally
        {
            RemoveScratch(prepared.ScratchDirectory);
        }
        _exitCode = exitCode;
        _timedOut = timedOut;
        _capKilled = capKilled;
        runtime.Finish(outcome, _exitCode);
        _elapsed = DateTimeOffset.Now - _started;
        WriteScope.Snapshot? treeAfter = treeBefore is null ? null : await WriteScope.TakeAsync(treeBefore.Root, _runsRoot).ConfigureAwait(false);
        _changed = treeBefore is null || treeAfter is null ? null : WriteScope.Changed(treeBefore, treeAfter);
        _outOfScope = _changed is null || _writeScope is null ? null : [.. _changed.Where(f => !WriteScope.InScope(f, _writeScope))];
        _notes.AddRange(_meter.Notes);

        QuotaReading? quotaAfter = null;
        if (_quotaBefore is not null)
        {
            try { quotaAfter = Quota.Read(providerInfo); }
            catch (Exception ex) when (Quota.IsReadFailure(ex)) { _notes.Add($"quota re-read failed: {ex.Message}"); }
        }
        _quotaAfter = quotaAfter;
    }

    private static bool OnWorkerLine(string line, IRuntime runtime, Outcome outcome, Meter meter, string model)
    {
        if (Json.TryParseObject(line) is not { } obj)
        {
            return false;
        }

        Usage? u = runtime.Parse(obj, outcome);
        return u is not null && meter.Add(u.Model.Length > 0 ? u : u with { Model = model });
    }

    private async Task SnapshotTrustAsync()
    {
        if (_gate is null)
        {
            return;
        }

        if (GateContext is not null)
        {
            // Round N+1: compare against round 1's before-worker snapshot and reuse its fixed path
            // list, so whatever a tampering process left on disk when this round started cannot
            // become the new baseline. Only the walks meant to catch new files (runs root, config
            // walk) are re-collected, inside RunGateAsync's checks. Round 1's gate.trust warnings
            // ride along on the frozen spec: gate.json records them, the result block notes them.
            // The one exception to "round 1's values": the declared outputs the previous round's
            // gate legitimately rewrote (PostGateOutputs, hashed after its clean after-gate check)
            // replace those keys, or this round's pre-gate check would fail on the gate's own
            // round-1 write.
            TrustSnapshot = GateContext.PostGateOutputs is null
                ? GateContext.TrustSnapshot
                : MergePostGateOutputs(NonNull(GateContext.TrustSnapshot), GateContext.PostGateOutputs);
            TrustPaths = GateContext.TrustPaths;
            FixedSources = GateContext.FixedSources;
            GatePathNames = GateContext.GatePathNames;
            if (_gate?.Warnings is { Count: > 0 } frozen)
            {
                _notes.AddRange(frozen);
            }

            return;
        }

        // Round 1: hash the trusted files before the worker runs — and before the run directory is
        // created, so an exit-2 refusal below (a declared output that is a trusted input, a bad
        // trusted value) leaves no half-made run directory behind. Nothing the launcher writes
        // until the worker starts is in the trust set anyway: task.md, system.md and command.txt
        // live in the run directory, the content-addressed system notes under system/, and the
        // runs-root walk skips runs/ and system/. The fixed part of the list (gate.trust globs,
        // argv entries, the resolved executable) is resolved once and reused for every later hash
        // in the whole chain. RunGateAsync hashes again before and after the gate and refuses the
        // gate if anything changed (a worker could otherwise swap the gate command or its report
        // path).
        TrustPaths = await CollectFixedPathsAsync().ConfigureAwait(false);
        (List<string> snapshotPaths, IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>> snapshotSources) =
            await CollectCheckPathsAsync(_noExclusions).ConfigureAwait(false);
        // A declared output that is also a trusted input is a configuration mistake, not tampering:
        // refuse it here, before the worker starts, as a launch error (exit 2).
        await ValidateDeclaredOutputsAsync(snapshotSources).ConfigureAwait(false);
        TrustSnapshot = await GateTrust.HashAsync(snapshotPaths).ConfigureAwait(false);
        RefuseBadSnapshotValues(snapshotSources);
        // The gate PATH's directories: one listing each, frozen as the baseline the pre-gate
        // and after-gate checks compare against (the GateTrust header's rule), with the
        // inside-the-working-tree refusal round 1 makes before the worker runs.
        await FreezeGatePathNamesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Round 1's listing of the gate PATH's directories, frozen as the baseline the pre-gate and
    /// after-gate checks compare against (the GateTrust header's rule), plus the round-1 refusal of
    /// a PATH that reaches into the working tree (<see cref="RefuseGatePathInsideWorkingTree"/>).
    /// FreezeRound1GateResolution set both ResolvedExecutable and GatePath (or refused the launch),
    /// so this is non-null for every round-1 gate. Later rounds reuse the frozen listing and never
    /// re-run the refusal. Runs before the run directory is created, so a refusal (exit 2) leaves
    /// no half-made run directory behind.
    /// </summary>
    private async Task FreezeGatePathNamesAsync()
    {
        string? gatePath = NonNull(_gate).GatePath;
        if (gatePath is null)
        {
            return;
        }

        GatePathNames = GateTrust.SnapshotPathNames(GateTrust.SplitGatePath(gatePath));
        string cwd = Directory.GetCurrentDirectory();
        string? gitRoot = await RepoToken.RootAsync(cwd).ConfigureAwait(false);
        RefuseGatePathInsideWorkingTree(gitRoot ?? cwd, cwd);
    }

    /// <summary>
    /// Round-1 refusal of a gate PATH that reaches into the working tree: a PATH directory at or
    /// under the git root, the working directory or the runs root lets the worker (and anything it
    /// spawns) write the gate's own PATH, and a symlink entry whose final target lies there lets it
    /// rewrite what the link resolves to while the PATH directory's listing stays intact — the
    /// listing check would only notice after a full worker run, and the restore-mtime gap can hide
    /// it altogether. A PATH directory whose listing can be neither read nor confirmed missing is
    /// refused in its own right: an unreadable directory is invisible to every later listing
    /// comparison (baseline and current would both say <c>unreadable</c>), so a worker that can
    /// write it could plant a tool the gate would still run. Both sides of every
    /// inside-the-working-tree comparison are canonicalised (<see cref="GateTrust.Canonical"/>),
    /// so a PATH entry through a symlinked directory or a git root behind a symlinked home is judged
    /// by the files the paths really name, and a link on the way to a PATH directory or to a final
    /// target is refused in its own right when the link path itself sits in the tree: the worker can
    /// retarget it even though the resolution lands outside today. Like every other round-1 refusal
    /// this is a launch error (exit 2), thrown before the run directory is created; the frozen
    /// listing the later rounds compare against exists only when this check passed.
    /// </summary>
    private void RefuseGatePathInsideWorkingTree(string gitRoot, string workingDirectory)
    {
        // The three roots, canonicalised once for this check: every comparison below runs against
        // the same resolved trees.
        string gitRootCanonical = CanonicalRootOrRefuse(gitRoot, $"git root '{gitRoot}'").Path;
        string workingDirectoryCanonical = CanonicalRootOrRefuse(workingDirectory, $"working directory '{workingDirectory}'").Path;
        string runsRootCanonical = CanonicalRootOrRefuse(Path.GetFullPath(_runsRoot), $"runs root '{Path.GetFullPath(_runsRoot)}'").Path;
        string? gatePath = NonNull(_gate).GatePath;
        if (gatePath is not null)
        {
            foreach (string dir in GateTrust.SplitGatePath(gatePath))
            {
                GateTrust.CanonicalPath canonical = GateTrust.Canonical(dir);
                RefuseUnresolvedCanonical(canonical, $"gate PATH directory '{dir}'");
                if (InsideWorkingTree(canonical.Path, gitRootCanonical, workingDirectoryCanonical, runsRootCanonical))
                {
                    throw new LaunchException($"gate PATH directory '{dir}' is inside the working tree");
                }

                if (canonical.Links.FirstOrDefault(link => InsideWorkingTree(link, gitRootCanonical, workingDirectoryCanonical, runsRootCanonical)) is { } via)
                {
                    throw new LaunchException($"gate PATH directory '{dir}' resolves into the working tree via '{via}'");
                }
            }
        }

        RefuseGatePathListingDangers(gitRootCanonical, workingDirectoryCanonical, runsRootCanonical);
    }

    /// <summary>
    /// The second half of the round-1 gate PATH refusal, over the frozen listings: a directory whose
    /// listing can be neither read nor confirmed missing is refused in its own right (an unreadable
    /// directory is invisible to every later listing comparison — baseline and current would both
    /// say <c>unreadable</c> — so a worker that can write it could plant a tool the gate would still
    /// run; a missing directory the gate never sees entries from is fine), and a readable
    /// directory's symlink entries are judged like the directories themselves: a final target at or
    /// under the working tree, or reached through a link that sits there, is refused.
    /// </summary>
    private void RefuseGatePathListingDangers(string gitRootCanonical, string workingDirectoryCanonical, string runsRootCanonical)
    {
        if (GatePathNames is null)
        {
            return;
        }

        foreach (KeyValuePair<string, GateTrust.PathNameListing> listing in GatePathNames.Directories)
        {
            if (!listing.Value.Readable && listing.Value.Reason is not "missing")
            {
                throw new LaunchException($"gate PATH directory '{listing.Key}' cannot be listed: {listing.Value.Reason}");
            }

            if (!listing.Value.Readable)
            {
                continue;
            }

            foreach (KeyValuePair<string, GateTrust.PathEntry> entry in listing.Value.Entries)
            {
                if (entry.Value.FinalTarget is not { } target || target is "dangling" || target.StartsWith("unreadable", StringComparison.Ordinal))
                {
                    continue;
                }

                string entryPath = Path.Combine(listing.Key, entry.Key);
                GateTrust.CanonicalPath canonical = GateTrust.Canonical(entryPath);
                RefuseUnresolvedCanonical(canonical, $"gate PATH entry '{entryPath}'");
                if (InsideWorkingTree(canonical.Path, gitRootCanonical, workingDirectoryCanonical, runsRootCanonical))
                {
                    throw new LaunchException($"gate PATH entry '{entryPath}' links into the working tree: {target}");
                }

                if (canonical.Links.FirstOrDefault(link => InsideWorkingTree(link, gitRootCanonical, workingDirectoryCanonical, runsRootCanonical)) is { } via)
                {
                    throw new LaunchException($"gate PATH entry '{entryPath}' resolves into the working tree via '{via}'");
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> (canonical) is at or under any of the three canonical roots
    /// the round-1 refusal judges the gate PATH by.
    /// </summary>
    private static bool InsideWorkingTree(string path, string gitRoot, string workingDirectory, string runsRoot)
    {
        return GateTrust.IsAtOrUnder(path, gitRoot)
            || GateTrust.IsAtOrUnder(path, workingDirectory)
            || GateTrust.IsAtOrUnder(path, runsRoot);
    }

    /// <summary>
    /// The canonical form of a root for the round-1 refusals, or a launch error when the resolution
    /// gives up (a link loop): the check must not guess from a half-resolved path.
    /// </summary>
    private static GateTrust.CanonicalPath CanonicalRootOrRefuse(string path, string what)
    {
        GateTrust.CanonicalPath canonical = GateTrust.Canonical(path);
        RefuseUnresolvedCanonical(canonical, what);
        return canonical;
    }

    /// <summary>
    /// The launch error for a path whose canonical resolution gives up (<see cref="GateTrust.CanonicalPath.Unreadable"/>).
    /// </summary>
    private static void RefuseUnresolvedCanonical(GateTrust.CanonicalPath canonical, string what)
    {
        if (canonical.Resolved)
        {
            return;
        }

        throw new LaunchException($"{what} {canonical.Unreadable}");
    }

    /// <summary>
    /// The part of the trust set that is fixed for the whole chain: the resolved gate executable,
    /// the argv file entries that exist now, and the gate.trust literals and glob matches. Resolved
    /// once, before round 1's worker, so glob matches and argv entries never pick up files the
    /// worker or the gate create later. The executable enters as the spec's
    /// <c>ResolvedExecutable</c> — the exact string round 1 resolved and the gate will start — so
    /// the hashed path and the started path are the same value. The per-path sources are frozen
    /// with the list: the report-path rule asks where a path is trusted from, and a later round
    /// must not re-derive that from a tree the worker has already changed. A gate.trust glob that
    /// matched no file becomes a warning on the gate spec (recorded in gate.json) and a note in
    /// the result block.
    /// </summary>
    private async Task<List<string>> CollectFixedPathsAsync()
    {
        GateSpec gate = NonNull(_gate);
        string cwd = Directory.GetCurrentDirectory();
        string? gitRoot = await RepoToken.RootAsync(cwd).ConfigureAwait(false);
        string root = gitRoot ?? cwd;
        List<string> warnings = [];
        Dictionary<string, HashSet<GateTrust.TrustSource>> sources = new(GateTrust.PathComparer);
        List<string> paths = GateTrust.CollectChainPaths(_runsRoot, root, cwd, gate.Command, gate.Trust, _pricesFile, warnings, sources, gate.ResolvedExecutable);
        FixedSources = sources;
        if (warnings.Count > 0)
        {
            _gate = gate with { Warnings = warnings };
            InitialGateSpec = _gate;
            _notes.AddRange(warnings);
        }

        return paths;
    }

    /// <summary>
    /// The paths for one trust check: the chain's fixed list plus the volatile walks re-collected now
    /// (runs-root files and the config-file walk, which are meant to catch new files), minus the
    /// caller's exclusions — empty for the before-worker and before-gate checks (nothing is excluded
    /// by default, the gate's argv entries included), the declared gate outputs for the after-gate
    /// check (the gate is expected to write exactly those). Also returns the per-path sources for
    /// this check (round 1's frozen fixed-path sources, deep-copied, plus the volatile walks'
    /// sources recorded now), so the report-path rule can ask where a path is trusted from whether
    /// or not an exclusion dropped it from the path list.
    /// </summary>
    private async Task<(List<string> Paths, IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>> Sources)> CollectCheckPathsAsync(IReadOnlySet<string> exclusions)
    {
        string cwd = Directory.GetCurrentDirectory();
        string? gitRoot = await RepoToken.RootAsync(cwd).ConfigureAwait(false);
        string root = gitRoot ?? cwd;
        Dictionary<string, HashSet<GateTrust.TrustSource>> sources = new(GateTrust.PathComparer);
        foreach (KeyValuePair<string, HashSet<GateTrust.TrustSource>> entry in NonNull(FixedSources))
        {
            sources[entry.Key] = [.. entry.Value];
        }

        List<string> volatilePaths = GateTrust.CollectVolatilePaths(_runsRoot, cwd, root, sources);
        List<string> paths = [];
        HashSet<string> dedupe = new(GateTrust.PathComparer);
        foreach (string path in NonNull(TrustPaths).Concat(volatilePaths))
        {
            if (!exclusions.Contains(path) && dedupe.Add(path))
            {
                paths.Add(path);
            }
        }

        return (paths, sources);
    }

    /// <summary>
    /// Shared empty exclusion set: the before-worker snapshot and the pre-gate comparison exclude
    /// nothing. No argv entry is ever excluded by default — the old runnable-argv heuristic (argv
    /// position, execute bits) missed wrappers such as <c>env VAR=1 sh gate.sh</c>, where the report
    /// is neither argv[0] nor argv[1] and carries no execute bit, so guessing was dropped in favour
    /// of the profile's explicit <c>gate.outputs</c> declaration.
    /// </summary>
    private static readonly IReadOnlySet<string> _noExclusions = new HashSet<string>(GateTrust.PathComparer);

    /// <summary>
    /// The profile's declared <c>gate.outputs</c>, resolved against the git root (or the working
    /// directory when not in git) — the same anchor the trust entries resolve against. Absolute
    /// entries are used as-is, normalised by <see cref="ResolveOutputEntry"/>.
    /// </summary>
    private async Task<List<string>> DeclaredOutputPathsAsync()
    {
        GateSpec gate = NonNull(_gate);
        if (gate.Outputs is not { Count: > 0 })
        {
            return [];
        }

        string anchor = await OutputAnchorAsync().ConfigureAwait(false);
        List<string> resolved = new(gate.Outputs.Count);
        foreach (string entry in gate.Outputs)
        {
            resolved.Add(ResolveOutputEntry(entry, anchor));
        }

        return resolved;
    }

    /// <summary>
    /// The declared gate outputs as the exclusion set of the after-gate trust comparison: the gate
    /// is expected to write them, so they leave both sides of that one comparison. The before-worker
    /// snapshot and the pre-gate comparison exclude nothing (<see cref="_noExclusions"/>) — the worker
    /// must not change a declared output either; only the gate, after the pre-gate check, may.
    /// </summary>
    private async Task<IReadOnlySet<string>> DeclaredOutputExclusionsAsync()
    {
        List<string> outputs = await DeclaredOutputPathsAsync().ConfigureAwait(false);
        return outputs.Count is 0 ? _noExclusions : new HashSet<string>(outputs, GateTrust.PathComparer);
    }

    // gitRoot ?? cwd: the anchor CollectFixedPathsAsync resolved the fixed trust list against, so a
    // declared output and a trust entry name the same file the same way.
    private static async Task<string> OutputAnchorAsync()
    {
        string cwd = Directory.GetCurrentDirectory();
        return await RepoToken.RootAsync(cwd).ConfigureAwait(false) ?? cwd;
    }

    private static string ResolveOutputEntry(string entry, string anchor)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(anchor, entry));
        }
        catch (ArgumentException ex)
        {
            // Spec validation (non-empty, no globs, no ".." in a relative entry) has passed; this is a
            // path the OS layer still refuses. A configuration mistake → launch error, not a crash.
            throw new LaunchException($"profile gate.outputs entry '{entry}' is invalid: {ex.Message}");
        }
    }

    /// <summary>
    /// Refuses a round-1 snapshot value in a bad state (<see cref="GateTrust.IsBadValue"/>: nonregular
    /// or unreadable) — a directory, a device such as <c>/dev/null</c> named in the argv, a dangling
    /// link, one the open could not read in time. The chain would
    /// refuse it at the pre-gate check anyway ("is not a regular file", exit 5) — after a full worker
    /// run, on every launch. It is a configuration mistake, not tampering, so it fails here as a
    /// launch error (exit 2) naming the path and the trust sources it came from, before the worker
    /// starts — the same rule as the directory-literal refusal.
    /// </summary>
    private void RefuseBadSnapshotValues(IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>> sources)
    {
        foreach (KeyValuePair<string, string> entry in NonNull(TrustSnapshot).Hashes)
        {
            if (!GateTrust.IsBadValue(entry.Value))
            {
                continue;
            }

            string source = sources.TryGetValue(entry.Key, out HashSet<GateTrust.TrustSource>? set) && set.Count > 0
                ? string.Join(", ", set.Order().Select(s => SourceName(s)))
                : "unknown source";
            throw new LaunchException($"trusted path '{entry.Key}' ({source}) is not a regular file: {entry.Value}");
        }
    }

    private static string SourceName(GateTrust.TrustSource source)
    {
        return source switch
        {
            GateTrust.TrustSource.ArgvEntry => "argv entry",
            GateTrust.TrustSource.Executable => "the resolved gate executable",
            GateTrust.TrustSource.TrustEntry => "a gate.trust entry",
            GateTrust.TrustSource.PricesFile => "the prices file",
            GateTrust.TrustSource.ConfigWalk => "the config walk",
            GateTrust.TrustSource.RunsRootWalk => "the runs-root walk",
            _ => source.ToString(),
        };
    }

    /// <summary>
    /// Refuses a declared output that is also a trusted gate input from any source other than an
    /// argv entry: the resolved executable, a gate.trust literal or glob match, the prices file, a
    /// config-walk file (<c>global.json</c>, <c>nuget.config</c>, <c>dotnet-tools.json</c>,
    /// <c>.config/dotnet-tools.json</c>) or a runs-root file. The config walk adds its candidate
    /// paths whether or not they exist, so a declared output with a config-walk file name is
    /// already refused by that check — no file-name rule is needed on top. The one refusal that
    /// applies whether or not the file is in the trusted set yet is the runs-root one: the moment
    /// the gate creates an output at or under the runs root outside the launcher's own bookkeeping
    /// subtrees, a later round's runs-root walk picks it up and the pre-gate check fails on the
    /// launcher's own tree. Like the walk's skip set, <c>runs/</c>, <c>inbox/</c> and
    /// <c>system/</c> under the runs root are exempt — the walk never descends into them, so an
    /// output there can never trip a later round (and <c>inbox/</c> is a likely place for scratch
    /// output). An argv entry is allowed as a trusted input: the operator may pin the very file
    /// the gate writes to (e.g. <c>sh gate.sh artifacts/check.sarif</c> with the path in the argv
    /// and in <c>gate.outputs</c>) — it is compared before the gate, and only the after-gate
    /// comparison excludes it. Runs with the round-1 snapshot, before the run directory is
    /// created: a collision is a profile mistake, so it fails as a launch error (exit 2), not as a
    /// trust violation.
    /// </summary>
    private async Task ValidateDeclaredOutputsAsync(IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>> sources)
    {
        GateSpec gate = NonNull(_gate);
        if (gate.Outputs is not { Count: > 0 })
        {
            return;
        }

        string anchor = await OutputAnchorAsync().ConfigureAwait(false);
        string runsRoot = Path.GetFullPath(_runsRoot);
        // Both sides of the runs-root comparison are canonicalised: a declared output that reaches
        // the runs root through a symlink — or a bookkeeping subtree (runs/, inbox/, system/)
        // someone replaced with a symlink pointing out of the root — is judged by where the paths
        // really resolve.
        GateTrust.CanonicalPath runsRootCanonical = CanonicalRootOrRefuse(runsRoot, $"runs root '{runsRoot}'");
        GateTrust.CanonicalPath runsDirCanonical = CanonicalRootOrRefuse(Path.Combine(runsRoot, "runs"), $"runs root subtree '{Path.Combine(runsRoot, "runs")}'");
        GateTrust.CanonicalPath inboxDirCanonical = CanonicalRootOrRefuse(Path.Combine(runsRoot, "inbox"), $"runs root subtree '{Path.Combine(runsRoot, "inbox")}'");
        GateTrust.CanonicalPath systemDirCanonical = CanonicalRootOrRefuse(Path.Combine(runsRoot, "system"), $"runs root subtree '{Path.Combine(runsRoot, "system")}'");
        // The same subtrees AddRunsRootFiles never descends into: the launcher's own bookkeeping.
        for (int i = 0; i < gate.Outputs.Count; i++)
        {
            string full = ResolveOutputEntry(gate.Outputs[i], anchor);
            if (sources.TryGetValue(full, out HashSet<GateTrust.TrustSource>? set)
                && set.Any(s => s is not GateTrust.TrustSource.ArgvEntry))
            {
                throw new LaunchException($"gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] '{full}' is a trusted gate input");
            }

            GateTrust.CanonicalPath fullCanonical = GateTrust.Canonical(full);
            RefuseUnresolvedCanonical(fullCanonical, $"gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] '{full}'");
            if (GateTrust.IsAtOrUnder(fullCanonical.Path, runsRootCanonical.Path)
                && !GateTrust.IsAtOrUnder(fullCanonical.Path, runsDirCanonical.Path)
                && !GateTrust.IsAtOrUnder(fullCanonical.Path, inboxDirCanonical.Path)
                && !GateTrust.IsAtOrUnder(fullCanonical.Path, systemDirCanonical.Path))
            {
                throw new LaunchException($"gate.outputs[{i.ToString(CultureInfo.InvariantCulture)}] '{full}' is at or under the runs root outside runs/");
            }
        }
    }

    private string Status()
    {
        Meter meter = NonNull(_meter);
        string status;
        if (_capKilled)
        {
            status = "budget-exceeded";
        }
        else if (!_outcome.HasResult)
        {
            if (_timedOut)
            {
                status = "timed-out";
            }
            else if (_exitCode is not 0)
            {
                status = "crashed";
            }
            else
            {
                status = "no-result";
            }
        }
        else if (_outcome.IsError)
        {
            status = "error";
        }
        else
        {
            status = "success";
        }
        if (meter.WrappedUp && status is "success" or "error")
        {
            status = "wrapped-up";
        }
        return status;
    }

    private async Task RunGateAsync()
    {
        // The gate only runs after a successful worker (a non-success status ends the chain with the
        // today's exit code; --no-gate or no profile key skips it).
        if (_gate is null || _status is not "success")
        {
            return;
        }

        (string? violation, Gate.GateResult? gateRan) = await RunTrustedGateAsync().ConfigureAwait(false);
        if (violation is not null)
        {
            await FinishTrustViolationAsync(violation, gateRan).ConfigureAwait(false);
            return;
        }

        Gate.GateResult result = gateRan!;
        GateResult = result;

        List<int> countsIncludingThis = GateContext is null
            ? [result.Count ?? 0]
            : [.. GateContext.Counts, result.Count ?? 0];
        decimal chainCostIncludingThis = ChainCostUsd + NonNull(_meter).Spent;
        decimal maxTotalUsd = _gate.MaxTotalUsd ?? 0m;
        int maxRounds = _gate.MaxRounds;

        string decision = GateChain.Decide(result, countsIncludingThis, Round, maxRounds, chainCostIncludingThis, maxTotalUsd);
        string? stuckReason = decision is GateChain.Stuck
            ? GateChain.StuckReason(countsIncludingThis, Round, maxRounds, chainCostIncludingThis, maxTotalUsd)
            : null;

        Counts = countsIncludingThis;
        ChainCostUsd = chainCostIncludingThis;
        LastGate = new GateOutcome(decision, result, RunDir, countsIncludingThis, chainCostIncludingThis, stuckReason);
    }

    /// <summary>
    /// Runs the gate between the two trust checks. Both compare against round 1's before-worker
    /// snapshot, never against a fresh baseline: a later round must not accept whatever is on disk
    /// when it starts, or a tampering process that survives into it would hide. Returns the trust
    /// violation message (null when clean) and the gate result when the gate ran. After a clean
    /// after-gate check, the declared outputs that are in the trusted set are re-hashed into
    /// <see cref="PostGateOutputs"/> so the next round's baseline accepts the gate's own round-1
    /// write and nothing else.
    /// </summary>
    private async Task<(string? Violation, Gate.GateResult? GateRan)> RunTrustedGateAsync()
    {
        // Pre-gate: a worker may have swapped a trusted input between round 1's before-worker
        // snapshot and now. Nothing is excluded: a declared gate output is compared like any
        // other trusted path here, because the worker must not change it — only the gate may,
        // after this check. The check refuses the gate with a precise list of changed paths so
        // the chain ends in `error` instead of running the gate against tampered inputs.
        string? violation;
        try
        {
            violation = TrustSnapshot is not null
                ? await TrustViolationMessageAsync(_noExclusions, reportPath: null, "the worker changed files the gate trusts: ").ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A trust check that cannot read the tree fails closed as a trust violation (outcome
            // `error`, exit 5) rather than crashing the launch with exit 2.
            return ($"the trusted inputs could not be checked: {ex.Message}", null);
        }

        violation ??= GatePathViolation("the worker changed a gate PATH directory: ");

        if (violation is not null)
        {
            return (violation, null);
        }

        Gate.GateResult result;
        try
        {
            result = await Gate.RunAsync(NonNull(_gate), RunDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failure inside the gate runner itself (opening gate.log, writing gate.json) is not
            // a trust-check failure: name the actual cause instead of blaming the trusted inputs.
            return ($"the gate could not be run: {ex.Message}", null);
        }

        return (await PostGateCheckAsync(result).ConfigureAwait(false), result);
    }

    /// <summary>
    /// The after-gate trust check plus the post-gate bookkeeping that follows it when the check is
    /// clean. Post-gate: a process the worker detached can survive the worker's exit and rewrite
    /// a trusted file between the pre-gate check and the gate's end. The pre-gate check
    /// is not enough; we hash a third time after the gate exits and refuse the chain on
    /// any difference from round 1's before-worker snapshot (or a bad state). The
    /// declared gate outputs leave this comparison on both sides — the gate is expected
    /// to write exactly those paths; everything else (the gate's argv entries included)
    /// must match. The gate's report path is checked first, inside the same call: it may
    /// be under this run's directory or a declared output, it is a trust violation when
    /// it is a trusted input from any source, and it is invisible to the trust set
    /// otherwise.
    ///
    /// Remaining gap (deliberate, no P/Invoke): a detached process that swaps a trusted
    /// file between two hashes and restores it before the next hash escapes the check.
    /// Killing the worker's process group would shrink that window; the launcher does
    /// not take that step.
    /// After a clean check, the declared outputs that are in the trusted set are re-hashed into
    /// <see cref="PostGateOutputs"/> so the next round's baseline accepts the gate's own write and
    /// nothing else.
    /// </summary>
    private async Task<string?> PostGateCheckAsync(Gate.GateResult result)
    {
        try
        {
            string? violation = TrustSnapshot is not null
                ? await TrustViolationMessageAsync(await DeclaredOutputExclusionsAsync().ConfigureAwait(false), result.ReportPath,
                    "trusted files changed while the gate ran: ").ConfigureAwait(false)
                : null;

            violation ??= GatePathViolation("a gate PATH directory changed while the gate ran: ");

            if (violation is null)
            {
                PostGateOutputs = await HashPostGateOutputsAsync().ConfigureAwait(false);
            }

            return violation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A trust check that cannot read the tree fails closed as a trust violation (outcome
            // `error`, exit 5) rather than crashing the launch with exit 2.
            return $"the trusted inputs could not be checked: {ex.Message}";
        }
    }

    /// <summary>
    /// The listing trust check over the gate PATH's directories against round 1's frozen listing:
    /// one enumeration per directory, no hashing (<see cref="GateTrust.PathNameViolations"/>). Null
    /// when there is no frozen listing or no frozen PATH (a hand-built context without the trust
    /// fields) or nothing changed. A violation here ends the chain with <c>error</c> (exit 5) like
    /// the hash-based checks; <paramref name="prefix"/> says which check found it, and the remedy
    /// hint is appended so the cause is actionable.
    /// </summary>
    private string? GatePathViolation(string prefix)
    {
        string? gatePath = NonNull(_gate).GatePath;
        if (GatePathNames is null || gatePath is null)
        {
            return null;
        }

        IReadOnlyList<string> diffs = GateTrust.PathNameViolations(GatePathNames, GateTrust.SplitGatePath(gatePath));
        return diffs.Count is 0 ? null : prefix + string.Join(", ", diffs) + PathPathHint;
    }

    /// <summary>
    /// Appended to every gate PATH violation: these directories are usually written by installs and
    /// updates, so the message names the remedy (install before launching, or call by absolute path)
    /// instead of leaving the operator to guess why an unrelated install ended the chain.
    /// </summary>
    private const string PathPathHint = " (PATH directories must not change during a gated run; install tools before launching or call them by absolute path)";

    /// <summary>
    /// After a clean after-gate check: hashes the declared gate outputs that are keys of the trust
    /// snapshot (the trusted set) and returns them as a snapshot fragment. The gate was allowed to
    /// rewrite exactly those paths, so the next round's baseline must carry their post-gate values —
    /// the chain loop stores the fragment on the next <see cref="GateContext"/> and every later
    /// round merges it over round 1's snapshot (<see cref="MergePostGateOutputs"/>), keeping round 1's
    /// values everywhere else. Null when there is no snapshot or no declared output in it.
    /// </summary>
    private async Task<GateTrust.Snapshot?> HashPostGateOutputsAsync()
    {
        if (TrustSnapshot is null)
        {
            return null;
        }

        GateTrust.Snapshot snapshot = TrustSnapshot;
        List<string> outputs =
            [.. (await DeclaredOutputPathsAsync().ConfigureAwait(false)).Where(p => snapshot.Hashes.ContainsKey(p))];
        return outputs.Count is 0 ? null : await GateTrust.HashAsync(outputs).ConfigureAwait(false);
    }

    /// <summary>
    /// Round 1's snapshot with the post-gate declared-output values (<paramref name="postGate"/>)
    /// replacing their keys — nothing else. The merge is anchored on round 1's snapshot every round,
    /// so a value only the gate writes can never leak into the baseline for any other path.
    /// </summary>
    private static GateTrust.Snapshot MergePostGateOutputs(GateTrust.Snapshot baseline, GateTrust.Snapshot postGate)
    {
        Dictionary<string, string> merged = new(baseline.Hashes, GateTrust.PathComparer);
        foreach (KeyValuePair<string, string> entry in postGate.Hashes)
        {
            merged[entry.Key] = entry.Value;
        }

        return new GateTrust.Snapshot(merged);
    }

    /// <summary>
    /// Runs one trust check against round 1's before-worker snapshot. When the gate has already run,
    /// <paramref name="reportPath"/> is the report path it named: it is checked first, because a
    /// trusted input the gate just announced it writes over deserves a precise error instead of a
    /// generic comparison failure (<see cref="GateTrust.ReportPathViolation"/>). Then compare the
    /// path sets first (a file that appeared or vanished is reported before anything is hashed),
    /// then hash the stable list and stop at the first entry that differs or sits in a bad state, so
    /// a planted FIFO cannot cost a timeout per entry. Returns null when the tree is clean;
    /// otherwise the error message for the chain's trust violation.
    /// </summary>
    private async Task<string?> TrustViolationMessageAsync(IReadOnlySet<string> exclusions, string? reportPath, string changedPrefix)
    {
        GateTrust.Snapshot before = NonNull(TrustSnapshot);
        (List<string> paths, IReadOnlyDictionary<string, HashSet<GateTrust.TrustSource>> sources) = await CollectCheckPathsAsync(exclusions).ConfigureAwait(false);

        if (GateTrust.ReportPathViolation(reportPath, sources, await DeclaredOutputExclusionsAsync().ConfigureAwait(false), RunDir) is { } reportViolation)
        {
            return reportViolation;
        }

        List<string> setDiff = GateTrust.SetDifferences(before, paths, exclusions);
        if (setDiff.Count > 0)
        {
            return $"{changedPrefix}{string.Join(", ", setDiff)}";
        }

        GateTrust.TrustDiff? diff = await GateTrust.FirstDiffAsync(before, paths).ConfigureAwait(false);
        if (diff is null)
        {
            return null;
        }

        // A bad state that round 1's snapshot already had is a configuration problem, not something
        // the worker or the gate did: word the error accordingly instead of accusing a change.
        return diff.IsBad && before.Hashes.TryGetValue(diff.Path, out string? expected) && expected == diff.Value
            ? $"trusted path '{diff.Path}' is not a regular file"
            : $"{changedPrefix}{diff.Path}";
    }

    private async Task FinishTrustViolationAsync(string message, Gate.GateResult? gateRan = null)
    {
        GateSpec gate = NonNull(_gate);
        // When the gate already ran, keep its real exit code, count and duration in gate.json and
        // the summary; replace only the outcome and the error.
        Gate.GateResult result = await Gate.WriteFailureAsync(RunDir, gate, message, gateRan).ConfigureAwait(false);
        GateResult = result;

        List<int> countsIncludingThis = GateContext is null
            ? [result.Count ?? 0]
            : [.. GateContext.Counts, result.Count ?? 0];
        decimal chainCostIncludingThis = ChainCostUsd + NonNull(_meter).Spent;

        // The decision is forced to "error"; no StuckReason (StuckReason is only set when decision == Stuck).
        Counts = countsIncludingThis;
        ChainCostUsd = chainCostIncludingThis;
        LastGate = new GateOutcome(GateChain.Error, result, RunDir, countsIncludingThis, chainCostIncludingThis, StuckReason: null);
    }

    private async Task ComputeSummaryStatsAsync()
    {
        _report = _outcome.Report;
        Meter meter = NonNull(_meter);
        _calls = meter.Calls();
        _tok = new JsonObject
        {
            ["input"] = _calls.Sum(c => c.Input),
            ["cache_write"] = _calls.Sum(c => c.CacheWrite5m + c.CacheWrite1h),
            ["cache_read"] = _calls.Sum(c => c.CacheRead),
            ["output"] = _calls.Sum(c => c.Output + c.Reasoning),
            ["thinking"] = _outcome.Thinking,
        };
        List<long> contexts = [.. _calls.Select(c => c.Context)];
        _first = contexts.Count > 0 ? contexts[0] : 0;
        _peak = contexts.Count > 0 ? contexts.Max() : 0;
        _firstCall = _calls.Count > 0 ? _calls[0] : null;
        double? firstCallCacheReadShare = null;
        if (_firstCall?.Context > 0)
        {
            firstCallCacheReadShare = Math.Round((double)_firstCall.CacheRead / _firstCall.Context, 3, MidpointRounding.ToEven);
        }
        _firstCallCacheReadShare = firstCallCacheReadShare;

        _hookChecks = File.Exists(Path.Combine(RunDir, "hook.log")) ? await Launcher.CountLinesAsync(Path.Combine(RunDir, "hook.log")).ConfigureAwait(false) : 0;
        _next = _status is "wrapped-up" or "success" ? null : Launcher.NextInChain(_chain, _provider, _model);
    }

    private void BuildSummary()
    {
        Meter meter = NonNull(_meter);
        _summary = new JsonObject
        {
            // Bumped when a field changes meaning or is removed; added fields keep the version. Rows without it are 0.
            ["schema_version"] = Launcher.RunSchemaVersion,
            ["timestamp"] = _started.ToString("o"),
            ["name"] = _name,
            ["run_dir"] = RunDir,
            ["profile"] = _profileName,
            ["runtime"] = _runtimeName,
            ["provider"] = _provider,
            ["model"] = _model,
            ["model_reason"] = _pickReason.Length > 0 ? _pickReason : null,
            ["effort"] = _effort,
            ["mode"] = _mode,
            ["billing"] = _billing,
            ["cache_ttl"] = _cacheTtl,
            ["hooks"] = (_runtimeName, _mode, _keepHooks) switch
            {
                ("opencode", _, _) => "user plugins off (clean config)",
                (_, "bare", _) => "off (bare)",
                (_, _, true) => "on",
                _ => "off (managed hooks still run)",
            },
            ["status"] = _status,
            ["subtype"] = _outcome.Subtype,
            ["terminal_reason"] = _outcome.TerminalReason,
            ["exit_code"] = _exitCode,
            ["num_turns"] = _outcome.Turns,
            ["api_calls"] = _calls.Count,
            ["duration_ms"] = (long)_elapsed.TotalMilliseconds,
            ["total_cost_usd"] = decimal.Round(meter.Spent, 6, MidpointRounding.ToEven),
            ["reported_cost_usd"] = _outcome.ReportedCost,
            ["budget_usd"] = _budget,
            ["wrap_up_usd"] = _wrapUp ? _budget * _wrapUpAt : null,
            ["wrapped_up"] = meter.WrappedUp,
            ["hook_checks"] = _hookChecks,
            ["continued_from"] = _o.ContinueFrom is not null && GateContext is null ? Path.GetFullPath(_o.ContinueFrom) : null,
            ["escalate_to"] = _next,
            ["gate"] = BuildGateSection(),
            ["model_traits"] = _traits?.Key,
            ["tokens"] = _tok,
            ["context_first_call"] = _first,
            ["first_call_cache_read"] = JsonValue.Create(_firstCall?.CacheRead),
            ["first_call_cache_read_share"] = JsonValue.Create(_firstCallCacheReadShare),
            ["context_peak"] = _peak,
            ["permission_denials"] = _outcome.Denials,
            ["write_scope"] = _writeScope is null ? null : new JsonArray([.. _writeScope.Select(p => (JsonNode)p)]),
            ["changed_files"] = _changed is null ? null : new JsonArray([.. _changed.Select(p => (JsonNode)p)]),
            ["out_of_scope"] = _outOfScope is null ? null : new JsonArray([.. _outOfScope.Select(p => (JsonNode)p)]),
            ["session_id"] = _outcome.SessionId,
            ["quota_before"] = _quotaBefore?.ToJson(),
            ["quota_after"] = _quotaAfter?.ToJson(),
            ["quota_used_pct"] = Launcher.QuotaDelta(_quotaBefore, _quotaAfter),
            ["notes"] = new JsonArray([.. _notes.Select(n => (JsonNode)n)]),
        };
    }

    private JsonNode? BuildGateSection()
    {
        GateOutcome? outcome = LastGate;
        if (outcome is null)
        {
            return null;
        }

        Gate.GateResult result = outcome.Result;
        return new JsonObject
        {
            ["chain_id"] = ChainId,
            ["round"] = Round,
            ["outcome"] = result.Outcome,
            ["exit_code"] = result.ExitCode,
            ["count"] = result.Count,
            ["decision"] = outcome.Decision,
            ["error"] = result.Error,
            ["report_path"] = result.ReportPath,
            ["duration_ms"] = (long)result.Duration.TotalMilliseconds,
        };
    }

    private async Task WriteSummaryAsync()
    {
        await Launcher.WritePlainAsync(Path.Combine(RunDir, "summary.json"), _summary.ToJsonString(Json.Indented)).ConfigureAwait(false);
        await Launcher.WritePlainAsync(Path.Combine(RunDir, "report.md"), _report).ConfigureAwait(false);
        await File.AppendAllTextAsync(Path.Combine(_runsRoot, "runs.jsonl"), _summary.ToJsonString() + Environment.NewLine, Json.Utf8, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PrintAsync()
    {
        await PrintHeaderAsync().ConfigureAwait(false);
        await PrintNoticesAsync().ConfigureAwait(false);
        await PrintReportAsync().ConfigureAwait(false);
    }

    private async Task PrintHeaderAsync()
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        string N(JsonNode? n) => Json.Num(n).ToString("N0", ic);
        TextWriter w = Console.Out;
        Meter meter = NonNull(_meter);
        await w.WriteLineAsync("LEAN-WORKER RESULT").ConfigureAwait(false);
        await w.WriteLineAsync($"run:      {RunDir}").ConfigureAwait(false);
        await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"status:   {_status}  (subtype={_outcome.Subtype}, reason={_outcome.TerminalReason}, exit={_exitCode})")).ConfigureAwait(false);
        string knob = _runtimeName is "opencode" ? $"variant {_variant ?? "default"}" : $"effort {_effort}";
        await w.WriteLineAsync($"model:    {_provider}/{_model}{(_pickReason.Length > 0 ? $" ({_pickReason})" : string.Empty)}, {knob}, profile {_profileName ?? "(none)"}").ConfigureAwait(false);
        await w.WriteLineAsync(_runtimeName is "opencode"
            ? $"runtime:  opencode, {_summary["hooks"]}"
            : $"runtime:  claude, mode {_mode}, hooks {_summary["hooks"]}, cache {_cacheTtl}").ConfigureAwait(false);
        await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"work:     {_outcome.Turns} turns, {_calls.Count} API calls, {(int)_elapsed.TotalMinutes}m{_elapsed.Seconds:00}s")).ConfigureAwait(false);
        string costNote = _billing is "subscription" ? "list-price equivalent; subscription, not billed" : "list price; metered";
        string reported = _outcome.ReportedCost is { } rc && Math.Abs(rc - meter.Spent) > Math.Max(0.0005m, meter.Spent * 0.05m) ? string.Create(CultureInfo.InvariantCulture, $"; runtime reported ${rc:0.0000}") : string.Empty;
        await w.WriteLineAsync($"cost:     ${meter.Spent.ToString("0.0000", ic)} ({costNote}{reported})").ConfigureAwait(false);
        string wrapUpText = _wrapUp ? string.Create(CultureInfo.InvariantCulture, $"wrap-up at ${(_budget * _wrapUpAt).ToString("0.####", ic)}{(meter.WrappedUp ? " (triggered)" : string.Empty)}, {_hookChecks} hook checks") : "wrap-up off";
        await w.WriteLineAsync($"budget:   ${_budget.ToString("0.####", ic)}, {wrapUpText}{(_capKilled ? ", STOPPED at the budget" : string.Empty)}").ConfigureAwait(false);
        string? gateLine = FormatGateLine(ic);
        if (gateLine is not null)
        {
            await w.WriteLineAsync(gateLine).ConfigureAwait(false);
        }
        await w.WriteLineAsync($"tokens:   input {N(_tok["input"])} | cache write {N(_tok["cache_write"])} | cache read {N(_tok["cache_read"])} | output {N(_tok["output"])} (thinking {N(_tok["thinking"])})").ConfigureAwait(false);
        string firstShareText = _firstCallCacheReadShare is { } s ? $" (cache read {Math.Round(s * 100, MidpointRounding.ToEven).ToString(ic)}%)" : string.Empty;
        await w.WriteLineAsync($"context:  first call {_first.ToString("N0", ic)}{firstShareText} | peak {_peak.ToString("N0", ic)}").ConfigureAwait(false);
        if (_quotaAfter is not null)
        {
            await w.WriteLineAsync($"quota:    {_quotaAfter.Line(_quotaBefore)}").ConfigureAwait(false);
        }

        if (_changed is null)
        {
            return;
        }

        await w.WriteLineAsync($"files:    {_changed.Count} changed in the working tree{(_outOfScope is null ? " (no write scope given)" : $", {_outOfScope.Count} outside the write scope")}").ConfigureAwait(false);
    }

    private string? FormatGateLine(CultureInfo ic)
    {
        GateOutcome? outcome = LastGate;
        if (outcome is null)
        {
            return null;
        }

        Gate.GateResult result = outcome.Result;
        int n = result.Count ?? 0;
        return outcome.Decision switch
        {
            GateChain.Clean => string.Create(ic, $"gate:     clean after {Round.ToString(ic)} round(s), findings {string.Join('→', outcome.Counts)}"),
            GateChain.Continue => string.Create(ic, $"gate:     round {Round.ToString(ic)}, {n.ToString(ic)} finding(s); starting round {(Round + 1).ToString(ic)}"),
            GateChain.Stuck => string.Create(ic, $"gate:     STUCK after {Round.ToString(ic)} round(s), findings {string.Join('→', outcome.Counts)} ({outcome.StuckReason ?? "stuck"})"),
            GateChain.Error => $"gate:     ERROR: {result.Error ?? "(no error message)"}",
            _ => null,
        };
    }

    private async Task PrintNoticesAsync()
    {
        foreach (string n in _notes)
        {
            await Console.Out.WriteLineAsync($"note:     {n}").ConfigureAwait(false);
        }

        Meter meter = NonNull(_meter);
        if (meter.WrappedUp)
        {
            await Console.Out.WriteLineAsync($"continue: --continue-from \"{Path.GetFullPath(RunDir)}\" (fresh worker, original task + this handoff; ask the operator first)").ConfigureAwait(false);
        }
        else if (_next is not null)
        {
            await Console.Out.WriteLineAsync($"escalate: --continue-from \"{Path.GetFullPath(RunDir)}\" --model {_next} (next in the profile's chain; ask the operator first)").ConfigureAwait(false);
        }

        if (!meter.WrappedUp && _outcome.Denials > 0)
        {
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"WARNING:  {_outcome.Denials} permission denial(s); see stream.jsonl. Add the needed commands to the profile's allowedTools.")).ConfigureAwait(false);
        }

        if (_outOfScope is not { Count: > 0 })
        {
            return;
        }

        string extras = _outOfScope.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $" (+{_outOfScope.Count - 5} more, see summary.json)") : string.Empty;
        await Console.Out.WriteLineAsync($"WARNING:  {_outOfScope.Count} file(s) changed outside the write scope: {string.Join(", ", _outOfScope.Take(5))}{extras}. Check them before accepting the run.").ConfigureAwait(false);
    }

    private async Task PrintReportAsync()
    {
        await Console.Out.WriteLineAsync("--- worker report ---").ConfigureAwait(false);
        if (_report.Length > _o.ReportMaxChars)
        {
            await Console.Out.WriteLineAsync(_report[.._o.ReportMaxChars]).ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"[truncated; full report: {Path.Combine(RunDir, "report.md")}]").ConfigureAwait(false);
        }
        else if (_report.Length > 0)
        {
            await Console.Out.WriteLineAsync(_report).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteLineAsync("(no report text)").ConfigureAwait(false);
            FileInfo fi = new(_stderrPath);
            if (fi.Exists && fi.Length > 0)
            {
                await Console.Out.WriteLineAsync("--- stderr (first 40 lines) ---").ConfigureAwait(false);
                int stderrLines = 0;
                await foreach (string l in File.ReadLinesAsync(_stderrPath, Json.Utf8, CancellationToken.None).ConfigureAwait(false))
                {
                    await Console.Out.WriteLineAsync(l).ConfigureAwait(false);
                    if (++stderrLines >= 40) { break; }
                }
            }
        }
    }

    private static T NonNull<T>(T? value) where T : class => value ?? throw new InvalidOperationException("LaunchRun phase order violated");

    private void RemoveScratch(string? path)
    {
        if (path is null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            _notes.Add($"config home not removed: {path} ({ex.Message})");
        }
        catch (UnauthorizedAccessException ex)
        {
            _notes.Add($"config home not removed: {path} ({ex.Message})");
        }
    }
}