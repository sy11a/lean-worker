# lean-worker: what exists today (input to the live run-status design)

Read-only map of this repository at master `536b6a4` (2026-11-22). Paths without a prefix are under
`skills/lean-worker/launcher/`; `L` is the repository root. `ced7d33` (the sy11a gate bump, #136) did not
touch
the launcher sources cited here. Private companion: sy11a_investigations, live-status-relay/ (not public); it
holds the relay internals and the machine-level detail this public file leaves out.

This file has three parts: the launcher map (sections 1-9 and the gaps list), written by a survey session; the
relay as its commands are seen from outside (section 5); and the verification log of the design session, which
re-checked the claims [README.md](README.md) leans on. Run-dir names and paths in examples are invented
unless the text says otherwise.

---

## 1. Run directory, files, ledger

**Location and name.** The run dir is not under /tmp. It is `<runs-root>/runs/<yyyyMMdd-HHmmss>-<safe-name>`
(`LaunchRun.cs:1020-1021`).
- The stamp is `_started = DateTimeOffset.Now`, local time (`LaunchRun.cs:1020`).
- The name is `--name`, else the task file's folder name (`LaunchRun.cs:1009`). A continuation takes `--name`,
  the previous summary's name, or `continuation` (`:1003`). Gate rounds get `<original>-gate<N>` (`:991`).
- `SafeName` turns everything except `[A-Za-z0-9._-]` into `-` (`Launcher.cs:189-190`).
- The runs root is `--runs-root`, default `.lean-worker` (`LaunchRun.cs:463`). A relative root is re-anchored
  on the task's workspace root when `<workspace>/.lean-worker` exists (`LaunchRun.cs:600-617`).
- The directory is made with `Directory.CreateDirectory` (`LaunchRun.cs:1027`), which does not fail on an
  existing dir. Two runs with the same name started in the same second share one directory, and `task.md` is
  overwritten. There is no uniqueness suffix.

**The only /tmp item.** The opencode runtime makes a scratch config home
`Directory.CreateTempSubdirectory("lean-worker-<rundir-name>-")` (`OpencodeRuntime.cs:67-80`). It holds the
plugin copy and is deleted in a `finally` after the worker (`LaunchRun.cs:1146-1149`, `:2744-2763`). Claude
runs have no scratch dir (`ClaudeRuntime.cs:63`, `ScratchDirectory: null`).

**Files and when they are written** (README list: `L/README.md:664-680`):

| file | writer | when | live or end |
|---|---|---|---|
| `task.md` | `LaunchRun.cs:1028` | before the worker | once |
| `system.md` (+ shared `<runs-root>/system/<sha12>.md`) | `LaunchRun.cs:1112-1114` | before the worker | once |
| `settings.json` (claude lean) | `ClaudeRuntime.cs:121-122` | Prepare | once |
| `opencode-config.json` | `OpencodeRuntime.cs:268` | Prepare | once |
| `command.txt` (command text only, no pid) | `LaunchRun.cs:1141` | just before spawn | once |
| `stream.jsonl` | `WorkerProcess.cs:25`, `:208` | each stdout line | **live**, flushed per line |
| `stderr.txt` | `WorkerProcess.cs:27`, `:41` | each stderr line | live, StreamWriter buffered (no per-line flush) |
| `stderr.txt.launcher` | `WorkerProcess.cs:218` | metering failed, worker stopped | event |
| `hook.log` | claude: `Commands.cs:78` (local ISO time, one line per PreToolUse); opencode: `opencode-plugin.ts:445` (UTC ISO + tool name per call) | each tool call, only when wrap-up is on | **live** |
| `redirect-denied.log` | `opencode-plugin.ts:452` | each denied redirect | live |
| `wrapup.json` {at, spent_usd, threshold_usd, budget_usd, reason} | `Meter.cs:63-75` | once, when spend passes the wrap-up share | event, the on-disk phase marker |
| `summary.json` | `LaunchRun.cs:2526` (temp + rename, `Launcher.cs:230-243`) | after worker, gate, worktree decision | end, atomic |
| `report.md` (worker text + evidence block) | `LaunchRun.cs:2527` | end | end, atomic |
| gate report / failure files | `Gate.WriteFailureAsync` / `Gate.RunAsync` (`LaunchRun.cs:2092`, `:2142`) | after the worker | end |

**`runs.jsonl`** is at `<runs-root>/runs.jsonl` (`LaunchRun.cs:2528`). The ledger stays in the invoking repo
even with `--worktree` (`LaunchRun.cs:22-29`, `WorktreeRun.cs:1-4`). The operator's machine holds several such
ledgers, one per repository that uses the skill.
- **Row schema.** The row is the full `summary.json` object, compacted (`LaunchRun.cs:2528`). It is built in
  `BuildSummary` (`LaunchRun.cs:2382-2443`):
  - identity: `schema_version` (=1, `Launcher.cs:30`), `timestamp` (start), `name`, `run_dir`;
  - model and runtime: `profile`, `runtime`, `provider`, `model`, `model_reason`, `effort`, `mode`, `billing`,
    `cache_ttl`, `hooks`;
  - outcome: `status`, `subtype`, `terminal_reason`, `exit_code`;
  - work: `num_turns`, `api_calls`, `duration_ms`;
  - money: `total_cost_usd`, `reported_cost_usd`, `budget_usd`, `wrap_up_usd`, `wrapped_up`, `hook_checks`;
  - continuation: `continued_from`, `escalate_to`, `worktree{path,branch,action,reason}`,
    `gate{chain_id,round,outcome,exit_code,count,decision,error,report_path,duration_ms,commands?}`
    (`:2487-2517`);
  - tokens and context: `model_traits`, `tokens{input,cache_write,cache_read,output,thinking}`,
    `context_first_call`, `first_call_cache_read`, `first_call_cache_read_share`, `context_peak`;
  - scope: `permission_denials`, `write_scope`, `changed_files`, `out_of_scope`;
  - session and quota: `session_id`, `quota_before`, `quota_after`, `quota_used_pct`, `notes`;
  - optional: `model_requested` (`:2445-2449`), `git_metadata_verified` (`:2457-2465`), `evidence_lines`
    (`:2525`).
- **`StatsRow`** is not a ledger row. It is the `stats` aggregate, one per profile and model: `(Profile,
  Model, Runs, Success, WrappedUp, Escalations, CostUsd, CostPerSuccessUsd, QuotaPctPerRun)`
  (`StatsRow.cs:6-7`, computed in `Commands.cs:277-297`).
- **When.** One row per `LaunchRun`, at the very end, after the gate and the worktree decision
  (`LaunchRun.cs:276-280`, `:2519-2529`). A gate chain writes one row per round. A launcher that dies or
  throws writes no row and no `summary.json` (`LaunchRun.cs:287-295` rethrows; `Program.cs:37-43` prints
  `LEAN-WORKER LAUNCH FAILED`).
- **Atomicity under concurrency.** The write is a bare `File.AppendAllTextAsync` with no lock and no temp file
  (`LaunchRun.cs:2528`). A row is about 5 KB, more than one write buffer. Whether .NET's append mode uses
  `O_APPEND` on Linux is not verified, so concurrent launchers on one runs root could interleave or tear rows.
  Readers skip unparsable lines (`Commands.cs:305`, `Json.TryParseObject`), so a torn row is silently dropped
  from `stats`. The only race-safe write in the area is the content-addressed system file
  (`Launcher.cs:205-221`, `AtomicWrite`).

## 2. stream.jsonl, parsing, Meter, end states

**Writer.** The launcher copies the worker's stdout line by line.
- `PumpStdoutAsync` reads with `ReadLineAsync` (`WorkerProcess.cs:96-144`).
- `TryMeter` writes and **flushes every kept line** under a lock (`WorkerProcess.cs:206-210`), then calls
  `onLine` to meter it.
- `IRuntime.Record` filters lines: claude drops `stream_event` deltas except `message_start` and
  `message_delta` (`ClaudeRuntime.cs:178-187`); opencode keeps everything (`IRuntime.cs:24`).
- After the worker exits the pump gets at most 30 s to drain (`WorkerProcess.cs:62`).

**Claude events parsed** (`ClaudeRuntime.cs:220-262`):
- `stream_event/message_start`: usage starts, the id becomes `_streaming` (`:226-230`).
- `stream_event/message_delta`: final output tokens (`:231-234`).
- `assistant`: usage again (`:238-241`). Per field the largest value wins (`Merge`, `:204-218`).
- `result`: `is_error`, `result` (report), `session_id`, `subtype`, `terminal_reason`, `total_cost_usd`,
  `num_turns`, `permission_denials`, thinking tokens (`:243-261`).
- **Not parsed:** `tool_use` content blocks in `assistant` events (tool name and input), `user` tool_result,
  `system/init|status|task_*`, `rate_limit_event`.
- Observed in a real claude stream of another repository: only `assistant` and `user` events carry a
  `timestamp`. `system/status` carries `"status":"requesting"`.
- **Turns are known only at the end** (`num_turns` on `result`). Live, turns can only be counted from
  `message_start` events.

**opencode events parsed** (`OpencodeRuntime.cs:348-394`):
- `text`: report text per messageID (`:354-367`).
- `error`: sets IsError (`:368-373`).
- `tool_use`: counted only when `state.status=error` with a permission message, as a denial (`:374-379`).
- `step_finish` with `part.tokens`: Turns++, LastStepReason, usage `{input, output, reasoning, cache.read,
  cache.write}`, id = part id (`:380-391`).
- `sessionID` is taken from any line (`:350`).
- Every event carries `timestamp` (epoch ms), and `step_finish` carries `part.cost`. **The launcher ignores
  both.** Observed shapes, from a real opencode stream of another repository: `step_start`,
  `step_finish{reason,tokens,cost}`, `text`, `tool_use{part.tool, part.callID, part.state.status,
  part.state.input}`.
- `Finish` derives HasResult, Truncated and Subtype after exit (`:403-423`).

**opencode-plugin.ts.** It holds no metering. The `tool.execute.before` hook appends `hook.log` (`<ISO>
<tool>`) and throws the wrap-up reason when `wrapup.json` exists (`opencode-plugin.ts:432-447`). It also runs
the redirect guard (`:448-459`). It is active only with `LEAN_WORKER_WRAPUP=1` (`:439`, set at
`OpencodeRuntime.cs:233`).

**Meter (live)** (`Meter.cs`):
- Prices each call with the price book; an unknown model is priced as the dearest (`:24-48`).
- Keyed by call id, so the last report wins and spend is re-summed (`:50-61`).
- Holds `Spent`, `WrappedUp`, `CapReached`, `Notes` (`:16-19`).
- Wrap-up: when `Spent >= budget*wrapUpAt`, it writes `wrapup.json` once (`:63-75`).
- Hard cap: when `Spent >= budget`, `Add` returns true (`:76-80`). `HandleUsageLine` passes that up
  (`LaunchRun.cs:1456-1474`), the pump calls `StopOnce` and the worker's tree is killed
  (`WorkerProcess.cs:78-88`, `:190-193`).
- The Meter knows nothing of turns, time or tools. Turns live in `Outcome.Turns`: opencode counts them live,
  claude only at the end (`Outcome.cs:15`).

**Where the states are decided.** `Status()` at `LaunchRun.cs:1991-2044`, in priority order:
1. `budget-exceeded`: the cap kill happened (`_capKilled`).
2. `truncated`: `Outcome.Truncated`, an output limit with an empty last step (`Outcome.cs:39-40`).
3. When there is no result: `timed-out` if the timeout fired, `crashed` on a non-zero exit, else `no-result`.
4. `error` when the outcome is an error, else `success`.
5. Then `wrapped-up` replaces success or error when the Meter wrapped up (`:2029-2032`).
6. Then `out-of-scope` replaces success or wrapped-up when git metadata was tampered with (`:2034-2041`).

Exit codes are 0 / 3 / 1 (`LaunchRun.cs:285`). Gate chains map to 0 / 4 / 5 (`Launcher.cs:73-79`). The timeout
is `Process.WaitForExit(TimeSpan)` then a tree kill (`WorkerProcess.cs:195-204`), default 60 minutes
(`Options.cs:42`, `:102`).

**Wrap-up as a phase.** It exists: `Meter.WrappedUp`, the `wrapup.json` marker and the hook's denials. It is
the only phase visible on disk mid-run. Nothing marks "gate running" or "finishing": the gate runs after the
worker, with no file until it reports.

## 3. Process model, signals, final output

**Spawn.**
- `Process.Start` with stdin, stdout and stderr redirected (`WorkerProcess.cs:146-186`).
- `ApplyGroup` rewrites the start info to `/usr/bin/setsid --wait <exe> args...` (`Prepared.cs:50-60`). The
  worker becomes a **session and group leader**.
- `ProcessGroup.ResolveAsync` checks in /proc that the pgrp equals the pid (`ProcessGroup.cs:93-113`).
- After the worker exits, `FinishGroupAsync` counts the group members still alive, sends SIGTERM, waits 5 s,
  sends SIGKILL, waits 1 s, using `/usr/bin/kill -s SIG -- -pgid` (`ProcessGroup.cs:125-158`, `:192-238`).
- `Leftovers` and `Survivors` go to notes and evidence (`WorkerGroup.cs`, `Prepared.cs:31-44`).
- There is no cgroup and no `systemd-run` scope. A cgroup scope is proposed in open issue #121 and described
  in `L/README.md:629-639`.
- `setsid` without a fork exec's in place, so `p.Id` is the worker's pid and pgid (inferred from the `Pgrp ==
  pid` check, `ProcessGroup.cs:103`).

**Pid on disk.** Not recorded anywhere. `p.Id` lives only in memory (`WorkerProcess.cs:22-23`). `command.txt`
holds only the command line (`LaunchRun.cs:1141`). Grep finds no pid write in the launcher.

**Signal handling.** None. There is no `PosixSignalRegistration`, `Console.CancelKeyPress` or
`AppDomain.ProcessExit` anywhere in the launcher (grep: 0 hits).

**If the launcher is killed:**
- The worker is in its own session, so neither SIGTERM/SIGKILL to the launcher nor a terminal SIGINT/SIGHUP
  reaches it.
- Its stdin is already closed (`WorkerProcess.cs:48-49`). Its stdout pipe loses its reader, so it fails on its
  next write (EPIPE or SIGPIPE; the worker's actual reaction is not verified).
- The group cleanup does not run, and `FinishGroupAsync` sits in a `finally` that SIGKILL skips.
- No `summary.json`, `report.md` or ledger row is written. For opencode the /tmp scratch config home leaks.
- Footprint of such an orphan: a run dir with `stream.jsonl` and no `summary.json`.

**Final output.** Everything goes to **stdout**, after the run (`LaunchRun.cs:2531-2541`):
- `LEAN-WORKER RESULT`, then lines `run:`, `status:`, `model:`, `runtime:`, `work:` (turns, API calls,
  elapsed), `cost:`, `budget:`, `gate:`, `tokens:`, `context:`, `quota:`, `worktree:`, `files:`
  (`:2562-2618`).
- Then `note:`, `continue:`, `escalate:` and `WARNING:` lines (`:2666-2703`).
- Then `--- worker report ---`, truncated at `--report-max-chars` (`:2705-2731`).
- Then `## Evidence (written by the launcher)` and its lines (`:2733-2739`, `EvidenceBlock.cs:23`, `:35-57`).
- Only the git-tamper warning goes to **stderr** (`:2535-2538`).
- A launch failure prints `LEAN-WORKER LAUNCH FAILED: ...` on stdout, exit 2 (`Program.cs:41-42`).
- Nothing is printed while the worker runs, apart from early notes.

## 4. CLI surface

Dispatch is in `Program.cs:25-35`: `hook`, `quota`, `cost`, `stats`, `profiles`, `prices`, `sweep-worktrees`;
anything else is a launch.

- **Launch flags** (`Options.cs:7-80` usage text, parsed at `:119-200`):
  - task: `--task`, `--continue-from`, `--profile`, `--system`, `--name`;
  - model: `--runtime`, `--model`, `--effort`, `--variant`;
  - tools and scope: `--tools`, `--allow` (repeatable), `--write-scope` (repeatable), `--mcp-config`;
  - budget: `--max-budget-usd`, `--wrap-up-at`, `--prices`;
  - mode and config: `--permission-mode`, `--runs-root`, `--claude-settings`, `--mode`, `--no-bare`,
    `--replace-system-prompt`, `--no-project-notes`, `--cache-ttl`;
  - worktree: `--worktree`, `--worktree-base`;
  - limits: `--timeout-minutes`, `--report-max-chars`;
  - keep switches: `--keep-claude-md`, `--keep-memory`, `--keep-hooks`, `--no-user-env`, `--no-hooks` (no-op);
  - gate: `--no-gate`, `--gate-max-rounds`;
  - help: `-h`/`--help`.
  - The arg loop is shared through `ArgReader.Read` (`ArgReader.cs:13-24`). Subcommands use their own `Flags`
    parser (`Commands.cs:16-36`).
- **Subcommands:**
  - `hook --run-dir` (`Commands.cs:67-98`).
  - `quota [--provider] [--json] [--max-age s]` (`:100-145`). It reads a cache at
    `~/.cache/lean-worker/quota-<provider>.json`, honouring `XDG_CACHE_HOME` (`Quota.cs:16-24`, `:59-63`).
  - `cost --claude <id|file> [--provider] | --opencode <ses_id>` (`:147-195`).
  - `stats [--since] [--json] [--runs-root]` (`:299-345`).
  - `profiles [--runs-root] [--json]` (`:351-428`).
  - `prices` (`:430-447`).
  - `sweep-worktrees [--repo]... [--root] [--older-than-days] [--apply] [--protect]...`
    (`WorktreeSweep.cs:68-80`, options at `:96-205`).
  - **There is no `status`, `list`, `ps` or `watch` command.**
- **Formatting.** Plain fixed-width text tables built with `string.Create` and alignment specifiers, no colour
  or TUI (`Commands.cs:333-343`; `ProfilesTable` sizes columns from the data, `:406-417`). `--json` prints
  indented JSON, and `stats --json` carries `schema_version`, `runs_file`, `since`, `cost_basis`, `groups`
  (`:307-331`). The sweep prints `REMOVE|KEEP <reason> <path>` lines (`WorktreeSweep.cs:386`).
- **Identity and env vars.**
  - No owner or session identity exists for the launching session. No `LW_*` variable is read by the launcher
    (grep `LW_`: 0 hits in launcher code); `LW_OPENCODE_MODEL` is used only by `tests/acceptance.sh`
    (`L/AGENTS.md:33`).
  - Env vars read: `PATH`, `ANTHROPIC_API_KEY` (`LaunchRun.cs:836`), `CLAUDE_CONFIG_DIR`
    (`ClaudeRuntime.cs:168`, `Commands.cs:199`), `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME`
    (`OpencodeRuntime.cs:276`, `Runtimes.cs:60`, `Quota.cs:61`, `PriceBook.cs:92`), and each provider's
    `keyEnv` (`Runtimes.cs:43`). The gate copies the environment (`Gate.cs:207`).
  - Env vars set for the worker: `LEAN_WORKER_RUN_DIR`, `LEAN_WORKER_WRAPUP`, `LEAN_WORKER_XDG_CONFIG_HOME`,
    `OPENCODE_*` (`OpencodeRuntime.cs:225-245`), and `CLAUDE_CODE_PROMPT_CACHE_TTL` / `ANTHROPIC_*`
    (`ClaudeRuntime.cs:66-89`).
  - Run ids: the run dir name is the de-facto id. `ChainId` is round 1's dir name (`LaunchRun.cs:1022-1026`).
    `session_id` is the worker runtime's id (`Outcome.cs:12`).
- **Mismatch.** `SKILL.md:157` tells delegators to run `... -- --profiles`. The subcommand is `profiles`
  without dashes (`Program.cs:31`); `--profiles` goes to `Options.Parse` and fails as an unknown option
  (`ArgReader.cs:20-22`).

## 5. Skill side and the session relay (interface level)

### 5a. What the skill tells the orchestrator

- `skills/lean-worker/SKILL.md:139-146` says: "A worker can outlast the shell tool's timeout, so:
  - **Claude Code:** run it with the Bash tool **in the background** (`run_in_background`) and wait for the
    completion notification; do not poll.
  - **opencode:** its bash tool has no background mode. Pass the tool's `timeout` (milliseconds) above the
    worker's own limit, e.g. `4800000` ..."
- `SKILL.md:161-167`: run it from the project root; the workspace is anchored on the task file's git root.
- `SKILL.md:174-198`: read only the result block, "Do not read `stream.jsonl`", verify the done-criterion with
  one command.
- `SKILL.md:315-321` lists the other commands; `SKILL.md:323-333` describes the records.
- `templates/orchestrator-rule.md:1-7`: "This session orchestrates. ... writes the task, reads the worker's
  result block, and verifies with one command."
- Neither file mentions detached units, `msg-relay`, `sy11a-unit-run` or a terminal pane (grep: 0 hits). The
  completion signal today is the harness's own background-task notification (Claude Code) or a blocking bash
  call (opencode).

### 5b. The relay and the unit runner, as their users see them

The operator's machine has two private command-line tools that the design rides on. Their behaviour as seen by
a
user of the commands (verified in the private companion note):

- **`msg-relay send <name> --text <t>` (or `--file <f>`)** delivers a text message to a named agent session,
  from any process. There is no relay daemon: it is a command, hooks in the receiving session and a file
  spool.
- **To an opencode session** the message is submitted as a prompt, so it **starts a model turn at once**. Only
  opencode sessions registered with the relay are reachable.
- **To a Claude Code session** the message is queued and shown to the session **at its next tool call or
  prompt**. **An idle Claude session is not woken** by a message.
- **Names change on restart.** A Claude Code session's relay name is derived when it starts; after a restart
  it
  has a new one, and a message to the old name fails after a few retries.
- **Sender detection** walks the parent processes to the nearest live Claude Code session, so a command run
  inside a headless `claude -p` worker resolves to the worker, not to the orchestrator.
- **There is no progress or status message type**, no coalescing and no delivery receipt.
- **`sy11a-unit-run <owner> <unit> [--log <file>] -- <cmd>`** runs `<cmd>` as a detached `systemd-run --user`
  transient service and, when the unit stops, sends `<owner>` **one `UNIT DONE` message** through `msg-relay`
  with the unit's result, exit status and the last lines of the log. The operator's rule is that agents never
  wait inside a turn: a job longer than a few minutes runs this way and the agent ends its turn.
- **The unit does not inherit the session's environment**: only a fixed set such as `PATH` and `HOME` is
  passed
  in. A launcher started this way cannot see `CLAUDE_CODE_SESSION_ID` or any other variable of the session
  that
  started it, unless the value is put on the command line (the session's shell expands it before the unit
  starts).
- The runner has no switch to skip the `UNIT DONE` message.


## 6. Worktrees, sweep, concurrency (prior art for orphan detection)

**`--worktree` path.** `PathFor` builds `<parent-of-repo>/<repo>-<safe-name>` (`WorktreeRun.cs:117-122`).
- **Mismatch with the operator rule of 2026-11-17** (`~/.claude/CLAUDE.md`, worktrees go under
  `<root>/.worktrees/<project>/<name>`, never `../<repo>-<name>`): the launcher still creates sibling
  worktrees.
- Mutual exclusion is only "path or branch already taken: refuse" (`WorktreeRun.cs:147-158`).
- The run removes its own worktree only when it is untouched (`WorktreeRun.cs:1-10`). A launch that dies is
  handled by `FinishWorktreeOnFailureAsync` (`LaunchRun.cs:287-295`), except on SIGKILL.

**Sweep liveness test** (`WorktreeSweep.cs`):
- It keeps worktrees that are `locked` (git lock), `protected` (`*-pinned`, `--protect`, `:21`), `nested`,
  `dirty` (`git status --porcelain`, `:456-460`), in progress (rebase or merge files, `:27-28`, `:486-509`) or
  `unmerged` (`:462-466`).
- **in-use:** some process has its `/proc/<pid>/cwd` inside the worktree (`:797-809`, `:857-888`). This is
  re-checked right before removal (`:377-384`).
- **young:** the newest mtime of the folder, HEAD, index and logs/HEAD is newer than `--older-than-days`
  (default 3, `:44`, `:131`, `:473-477`, `:516-550`).
- There is no pid file, lock file or heartbeat. Liveness is inferred from /proc cwd and mtimes. This is the
  in-repo prior art: **/proc scan + mtime age**, no recorded owner.

**Two launchers on one repository.** Nothing coordinates them:
- They share the runs root and the ledger (unsynchronised append, section 1).
- The same name in the same second gives the same run dir (section 1).
- The quota cache is shared (`Quota.cs:19-20`).
- The write-scope and changed-files snapshots are git-status based over the whole working tree
  (`LaunchRun.cs:1230-1276`; the invoking-repo snapshot excludes only `runs/` and `runs.jsonl`, `:1369-1370`).
  Two workers in one checkout would each report the other's edits as their own changed or out-of-scope files
  (inferred from the snapshot design; no test seen).
- No issue discusses concurrent launchers explicitly (not found).

## 7. Clocks and cost

- **Wall clock everywhere.**
  - Start: `DateTimeOffset.Now` (`LaunchRun.cs:1020`). Elapsed: `DateTimeOffset.Now - _started` (`:1161`).
    Both are wall clock and not monotonic, so an NTP step or suspend shifts them.
  - `duration_ms` covers the run start to the worker's exit. It excludes the gate (the gate's own
    `duration_ms`, `Gate.cs:62`, `:325`) and the later bookkeeping.
  - The timeout uses `Process.WaitForExit(TimeSpan)` (`WorkerProcess.cs:197`), a relative wait.
  - `wrapup.json.at` and claude `hook.log` use local time with an offset (`Meter.cs:69`, `Commands.cs:78`).
    The opencode `hook.log` uses UTC `toISOString()` (`opencode-plugin.ts:445`), so the zones are mixed.
  - Worker-reported timestamps (opencode `timestamp` ms on every event, claude `timestamp` on assistant and
    user events) are **not read**.
- **Open issue #60.** `--timeout-minutes` did not end an opencode run hung on an API step (about 10 h). The
  workaround was an external watchdog on stream.jsonl idle time of 20 min. The cause in the current code is
  not verified.
- **Cost.**
  - The launcher's cost is its own: `ModelPrice.Cost` = (input*in + (output+reasoning)*out + cacheRead*cr +
    cw5m*cw + cw1h*cw1h) / 1e6 * UsdRate, with an above-N-tokens tier (`ModelPrice.cs:5-11`).
  - Prices are resolved from the merged price book: shipped, then `~/.config/lean-worker/prices.json`, then
    the runs-root copy or `--prices` (`Commands.cs:61`, `PriceBook.cs:90-97`). An unknown model is priced as
    the dearest, or by the `unknownModel` policy (`PriceBook.cs:237-267`).
  - The worker-reported cost is claude's `result.total_cost_usd` only (`ClaudeRuntime.cs:257`). It is shown
    when it differs by more than 5% (`LaunchRun.cs:2581`). opencode's `part.cost` is ignored, and it is 0 for
    coding plans (`L/README.md:653-656`).
