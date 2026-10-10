using System.Globalization;

namespace LeanWorker;

internal sealed class Options
{
    public const string Usage = """
        LeanWorker: run one task in a minimal-context worker (Claude Code or opencode).
        Exit codes: 0 = worker finished without error (or a gate chain ended clean); 1 = worker reported an error;
        2 = launcher failed; 3 = worker wrapped up near its budget; 4 = a gate chain ended stuck (no decrease in
        two consecutive rounds, max rounds reached, or cost cap exceeded); 5 = a gate chain ended in an error.

          --task <file>              task prompt (required unless --continue-from)
          --continue-from <run-dir>  fresh worker on a stopped run: its original task + its report as the handoff
                                     (replaces --task; profile defaults to that run's; model/runtime can be overridden)
          --profile <name>           profile from <runs-root>/profiles.json (default: its defaultProfile)
          --system <file>            per-task notes, appended after <runs-root>/project.md
          --name <name>              run name (default: the task file's folder name)
          --runtime <claude|opencode> worker runtime (default claude; profile key "runtime")
          --model <id>               override the profile's model (or model chain); provider/model, e.g.
                                     zai-coding-plan/glm-5.3; a bare id is an Anthropic model
          --effort <level>           low | medium | high | xhigh | max (claude runtime)
          --variant <name>           opencode model variant (profile key "variant")
          --tools <A,B,C>            built-in tools available to the worker (Claude Code names)
          --allow <pattern>          pre-approved tool pattern, repeatable, e.g. --allow "Bash(git diff:*)"
          --write-scope <glob>       a path the task may write, repeatable, relative to the git root (e.g. "src/Foo/**");
                                     files the run changed outside it are reported (profile key "writeScope")
          --mcp-config <file>        MCP servers for this run only (claude runtime; always --strict-mcp-config)
          --max-budget-usd <n>       spend cap for the run, metered by the launcher with prices.json
          --wrap-up-at <share>       past this share of the budget tools are blocked and the worker writes a
                                     handoff (default 0.8; 0 = off; profile key "wrapUpAt")
          --prices <file>            price file merged over the shipped and user ones (default <runs-root>/prices.json)
          --permission-mode <mode>   acceptEdits | dontAsk | plan | manual | auto | bypassPermissions
          --runs-root <dir>          default .lean-worker
          --claude-settings <file>   passed to claude as --settings (e.g. an apiKeyHelper for --bare)
          --timeout-minutes <n>      kill the worker after n minutes (default 60)
          --report-max-chars <n>     truncate the printed report (default 6000)
          --no-project-notes         do not give the worker <runs-root>/project.md
          --replace-system-prompt    replace Claude Code's system prompt instead of appending
          --mode <auto|bare|lean>    claude runtime. bare = claude --bare (API key; skips hooks, so no wrap-up);
                                     lean = the same minimal profile from flags (subscription login, key, or another
                                     provider). auto (default) = lean, or bare when a key is set and wrap-up is off
          --no-bare                  alias for --mode lean
          --keep-claude-md           do not exclude CLAUDE.md / AGENTS.md / .claude/rules (opencode: keep project config)
          --keep-memory              lean mode: keep auto memory (off by default)
          --cache-ttl <5m|1h|default> prompt-cache lifetime for the worker (default 5m; profile key "cacheTtl")
          --keep-hooks               lean mode: load the user's settings, hooks and plugins (off by default; profile
                                     key "keepHooks": true). Managed (organisation) hooks always run.
          --no-user-env              lean mode: do not carry the user settings' env block into the worker
          --no-gate                  do not run the profile's gate (profile key "gate")
          --gate-max-rounds <n>      overrides gate.maxRounds for this run (profile key "gate.maxRounds")

        Other commands:
          hook --run-dir <dir>       the worker's pre-tool hook (installed by the launcher)
          quota [--provider <name>] [--json] [--max-age <seconds>]
                                     subscription quota of every provider with a quota adapter (e.g. zai-coding-plan)
          cost --claude <session-id|file.jsonl> [--provider <name>] | --opencode <session-id>
                                     price a manual session with prices.json
          stats [--since <yyyy-mm-dd>] [--json]
                                     runs per profile and model: success rate (worker status or gate clean), wrap-ups, escalations,
                                     cost per success, quota used; --json for other tools (schema_version as in runs.jsonl)
          prices                     the merged price book: sources and each entry's date
        """;

