# lean-worker

A skill for orchestrating sessions in Claude Code and opencode. It runs one coding task in a separate,
minimal-context worker: Claude Code
(`claude -p`) or opencode (`opencode run`), on Claude or on other providers' models (GLM, DeepSeek,
MiniMax). The orchestrating session gets back the worker's report together with its token
usage and cost. Near its budget the worker writes a handoff instead of stopping blind, and a fresh
worker can pick the task up from it.

> **Quick install through your agent:** in a Claude Code or opencode session in your project, say
> *"Install the lean-worker skill for this project, following
> https://github.com/sy11a/lean-worker/blob/master/INSTALL.md"*.
> [INSTALL.md](INSTALL.md) walks the agent through the whole setup and tells it when to stop and ask you.
> A fuller prompt to paste is in [AGENT-INSTALL-PROMPT.md](AGENT-INSTALL-PROMPT.md).

## Why

An in-session subagent inherits the project's CLAUDE.md, the skill listing, every MCP
server and every tool schema. That is often 40-60k tokens before the subagent does anything,
and every API call it makes re-reads all of it. A `--bare` worker loads only what you pass
it: the project notes, the task, and the tools the task needs.

Every run is recorded, so you can see what each delegated task actually cost:

```
LEAN-WORKER RESULT
run:      .lean-worker/runs/20260929-133150-add-greeting
status:   success  (subtype=success, reason=completed, exit=0)
model:    claude-haiku-4-5, effort low, profile code, mode lean
work:     3 turns, 3 API calls, 0m13s
cost:     $0.0105 (list price reported by Claude Code)
tokens:   input 25 | cache write 971 | cache read 43,573 | output 830 (thinking 501)
context:  first call 14,283 (cache read 93%) | peak 15,253
--- worker report ---
...
```

This is real output from a test run with `--no-bare`. A `--bare` run starts smaller.

## Requirements