- **Skew and double counting.**
  - Calls are deduplicated by id (`Meter.cs:50-61`). Claude merges each field with max
    (`ClaudeRuntime.cs:204-218`), which the README says matches claude's cost to the cent
    (`L/README.md:657-660`).
  - Risk: a missing id falls back to `Guid.NewGuid()` (`ClaudeRuntime.cs:228`, `:240`;
    `OpencodeRuntime.cs:388`), so a re-reported call without an id would count twice.
  - Claude's `message_delta` is bound to `_streaming`, the last `message_start`. Interleaved sub-agent streams
    would misattribute; whether Claude ever interleaves is not verified.
  - Live spend during a run lags by one step: claude finalises output at `message_delta`, opencode only at
    `step_finish`.
  - A gate chain's total is `ChainCostUsd`, accumulated across rounds (`Launcher.cs:50-54`). `stats` sums
    per-row cost, rounds included (`Commands.cs:288`), with no double count.

## 8. Docs conventions and prior discussion

- **There is no `docs/` directory** (`ls docs`: no such file). Top-level docs are `README.md` (736 lines),
  `AGENTS.md` (82, with `CLAUDE.md -> AGENTS.md`), `INSTALL.md` (176) and `AGENT-INSTALL-PROMPT.md` (25).
- **Style.** One `#` title, then `##` sections in sentence case (`README.md` "## Why", "## Budget wrap-up and
  continuation", "### The worker runs in a process group of its own"). INSTALL uses "## Step N: ...".
  - Prose is hard-wrapped around 100-110 columns (README has 23 lines over 120, mostly long bullets; AGENTS
    has 4).
  - The tone is plain declarative, short sentences, bullets with **bold lead-ins**, and code in backticks.
    Issues are cited as `lean-worker#121` or `issue #116`.
- **Requirement ids.** `R-083` appears only in a test comment
  (`tests/LeanWorker.Tests/NoVendorReferenceTests.cs:6`), and `R-025` with "sy11a_analyzers case 0002" in
  `tests/LeanWorker.Tests/LeanWorker.Tests.csproj:4`. "Decision W5" appears in `WorktreeSweep.cs:1`, and
  `architecture.yaml:1` cites "case 0003". These ids live in sy11a_analyzers (cases and decisions); this repo
  has no local requirement list. The design record is GitHub issues (`L/AGENTS.md:5-6`).
- **`gh issue list --state all --search "status OR live OR telemetry OR dashboard"`** returned 13 hits, none
  about a live status view. The relevant ones:
  - #60 OPEN: the timeout does not end an opencode run hung on an API step. Its workaround is a
    stream.jsonl-idle watchdog, prior art for a stall signal.
  - #127 OPEN: hand over to the next model on a mid-run quota refusal. It notes that "under opencode the
    worker can also just stall ... looks alive until the timeout" and proposes `status: quota`.
  - #22 OPEN: design gaps (done-check verdict, wrap-up escalation, report reserve).
  - #121 OPEN: hardening leftovers, including a cgroup scope or `PR_SET_CHILD_SUBREAPER` for the process
    group.
  - #116 OPEN, #119 OPEN: the process-group pack.
  - #115 OPEN: truncated reported as success.
  - #11 CLOSED: runs.jsonl as a versioned contract plus `stats --json`. It is the precedent for adding a
    readable schema.
  - #106 CLOSED: worktree lifecycle and orphan sweep (W5).
  - #20 CLOSED: orchestrating from opencode.
  - Not found: any issue on live status, a dashboard, heartbeat, a pid file or owner identity. A prior private
    investigation founded a machine-wide dashboard daemon; this design's files are meant to be readable by it.

## 9. Build constraints

- **Project.** `net8.0` with `RollForward=LatestMajor`, Nullable, ImplicitUsings, `TreatWarningsAsErrors`,
  `InvariantGlobalization` (`LeanWorker.csproj:6-11`). No AOT, trimming, single-file or `PublishAot`. It runs
  as `dotnet run --project` (`SKILL.md:152`). `prices.json` and `opencode-plugin.ts` are copied next to the
  binary (`LeanWorker.csproj:20-24`). The SDK is pinned at 11.0.100 (`global.json:2-4`).
- **Packages.** Versions are central and overrides disabled (`Directory.Packages.props:6-8`). The only entries
  are `Sy11a.Analyzers [0.44.0]` (PrivateAssets, `Directory.Build.props:6-8`), `coverlet.MTP`, and `xunit.v3`
  for tests (`Directory.Packages.props:12-15`). The launcher has **no runtime package**.
- **Architecture rules.** The `LeanWorker` layer may use only `@dotnet-host`, principle
  `self-contained-launcher`, and the header says "uses only the .NET host APIs (process control, files,
  network, JSON) and no third-party package" (`architecture.yaml:2-5`, `:14-17`).
  - **Spectre.Console or any TUI package would break this rule** and needs an operator ruling: it loosens a
    gate, so it is `triage:operator` per `L/AGENTS.md`.
  - A `status` command in the same assembly fits the rules, but `LaunchRun` already carries `class_coupling:
    96` debt and `Commands` methods carry complexity debt (`architecture.yaml:21-57`). A new class is better
    than growing either.