    public string? TaskFile;
    public string? Profile;
    public string? SystemFile;
    public string? Name;
    public string? Model;
    public string? Effort;
    public string? Variant;
    public string? McpConfig;
    public string? PermissionMode;
    public string? RunsRoot;
    public string? ClaudeSettings;
    public string? CacheTtl;
    public string? ContinueFrom;
    public string? Runtime;
    public string? PricesFile;
    public List<string>? Tools;
    public List<string> AllowedTools = [];
    public List<string> WriteScope = [];
    public decimal? MaxBudgetUsd;
    public decimal? WrapUpAt;
    public int TimeoutMinutes = 60;
    public int ReportMaxChars = 6000;
    public bool NoProjectNotes;
    public bool ReplaceSystemPrompt;
    public bool KeepClaudeMd;
    public bool KeepMemory;
    public bool KeepHooks;
    public bool NoUserEnv;
    public bool NoGate;
    public int? GateMaxRounds;
    public bool Help;
    public string Mode = "auto";

    public static Options Parse(string[] args)
    {
        Options o = new();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new LaunchException($"{args[i]} needs a value");
            if (!o.TrySetText(args[i], () => Next()) && !o.TrySetParsed(args[i], () => Next()) && !o.TrySetFlag(args[i]))
            {
                throw new LaunchException($"unknown option '{args[i]}' (see --help)");
            }
        }

        return o;
    }

    private bool TrySetText(string arg, Func<string> next)
    {
        switch (arg)
        {
            case "--task": { TaskFile = next(); return true; }
            case "--continue-from": { ContinueFrom = next(); return true; }
            case "--profile": { Profile = next(); return true; }
            case "--system": { SystemFile = next(); return true; }
            case "--name": { Name = next(); return true; }
            case "--runtime": { Runtime = next(); return true; }
            case "--model": { Model = next(); return true; }
            case "--effort": { Effort = next(); return true; }
            case "--variant": { Variant = next(); return true; }
            case "--mcp-config": { McpConfig = next(); return true; }
            case "--prices": { PricesFile = next(); return true; }
            case "--permission-mode": { PermissionMode = next(); return true; }
            case "--runs-root": { RunsRoot = next(); return true; }
            case "--claude-settings": { ClaudeSettings = next(); return true; }
            case "--mode": { Mode = next(); return true; }
            case "--cache-ttl": { CacheTtl = next(); return true; }
        }

        return false;
    }

    private bool TrySetParsed(string arg, Func<string> next)
    {
        switch (arg)
        {
            case "--tools": { Tools = [.. next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]; return true; }
            case "--allow": { AllowedTools.Add(next()); return true; }
            case "--write-scope": { WriteScope.Add(next()); return true; }
            case "--max-budget-usd": { MaxBudgetUsd = decimal.Parse(next(), CultureInfo.InvariantCulture); return true; }
            case "--wrap-up-at": { WrapUpAt = decimal.Parse(next(), CultureInfo.InvariantCulture); return true; }
            case "--timeout-minutes": { TimeoutMinutes = int.Parse(next(), CultureInfo.InvariantCulture); return true; }
            case "--report-max-chars": { ReportMaxChars = int.Parse(next(), CultureInfo.InvariantCulture); return true; }
            case "--gate-max-rounds": { GateMaxRounds = int.Parse(next(), CultureInfo.InvariantCulture); return true; }
        }

        return false;
    }

    private bool TrySetFlag(string arg)
    {
        switch (arg)
        {
            case "--no-project-notes": { NoProjectNotes = true; return true; }
            case "--replace-system-prompt": { ReplaceSystemPrompt = true; return true; }
            case "--no-bare": { Mode = "lean"; return true; }
            case "--keep-claude-md": { KeepClaudeMd = true; return true; }
            case "--keep-memory": { KeepMemory = true; return true; }
            case "--keep-hooks": { KeepHooks = true; return true; }
            case "--no-user-env": { NoUserEnv = true; return true; }
            case "--no-hooks": { return true; } // hooks are off by default; accepted for compatibility
            case "--no-gate": { NoGate = true; return true; }
            case "-h" or "--help": { Help = true; return true; }
        }

        return false;
    }
}