- Claude Code, logged in with either:
  - an **API key** (`ANTHROPIC_API_KEY` or an `apiKeyHelper`). Workers then run in **bare** mode
    (`claude --bare`), or
  - a **subscription login** (Pro, Max, Team or **Enterprise**; `claude` + `/login`). Workers then run in
    **lean** mode. `--bare` cannot use a subscription login, so the launcher builds the same
    minimal profile from flags instead. See [Authentication and modes](#authentication-and-modes).
- .NET SDK 8 or newer. The launcher targets `net8.0` with `RollForward=LatestMajor`, so it
  also runs on newer runtimes. It has no NuGet dependencies.
- Linux or macOS. The installer is `install.sh` (bash; `jq` for the permission step).
- Optional: [opencode](https://opencode.ai), for workers with `"runtime": "opencode"`.

## Setup after download

### 1. Run the installer (no agent involved)

From the downloaded repository:

```bash
./install.sh --project ~/src/my-product --smoke-test
```

The installer (flags: `--scope project`, `--orchestrator claude|opencode|both`, `--skip-permission`,
`--add-rule`):

1. **Checks prerequisites:** a .NET SDK 8 or newer, `claude` with `--bare` when Claude Code
   orchestrates, `opencode` when opencode orchestrates, and whether `ANTHROPIC_API_KEY` is set (a missing
   key is not a failure).
2. **Installs the skill** for each orchestrator. The default is both when opencode is on PATH, else
   Claude Code:
   - Claude Code: `~/.claude/skills/lean-worker`, or
     `<project>/.claude/skills` with the project scope;
   - opencode: `~/.config/opencode/skills/lean-worker` (or `$XDG_CONFIG_HOME/opencode/...`), or
     `<project>/.opencode/skills` with the project scope. opencode finds it there even with its import of
     Claude Code skills turned off.

   The skill's description is loaded into every session of that scope (about 310 characters), so the
   project scope keeps it out of unrelated projects.
3. **Builds the launcher once**, so the first worker starts immediately.
4. **Prepares the project** when `--project` is given:
   - creates `.lean-worker/project.md` and `.lean-worker/profiles.json` from the templates.
     Files that already exist are never overwritten.
   - adds `.lean-worker/runs/`, `.lean-worker/inbox/` and `.lean-worker/runs.jsonl` to `.gitignore`.
   - adds the allow rule `Bash(dotnet run --project:*)` to `<project>/.claude/settings.json`,
     so the orchestrator can start workers without a permission prompt. The file is backed up
     first (`settings.json.bak-<stamp>`) and is re-serialised, so any formatting is not kept. Pass
     the skip flag to leave it alone and add the rule yourself.
   - for opencode, `"permission": {"bash": {"dotnet run --project*": "allow"}}` in `<project>/opencode.json`
     (created if missing, backed up otherwise). A plain `"bash": "ask"` becomes the object's `"*"`
     entry. A project with only `opencode.jsonc` gets a warning and edits it by hand.
   - with the add-rule flag, the delegation rule (`templates/orchestrator-rule.md`: this session
     orchestrates, workers implement, search and review) is appended once to the file each orchestrator
     reads: `CLAUDE.md` for Claude Code, `AGENTS.md` for opencode. A `CLAUDE.md` that is a symlink to
     `AGENTS.md` gets it once. Leave this off in a repository whose instruction files are generated or
     delivered by another tool.
5. **Runs one tiny worker** when `--smoke-test` is given. It uses Haiku, is read-only and has a
   budget of $0.10. The installer prints that worker's result block.

The installer is safe to re-run: it updates the skill and leaves your project files alone,
including your price files (`.lean-worker/prices.json`, `~/.config/lean-worker/prices.json`).

### 2. Fill in the project notes and profiles

Start a new Claude Code or opencode session so it picks up the skill. Then, in a session in
your project:

```
/lean-worker set up .lean-worker/project.md and profiles.json for this repository
```

The orchestrator reads the repository and asks you about anything it cannot see. It fills in:

- **`.lean-worker/project.md`**: the notes every worker receives. Stack, layout, build, test and
  lint commands, conventions, prohibitions, and where to look. Keep it short (target under 2k
  tokens), because it is paid for on every worker API call.
- **`.lean-worker/profiles.json`**: named profiles (`read`, `edit`, `code`, `research`, `review`), or, with other model plans, `templates/profiles-chains.json`: the same roles with model chains across plans (MiniMax, GLM, then Claude). What each model needs is in `skills/lean-worker/models.md`.
  Each one sets the model, effort, tools, pre-approved commands and budget. The `{build command}`
  and `{test command}` placeholders are replaced with your own commands, and you add profiles
  for your own task classes.

Then **read and edit both files by hand**. They are yours, and the skill never overwrites your edits.

### 3. Authentication and modes

The launcher picks the mode itself (`--mode auto`, the default). The mode is shown in every result block.

| Situation | Mode | What the worker runs |
|---|---|---|
| Subscription login (Pro, Max, Team, **Enterprise**), an API key with wrap-up on (the default), or another provider's model | **lean** | `claude -p --setting-sources ""` with CLAUDE.md, CLAUDE.local.md, AGENTS.md and `.claude/rules` excluded (`claudeMdExcludes`), no user/project/local settings (so none of your hooks or plugins; organisation-managed settings and hooks still apply), auto memory off, skills disabled, and no MCP. The launcher's own wrap-up hook is injected through `--settings` |
| `ANTHROPIC_API_KEY` (or `--claude-settings` with an `apiKeyHelper`) and `--wrap-up-at 0` | **bare** | `claude -p --bare`: loads no CLAUDE.md, hooks, plugins, skills, auto-memory or MCP. Bare mode skips every hook, so it has no wrap-up; the budget is still enforced |

`--setting-sources ""` replaced the earlier `disableAllHooks` approach. Measured on a subscription
login (Claude Code 2.1.284): the injected hook fires, the user's SessionStart hooks and plugins do
not load (31 plugins → 2 built-in), and a one-call probe went from $0.0197 to $0.0043. Your user
settings' `env` block (proxies, for example) is passed to the worker through its process environment,
never written to the run directory; `--no-user-env` turns that off. `--keep-hooks` loads your settings, hooks and plugins as before.

Nothing needs configuring for an Enterprise subscription. Stay logged in to `claude` as usual;
the worker uses the same login.

- **Don't set an API key "just for the workers" on an Enterprise seat** unless your organisation
  gives you one. `--bare` cannot use the subscription login; the lean mode exists for exactly that.
- **Cost on a subscription.** The `cost:` line is the list-price equivalent that Claude Code reports.
  On Enterprise you are not billed that amount: the tokens count against your plan's usage limits.
  It is still the right number for comparing tasks and profiles.
- **Size.** A trivial task, measured with a subscription login, had a first call of about 5k
  tokens in lean mode with its defaults (hooks off). A default in-session subagent is typically
  40-60k. Bare mode is expected to be about as small.
- **User and managed settings still apply in lean mode.** That includes enterprise policy,
  proxies and your user hooks. Bare mode skips hooks.

**What bare turns off, and how lean covers each item.** Measured with a subscription login,
one-call probe:

| Item | bare | lean default | Control in lean | Measured effect |
|---|---|---|---|---|
| CLAUDE.md, CLAUDE.local.md, AGENTS.md, `.claude/rules` | off | off | `--keep-claude-md` to keep them | about −4k (a 13.5 KB CLAUDE.md) |
| Auto memory (also its instructions in the system prompt) | off | off | `--keep-memory` to keep it | about −3k, even with no memory files |
| Hooks (user, project, plugins) | off | off (`--setting-sources ""`). **Managed hooks still run:** managed settings always load | `--keep-hooks`, or `"keepHooks": true` in a profile | about −1k on the test machine; depends on your hooks |
| Skills | off | off | always off (`--disable-slash-commands`) | small |
| MCP servers | off | off | always `--strict-mcp-config`; a profile can name servers | depends on the servers |
| Plugins, plugin sync, LSP | off | loaded | with skills, hooks and MCP off, and the worker's tool list fixed, a plugin has no remaining way into the worker's context | none measured |
| Background traffic (updates, telemetry) | off | on | not changed by the launcher. `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` in your environment turns it off; check your organisation's telemetry policy first | no context effect |
| Keychain | not read | read | this is how the subscription login works | n/a |

With the defaults, a lean worker's first call was about 5.2k tokens.

**Your integrations do not leak into the worker; your organisation's controls still apply.**
Lean mode shuts every channel through which personal customisations enter a session:

- CLAUDE.md files at every level, including `~/.claude/CLAUDE.md`, plus AGENTS.md and rules;
- auto memory;
- your user, project and plugin hooks;
- your output style (the worker's settings force `outputStyle: "default"`);
- skills, plugin commands and agents (not reachable with skills disabled and a fixed tool list);
- MCP servers.

The hook check was run on this machine: with hooks on, a plugin's SessionStart text reached the
worker; with the default, nothing did. Hooks deployed by your organisation through managed
settings keep running, because the docs state that a `disableAllHooks` set outside managed
settings cannot disable them.

Two things from your user settings do still reach the worker, by design:

- the `env` block (for example proxies);
- `permissions.allow` rules. A broad rule such as `Bash(*)` in your user settings also
  pre-approves those commands for workers. Keep broad rules out of user settings if that matters.

`CLAUDE_CODE_SAFE_MODE=1` also works with a subscription login (5.2k in the same probe). It turns
off CLAUDE.md, skills, plugins, hooks, MCP, agents, LSP and auto memory in one go. The launcher
does not use it, because it is documented as a troubleshooting mode, and the explicit settings
above can be switched one at a time and are recorded in each run's `settings.json`.

## How it fits your Claude Code flow

The orchestrator starts the launcher with its normal Bash tool, in the background. When the
worker finishes, Claude Code notifies the orchestrator, and the notification carries the
launcher's output. The tokens and cost therefore land in the orchestrating session by
themselves: nothing polls, and nobody has to open a separate log.

```
You:           /lean-worker Add retry with backoff to OrderClient.Send; done when the OrderClient tests pass
Orchestrator:  writes .lean-worker/inbox/order-retry/task.md
               Bash (background): dotnet run --project <skill-dir>/launcher -c Release -- --task ... --profile code
               ... the worker runs; the orchestrator waits ...
               <- completion notification with the launcher output:
                  LEAN-WORKER RESULT
                  status: success ...
                  cost:   $0.3812
                  tokens: input 210 | cache write 18,300 | cache read 351,000 | output 7,900
                  context: first call 5,100 (cache read 98%) | peak 31,000
                  --- worker report --- ...
               Bash: <your test command>   <- checks the done-criterion itself
Orchestrator -> you: "Done, tests green. Worker: 14 API calls, $0.38."
```

The orchestrator is told to read only the result block, and not the worker's transcript or the
full diff. That keeps its own context, which it re-reads on every turn, small. Every run is also
recorded as one line in `.lean-worker/runs.jsonl` (see below).

## Budget wrap-up and continuation

A worker that simply hits its spending cap stops mid-task: the files it edited stay on disk, and
nobody records what is done and what is left. The launcher prevents that:

1. It **meters spend live**: every API call the worker streams is priced with the price book
   (below), for Claude and for every other provider alike.
2. Past **`wrapUpAt`** of the budget (default `0.8`; profile key or `--wrap-up-at`; `0` = off) it writes
   `<run>/wrapup.json`. The worker's pre-tool hook (Claude Code: `LeanWorker hook`, injected through the
   run's `--settings`; opencode: a plugin in the run's clean config) then denies every tool call with an
   instruction to finish with a `HANDOFF` block: Done / Remaining / Files touched / State / Next step.
3. At the **budget** the launcher stops the worker. This is the hard cap for every model; Claude Code's
   own `--max-budget-usd` is passed as a second net only for Claude models, because it prices other
   providers' models wrongly.
4. The result shows `status: wrapped-up`, exit code `3`, and a `continue:` line. On the operator's go,
   `--continue-from <run-dir>` (without `--task`) starts a **fresh** worker with the original task plus the
   handoff, the previous run's profile and the full budget again. It does not resume the old session,
   which would re-read the whole context. `--runtime` and `--model` can be overridden, so a task can move
   to a cheaper model or to the other runtime. Continuing a continuation keeps the original task once.

```
status:   wrapped-up  (subtype=success, reason=completed, exit=0)
cost:     $0.0126 (list-price equivalent; subscription, not billed)
budget:   $0.03, wrap-up at $0.009 (triggered), 8 hook checks
continue: --continue-from ".lean-worker/runs/20260929-180127-glob12" (fresh worker, original task + this handoff; ask the operator first)
--- worker report ---
HANDOFF
- Done: Completed 5 Glob calls with patterns a*, b*, c*, d*, e*
- Remaining: 7 more Glob calls needed with patterns f*, g*, h*, i*, j*, k*, l* in that order
...
```

Limits:

- The remaining share must cover one final report turn. At a very large context it might not, and the
  budget then stops the run with no report (`status: budget-exceeded`). Lower `wrapUpAt` for that profile.
- The meter sees an API call when the runtime streams it. Claude Code streams a call before its tool runs;
  opencode reports a step when it finishes. Wrap-up can therefore fire one step late in opencode.
- `hook checks` in the `budget:` line counts how often the hook ran. `0` with wrap-up on means the hook
  never ran; the budget is still enforced.

## Models, prices and subscriptions

### Model ids and providers

A model is `provider/model`, e.g. `zai-coding-plan/glm-5.3` or `deepseek/deepseek-v4-flash`; a bare id
(`claude-sonnet-5-5`) is an Anthropic model. Provider ids follow opencode's. In the claude runtime a
non-Anthropic model runs through the provider's Anthropic-compatible endpoint (`anthropicBaseUrl` in the
price book) with the key from the provider's `keyEnv` variable, or from opencode's stored login
(`opencode auth login`). Keys go to the worker through its environment and are never written to disk.
The shipped endpoint verified end to end is z.ai's (`zai-coding-plan`); the others are from the providers'
documentation.

### The price book

`<skill-dir>/launcher/prices.json` ships list prices (USD per million tokens, from models.dev, checked by
hand, with the date). Two files are merged over it, later ones winning: `~/.config/lean-worker/prices.json`
(personal) and `.lean-worker/prices.json` (the project's, shared by every tool; `--prices <file>` replaces it
for one run). Add a model or change a price there; no rebuild is needed, and the installer never touches
these files. A project file travels with the repository, so it cannot say where keys go: a provider's
`anthropicBaseUrl`, `keyEnv`, `quota.url` and `quota.adapter` are read only from the shipped and the
personal file, and the result block names any the project file tried to set.

```json
{
  "asOf": "2026-10-15",
  "unknownModel": "dearest",
  "rates": { "USD": 1, "CNY": 0.14 },
  "providers": {
    "my-plan": { "billing": "subscription", "priceAs": "zai", "keyEnv": "MY_PLAN_KEY",
                 "anthropicBaseUrl": "https://example.com/anthropic" }
  },
  "models": {
    "zai/glm-6": { "input": 1.5, "output": 5, "cacheRead": 0.3, "cacheWrite": 0 },
    "my-plan/model-x": { "input": 3, "output": 12, "currency": "CNY",
                        "above": { "tokens": 200000, "input": 6, "output": 24 } }
  }
}
```

- A model id matches the longest key it starts with, so dated ids (`claude-haiku-4-5-20251001`) match.
- `cacheRead` and `cacheWrite` default to the input price, `cacheWrite1h` to twice the input price.
  Reasoning tokens are billed as output. `above` is a price tier for calls whose context exceeds `tokens`.
- `unknownModel`: `dearest` (price it as the most expensive model: a wrap-up that comes early, never late),
  `error` (refuse to launch), or a model key to price it as. The result block names the unpriced model.
- `LeanWorker prices` lists the merged book, its sources and each entry's date, and flags entries older
  than 90 days.

### Billing: metered or subscription

Each provider has a `billing`: `metered` (tokens cost money), `subscription` (a plan with usage limits,
such as z.ai's GLM Coding Plan), or `auto` (Anthropic: a subscription when the worker uses your
Claude Code login, metered with an API key). **Budgets are in list price either way.** On a subscription
the `cost:` line is the list-price equivalent, not a bill; it is the number to compare tasks, profiles and
models by, and the one the wrap-up uses. `priceAs` prices a plan's models as the provider's metered ones
(opencode and models.dev price coding plans at 0, which would make every budget infinite).

### Quota and model chains

Providers with a `quota` adapter report how much of the plan is used. Two are supported:

- z.ai's GLM Coding Plan (`zai-coding-plan`, adapter `zai`; `GET https://api.z.ai/api/monitor/usage/quota/limit`):
  a 5-hour and a weekly token window, and the monthly MCP tool calls.
- MiniMax's Coding / Token Plan (`minimax-coding-plan`, adapter `minimax`;
  `GET https://api.minimax.io/v1/api/openplatform/coding_plan/remains`): a 5-hour and a weekly window for the
  text models, plus windows for other model families such as video (`video-24h`, `video-weekly`). MiniMax
  reports what is left; the launcher shows what is used. A plan key works in one region only: set
  `"region": "cn"` in the provider's `quota` block (personal price file) for `api.minimaxi.com`.