- **Policy notes.**
  - "A policy finding fails the build: fix it, never suppress it" (`L/AGENTS.md:31-32`).
  - The verify set is in `L/AGENTS.md:21-33`: `dotnet test`, `sy11a-lint --full`, `sy11a-arch --affected`,
    `sy11a-test`.
  - The tests run in a serialised collection for process-global state
    (`tests/LeanWorker.Tests/LauncherProcessStateDefinition.cs:5-9`).
  - `install.sh` copies only `skills/lean-worker`, so an installed copy does not see the props
    (`Directory.Build.props:3-5`).

---

## Gaps relevant to live status (what is not on disk today)

**Not recorded anywhere:**
1. **Worker pid / pgid.** Memory only (`WorkerProcess.cs:22-23`). No liveness check is possible from outside
   except a /proc cwd scan, the sweep's trick, which is ambiguous when two workers share a checkout.
2. **Launcher pid.** Not recorded. No lock or pid file in the run dir.
3. **Owner identity.** No field for the launching session: no relay name, Claude session id, cwd or terminal.
   Nothing in the environment carries it to a detached launcher (5b).
4. **Start record.** `timestamp` and the other row fields exist only in the end-of-run row. A running run has
   only `task.md`, `command.txt` and the config files, with no machine-readable start record (profile, model,
   budget, timeout deadline).
5. **Heartbeat.** None. Proxies are the `stream.jsonl` mtime (flushed per line) and the `hook.log` mtime (per
   tool call, only with wrap-up on). The opencode `timestamp` per event is unread.
6. **Phase.** Only `wrapup.json`, which marks wrap-up. Nothing marks starting, worker running, gate running
   (round N), finishing (worktree decision, summary), done, or a launch that died. Done = `summary.json`
   present. A dead launcher leaves `stream.jsonl` without `summary.json`, the orphan signature, and a
   still-running worker looks the same.
7. **Live counters.**
   - Turns: opencode live in memory (`Outcome.Turns`), claude only at `result`.
   - Tokens and cost: in memory only (`Meter.Spent`, `Meter.Calls()`), never written mid-run.
   - Last tool activity: never parsed. It is only available in `hook.log` (opencode includes the tool name) or
     by re-parsing `stream.jsonl` (claude `assistant.content[].tool_use.name`, opencode `tool_use.part.tool`).
8. **Run id uniqueness and a registry.** Run dir names can collide (same name, same second). No cross-repo
   index exists: the several runs roots are separate, and a status view must know them or scan them.
9. **Completion signal.** Today it is the harness's own background notification, or a `sy11a-unit-run` wake
   carrying the last lines of the log. The launcher itself emits nothing at the end besides stdout and the
   files.