```
$ dotnet run --project <skill-dir>/launcher -c Release -- quota
zai-coding-plan (max): 5h 9% (resets in 1h43m), weekly 89% (resets in 2d2h), mcp-monthly 0% (resets in 28d17h)
  mcp-monthly: 0 of 4000 calls
  headroom for workers: no (zai-coding-plan weekly 89% >= 85%)
```

A profile's `model` can be a **chain**, e.g. `["zai-coding-plan/glm-5.3", "deepseek/deepseek-v4-flash",
"claude-sonnet-5-5"]`. The launcher takes the first model with headroom: a subscription model while every
window is under its `maxPercent` (shipped: 5h 90%, weekly 85%), a metered model always. If the quota
cannot be read, the model counts as available and the result says so. A subscription run records the
quota before and after it (`quota:` line; `quota_used_pct` in `runs.jsonl`). When a run fails on a model
that has a next one in the chain, the result offers an `escalate:` line; like a continuation, it runs
only on the operator's go.

For a status line or a quick check, `quota --max-age 60` reuses a reading younger than 60 seconds
(cached in `~/.cache/lean-worker/`). A Claude Code status line can call it; in opencode, a custom command
can run it.

### Using cheaper models without changing the flow

The goal is a lower cost per finished task, with the same commands, the same result block and the same
single "go" from the operator:

- The orchestrator stays where it is. Only workers change model, and they are where most tokens go.
- Mechanical task classes with a checkable done-criterion (`read`, `edit`, `code`) get a chain that
  starts with the cheapest capable model: a subscription while it has quota, then cheap metered models,
  then Claude. `review` should use a different model family from the one that wrote the change.
- `LeanWorker stats` shows, per profile and model, the success rate, the cost per successful task and the
  quota used per run. Success is the worker's own status: the launcher does not see the orchestrator's
  check of the done-criterion. Move a model down the chain when its cost per success is worse than the next
  model's, even if it is cheaper per token.

### Pricing a manual session

`cost --claude <session-id|file.jsonl>` prices a Claude Code transcript (`~/.claude/projects/...`); add
`--provider <name>` for a session that ran on another provider's endpoint. `cost --opencode <session-id>`
prices an opencode session from its database. Both use the same price book and show each model's billing.

## The opencode runtime

`"runtime": "opencode"` (or `--runtime opencode`) starts `opencode run --format json` instead of `claude -p`.
The worker is kept as lean as in Claude Code:

- It runs on a **clean config home** (`XDG_CONFIG_HOME` set to a temporary directory
  `lean-worker-<run>-*` under the system temp path, removed when the worker exits): no global instructions,
  plugins, skills or MCP servers. Logins stay where they are. The home stays out of the repository because
  opencode installs its plugin dependencies there (`node_modules`, about 200 directories per run), which
  would pile up in the run directories and load every tool that watches the working tree. `OPENCODE_DISABLE_PROJECT_CONFIG` and
  `OPENCODE_DISABLE_CLAUDE_CODE` keep the project's AGENTS.md and CLAUDE.md out (`--keep-claude-md` keeps
  them). The worker's shell commands get your own `XDG_CONFIG_HOME` back, so git and other tools behave
  as usual.
- A custom provider defined in your opencode config is carried into the worker's config (in memory; the
  recorded `opencode-config.json` leaves it out, since it may hold a key). For providers whose id starts with
  `zai` (z.ai's coding plan and others) the launcher pins `x-session-affinity` and `X-Session-Id` to
  `lean-worker` on `provider.<id>.models.<model>.headers`, the only level opencode merges after its own:
  z.ai routes by those headers, so every fresh worker would otherwise land on a node without its prefix
  cached (first call 42-51% cache read, 99% with the headers fixed).
- Tools map from the profile's Claude Code names (`Read`, `Edit`, `Write`, `Glob`, `Grep`, `Bash`,
  `WebFetch`, `WebSearch`) to opencode permissions; every other tool is denied. `Bash` runs only the
  `allowedTools` patterns (`Bash(git diff:*)` → `git diff*`); any other command is rejected, since
  `opencode run` cannot ask.
- A fixed **deny floor** runs under whatever the profile allows and in every permission mode: Claude receives
  it as `--disallowedTools` after `--allowedTools` (deny beats allow); opencode receives the same three
  patterns as bash deny rules that come after the allow entries in the `bash` object (last matching rule
  wins), so even `bypassPermissions` keeps the floor, though under it the floor only blocks those literal
  git invocations and the mode itself runs anything. The three patterns are `Bash(git *--output*)`
  (which also covers git's harmless `--output-indicator-*` flags), `Bash(git *--ext-diff*)` and
  `Bash(git *--textconv*)`; those flags write a file or run a program whatever prefix allowed the
command. A profile can add more with `"deniedTools": [...]` (Bash patterns, applied only when
   the tools include Bash; opencode honours only `Bash(...)` entries) (floor first, no duplicates);
   nothing can remove the floor.
  Edit/Write under `.git/**` and `**/.git/**` (and the plain `.git` / `**/.git` paths, for a linked
  worktree whose `.git` is a file) is also denied, so a worker can't rewrite `.git/config` and turn a
  later `git diff` (diff.external) or `git status` (core.fsmonitor) into a command runner. This edit
  deny is opencode-only; Claude Code's own refusal of `.git` edits was measured under `acceptEdits`
  only. Known gap: a shell redirect from any allowed command (e.g. `echo x >> .git/config`) is not
  covered by the edit rules and holds for opencode in every mode and for Claude under `bypassPermissions`
  (measured).
- `effort` does not apply; `variant` (profile key or `--variant`) selects an opencode model variant.
- `--mcp-config` is claude-runtime only.

An opencode orchestrator uses the launcher the same way: write the task, run the launcher from the
project root, read only the result block. `.lean-worker/` is the same for both tools. opencode's bash
tool has no background mode, so the skill tells the orchestrator to pass a `timeout` above the worker's own.

## Launcher reference

Run from the project root (`.lean-worker/` is resolved relative to the current directory):

```
dotnet run --project <skill-dir>/launcher -c Release -- --task .lean-worker/inbox/fix-parser/task.md --profile code
```

| Option | Default | Meaning |
|---|---|---|
| `--task <file>` | required, unless `--continue-from` | Task prompt, piped to the worker |
| `--continue-from <run-dir>` | none | Fresh worker on a stopped run: its original task plus its report as the handoff. Profile defaults to that run's |
| `--profile <name>` | `defaultProfile` in profiles.json | Named profile |
| `--system <file>` | none | Per-task notes, appended after `project.md` |
| `--runtime claude\|opencode` | `claude` | Worker runtime (profile key `runtime`) |
| `--model <id>` | from profile | `provider/model`, or a bare Anthropic id. Overrides the profile's model or chain |
| `--effort`, `--variant`, `--tools A,B`, `--allow <pattern>` (repeatable), `--mcp-config`, `--max-budget-usd`, `--permission-mode` | from profile | Per-run overrides |
| `--wrap-up-at <share>` | `0.8` | Share of the budget after which tools are blocked and the worker hands off; `0` = off (profile key `wrapUpAt`) |
| `--prices <file>` | `<runs-root>/prices.json` | Price file merged over the shipped and personal ones (profile key `prices`) |
| `--timeout-minutes <n>` | `60` | Kill the worker after n minutes |
| `--no-project-notes` | off | Do not send `project.md` |
| `--replace-system-prompt` | off | Replace Claude Code's system prompt instead of appending to it |
| `--mode auto\|bare\|lean` | `auto` | Claude runtime: `lean`, or `bare` when an API key is set and wrap-up is off |
| `--keep-claude-md` | off | Do not exclude CLAUDE.md / AGENTS.md / `.claude/rules` (opencode: keep the project config) |
| `--keep-memory` | off | Lean mode: keep auto memory |
| `--cache-ttl 5m\|1h\|default` | `5m` | Prompt-cache lifetime for the worker (profile key `cacheTtl`). See below |
| `--keep-hooks` | off | Lean mode: load your user/project settings, hooks and plugins (or `"keepHooks": true` in a profile). Managed hooks always run |
| `--no-user-env` | off | Lean mode: do not carry your user settings' `env` block into the worker |
| `--claude-settings <file>` | none | Passed to `claude` as `--settings`. It counts as an API key only if it has an `apiKeyHelper`. In lean mode it is merged with the exclusions |
| `--no-bare` | off | Alias for `--mode lean` |

A setting comes from the command-line option if one is given, otherwise from the profile,
otherwise from the built-in default. The worker always runs with `--strict-mcp-config`, so it
gets no MCP server unless the profile or `--mcp-config` names one.

Other commands (same `dotnet run ... --` prefix): `quota`, `cost`, `stats`, `prices` (see above), and
`hook`, which the launcher installs into workers itself.

Exit codes: `0` success, `1` the worker reported an error or failed, `2` the launcher failed,
`3` the worker wrapped up near its budget and left a handoff.

## Cost notes from real runs

- **Cache TTL.** A `claude -p` worker on a subscription login writes its cache with the 1-hour TTL by
  default, which costs 2x base input. An API key defaults to 5 minutes, at 1.25x. A worker's calls
  follow each other within seconds, so the launcher sets `CLAUDE_CODE_PROMPT_CACHE_TTL=5m` for the
  worker. On two measured runs the 1-hour writes were 25-45% of the cost; at 5m they cost about 40% less.
- **Tools are the base now.** With instructions excluded, most of a worker's first call is tool schemas.
  Bash, Edit, Write and WebFetch are large. Give each profile only the tools its task class uses.
- **Pre-approve harmless read commands** (`ls`, `cat`, `pwd`) in coding profiles. Each denied call is a
  wasted API call.
- **Research workers** grow with every fetched page: a docs task peaked at 99.5k tokens, and the
  WebFetch summaries cost another 16%. Give exact URLs and split research by source.
- **Keep the orchestrator short-lived.** Its long history is re-read on every one of its turns, so it is
  easily the most expensive part. Let a `review` worker check a worker's result, and read only verdicts.
- **Other providers' runtimes misprice.** Claude Code prices a GLM model through z.ai's endpoint at its
  own guess ($0.0200 where the list price was $0.0074 in one run), and opencode prices coding-plan models
  at 0. The launcher prices every call with the price book; the runtime's figure is shown only when it
  differs by more than 5%.
- **Claude Code's stream undercounts output.** Its `assistant` events repeat the output count from the
  start of a message; only the `message_delta` of `--include-partial-messages` carries the final one.
  The launcher reads that, which matched Claude Code's own cost to the cent. z.ai's endpoint sends
  every count in the delta and zeros at the start; the meter keeps the largest value of each field.

## What is recorded

`.lean-worker/runs/<stamp>-<name>/` holds:

- `task.md`
- `system.md`, the notes the worker actually received (the worker reads them through a content-addressed path `<runs-root>/system/<sha12>.md` that stays the same across runs of identical content, so Anthropic's prompt cache survives a repeat; opencode prints the instructions file's path into the system prompt, and a per-run path breaks it)
- `command.txt`
- `stream.jsonl`, the worker's JSON output (Claude Code's token-by-token deltas are left out)
- `settings.json` (claude runtime, lean mode) or `opencode-config.json` (opencode runtime)
- `wrapup.json` when the wrap-up fired, and `hook.log`, one line per hook check
- `summary.json`
- `report.md`

`.lean-worker/runs.jsonl` gets one JSON line per run, recording:

- profile, runtime, provider, model (and why it was picked from the chain), effort and billing;
- status, turns, and API calls (deduplicated by message id);
- cost as metered with the price book, and the cost the runtime reported, if any;
- budget, wrap-up threshold, whether it fired, and hook checks;
- the input / cache-write / cache-read / output / thinking token split;
- first-call and peak context, the first call's cache-read tokens, and the cache-read share of the first call (3 decimals);
- quota before and after the run, and the percentage it used, on a subscription with a quota adapter;
- the next model the run offered (`escalate_to`), if any;
- the write scope, the files the run changed in the git working tree, and those outside the scope.

Each row carries `schema_version` (currently 1). Fields may be added within a version. The version goes up
when a field changes meaning or is removed. Rows written before the field existed count as version 0: the same
fields, without `schema_version`, `escalate_to` and the write-scope fields. Other tools read the ledger
directly, or its aggregate with `stats --json`: per profile and model, runs, successes, wrap-ups,
escalations, cost, cost per success and quota % per run.

## Verification status

Checked on 2026-09-29 with Claude Code 2.1.284, opencode 1.18.32 and .NET SDK 10, on Linux (Fedora).

**Tested on Linux:**
- the launcher builds with no warnings; 48 unit tests (`dotnet test --project tests/LeanWorker.Tests`): pricing,
  stream parsing for Anthropic, z.ai and opencode (including permission denials and cut-off sessions), the
  meter, quota parsing, the key-routing guard, the write scope, the profile templates and `stats`;
- `tests/acceptance.sh`, 24 checks with real workers (about $0.65 list price in total, most of it one
  deliberately overpriced case), all passing:
  - wrap-up, continuation and continuation of a continuation on Claude Haiku (subscription login);
  - the meter matching Claude Code's own cost within 5% (to the cent in practice);
  - `--keep-hooks` loading a project hook, and the default keeping it out;
  - wrap-up off, a price change without a rebuild, and the unknown-model policy;
  - GLM-5.3 on z.ai's GLM Coding Plan, in the claude runtime (Anthropic-compatible endpoint) and in
    the opencode runtime, including wrap-up and a cross-runtime continuation;
  - the quota command and the chain fallback over a quota threshold;
  - an opencode permission denial, model traits, the write scope, `stats --json` and `cost`;
  - run and scratch paths containing a space;
- `install.sh` into a sandbox home and project, for Claude Code
  and opencode orchestrators, user and project scope, with the permission and delegation-rule steps, and
  a re-run that duplicates nothing: `tests/installer-test.sh`, 8 checks;
- an opencode orchestrator (MiniMax-M3, Claude Code skill import off) finding the project-scope skill in
  `.opencode/skills` and launching a worker through it (2026-09-30);
- the hook's overhead: about 60 ms per tool call.

From earlier checks, still valid: lean mode keeps the project's CLAUDE.md, AGENTS.md and
`.claude/rules` out of the worker (codeword probes), and `--keep-claude-md` brings them back.

**Not yet tested:**
- MiniMax quota in the CN region (the international endpoint was verified on 2026-09-29);
- a `--bare` run with an API key;
- DeepSeek end to end (its prices and endpoint ship from its documentation). MiniMax-M3 on the
  Token Plan runs as a worker in both runtimes (2026-09-29/30; acceptance cases 11 and 12 with
  `LW_OPENCODE_MODEL=minimax-coding-plan/MiniMax-M3`).

## License

MIT