10. **Orphan handling.** No signal handlers. Killing the launcher leaves the worker (own session) running
    until its next stdout write fails, with no row, no summary, and a leaked opencode /tmp config home.

**What the relay already gives for free (section 5b):**
- One address scheme for both runtimes: `msg-relay send <name>` works from any process.
- The `UNIT DONE` completion wake through `sy11a-unit-run`.
- Immediate prompt injection for opencode owners; delivery at the next tool call or prompt for Claude Code
  owners.

**What the relay lacks for status:**
- No push into an idle Claude Code session.
- Every message to an opencode owner costs a model turn, so per-tick progress through the relay is expensive.
- No progress or status message type, no coalescing, no rate limit, no dedup, no delivery receipt.
- Owner names change on restart, so a name stored at launch can go stale.
- A detached launcher cannot discover its owner. The owner must be passed in or captured at launch, while the
  launcher is still a descendant of the owner's shell.
- Sender detection inside a `claude -p` worker resolves to the worker.

**Also noted (mismatches, filed as #138 and #139 in this repository, not acted on here):**
- `SKILL.md:157` uses `--profiles` where the subcommand is `profiles`.
- `WorktreeRun.PathFor` creates `../<repo>-<name>` worktrees, against the operator's 2026-11-17
  worktree-location
  rule.

## Verification log (design session, 2026-11-22)

The design in [README.md](README.md) leans on the claims below. Each was re-checked by reading the file or
running the command named; "confirmed" means the text above is right, "differs" means the design uses the
corrected fact. The re-checks of the private relay tools are in the private companion note.

**lean-worker launcher (master `536b6a4`; `git diff --stat 536b6a4 ced7d33 -- skills/` is empty):**
- Confirmed: the run dir is `<runs-root>/runs/<yyyyMMdd-HHmmss>-<safe-name>`, stamped with
  `DateTimeOffset.Now` and created with `Directory.CreateDirectory`, no uniqueness suffix
  (`LaunchRun.cs:1019-1027`).
- Confirmed: `stream.jsonl` is written and flushed per kept line under a lock (`WorkerProcess.cs:25`,
  `:206-210`).
- Confirmed: `ApplyGroup` rewrites the start info to `/usr/bin/setsid --wait <exe> ...` (`Prepared.cs:50-60`,
  `ProcessGroup.cs:23`), and `ResolveGroupAsync(p.Id)` runs right after `Process.Start`
  (`WorkerProcess.cs:22-23`).
- Confirmed: no `PosixSignalRegistration`, `CancelKeyPress` or `ProcessExit` in the launcher (grep, 0 hits);
  `Environment.ProcessId` is used only for the launcher's own group (`ProcessGroup.cs:260`).
- Confirmed: `summary.json` and `report.md` go through `Launcher.WritePlainAsync` (temp sibling + `File.Move`
  overwrite, `Launcher.cs:230-243`); the ledger row is a bare `File.AppendAllTextAsync`
  (`LaunchRun.cs:2519-2529`).
- Confirmed: the order of a run is trust snapshot, task, system notes, worker, status, gate, worktree, stats,
  summary, print (`LaunchRun.cs:270-281`); the exit code is 0 success / 3 wrapped-up / 1 other
  (`LaunchRun.cs:282-285`); gate chains map to 0 / 4 / 5 (`Launcher.cs:73-79`); a thrown launch prints
  `LEAN-WORKER LAUNCH FAILED` and exits 2 (`Program.cs:37-43`).
- Confirmed: the runs-root walk of the trust set skips `runs/` and `system/` (`LaunchRun.cs:1508-1516`), and
  the invoking-repo snapshot excludes `runs/` and `runs.jsonl` (`LaunchRun.cs:1365-1370`). A new file inside
  a run dir is therefore outside the gate's trust set and outside the changed-files snapshot.
- Confirmed: the opencode parser counts a turn on `step_finish` with `part.tokens` and ignores `part.cost`
  and `timestamp` (`OpencodeRuntime.cs:348-392`); the claude parser reads usage from `message_start`,
  `message_delta` and `assistant`, and turns only from `result.num_turns` (`ClaudeRuntime.cs:220-262`).
- Confirmed: the Meter writes `wrapup.json` once with a local-time `at` (`Meter.cs:60-75`); the claude hook
  writes a bare local timestamp per tool call to `hook.log`, without the tool name (`Commands.cs:78`).
- Confirmed: the subcommand dispatch is `hook`, `quota`, `cost`, `stats`, `profiles`, `prices`,
  `sweep-worktrees`, else a launch (`Program.cs:25-35`); subcommands parse `--flag value` pairs with
  `Commands.Flags` (`Commands.cs:16-36`).
- Differs (precision): `LEAN_WORKER_RUN_DIR` and `LEAN_WORKER_WRAPUP` are set only for opencode workers
  (`OpencodeRuntime.cs:232-233`). The claude runtime's environment (`ClaudeRuntime.cs:66-89`) sets neither,
  so a process inside a claude worker cannot tell today that it runs inside a lean-worker run.
- Confirmed: the worker inherits the launcher's whole environment plus the runtime's additions
  (`WorkerProcess.cs:167-181`). A launcher started inside an opencode 1.x worker therefore inherits the
  top-level session's `CLAUDE_CODE_SESSION_ID`, if any (inference from the code; not run).
- Confirmed: the launcher project targets `net8.0` (`LeanWorker.csproj:6`), while `global.json:2-4` pins the
  SDK at 11.0.100 and `RollForward=LatestMajor` lets the launcher run on a newer installed runtime
  (`LeanWorker.csproj:6-11`). Which runtime it actually runs on depends on the machine.
- Measured: kept-line rates of the six newest runs on the operator's machine (a python count over each run's
  `stream.jsonl` and `summary.json`): opencode 399 lines in 602 s (117 `step_finish`, 142 `tool_use`), 295 in
  359 s, 344 in 665 s, 331 in 1200 s (a timed-out run); claude 68 lines in 66 s (16 `assistant`, 10 of them
  with a `tool_use` block), 137 in 102 s. So at most about 1.3 kept lines per second, and well under one "turn
  or tool" event per second.

**Claude Code (public behaviour, observed locally):**
- A Claude Code session file `~/.claude/sessions/<pid>.json` carries `procStart`, which equals field 22
  (`starttime`, clock ticks since boot) of `/proc/<pid>/stat` (checked for one live session). Claude Code
  itself uses pid plus start time against pid reuse; the design copies that. The file format is not documented
  as a stable interface.
- A Bash child of an interactive Claude Code 2.1.330 session has `CLAUDE_CODE_SESSION_ID`, `CLAUDE_PID`,
  `CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_SESSION_ATTENDED` and `AI_AGENT` (names only,
  `env`).
  opencode 1.18.35 is installed (`--version`).

**Public survey sources re-checked:** herdr-core's README has `hc wait --children all`, "Core draws nothing",
`notes_quiet = 3` and the 180 s check-in (`gh api repos/herdrdev/herdr-core/contents/README.md`);
vibe-kanban #2495 is open; workmux #209 is open; agent-deck #1550 is closed.

**Not verified by the design session:** whether .NET's append mode uses `O_APPEND` on Linux; what an opencode
or claude worker does when its stdout reader disappears; every survey link not listed above.
