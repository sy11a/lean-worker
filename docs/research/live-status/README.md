# Live run status for lean-worker: research and design

- **Asked:** 2026-11-22 by the operator: a live "runs table" for the workers that one orchestrating
  session launched (turns, tokens, cost, elapsed, state, last tool activity), updated in real time,
  plus a completion signal back to the operator; herdr (https://herdr.dev) and its analogues as the
  reference; ride on the existing relay between opencode and Claude Code, no parallel channel.
- **Status:** research and design only. Nothing here is built. Every proposal needs the operator's
  word before it becomes a task.
- **Files in this directory:**
  [survey-fleet.md](survey-fleet.md) (herdr, herdr-core, the Herdr app, cmux, workmux, agent-deck,
  ccmanager, claude-squad, Conductor, omnara, vibe-kanban, claude_code_agent_farm),
  [survey-tui-builtins.md](survey-tui-builtins.md) (k9s, lazygit, btop, .NET rendering options,
  Claude Code and opencode built-ins),
  [current-state.md](current-state.md) (what the launcher does today, with file:line, the relay as
  its commands are seen from outside, and the verification log of this design).
- **Private companion:** sy11a_investigations, live-status-relay/ (not public). It holds the relay
  internals, the machine-level checks and the relation to the operator's private projects. Claims
  below marked "verified in the private companion note" rest on it.
- **Marks used below:** *verified* (with a file:line or a command this session ran), *survey* (from
  the survey files, which name their own sources), *inference*, *unverified*, and *proposal*. Field
  names, file names, flags and exit codes marked as a proposal are new names this design invents;
  they exist in no tool today. Run names, ids, pids and paths in examples are invented.
- **Review:** reviewed by GLM-5.3 on 2026-11-22 (2 major, 4 minor, 6 notes; all taken up).

## Summary

The launcher should write one small status file per run, `status.json` in the run directory,
replaced atomically at every phase change and at every turn or tool event, and one immutable pointer
file per run in a per-user index, `$XDG_STATE_HOME/lean-worker/runs/`. Both carry the launcher's and
the worker's pid with their kernel start time and the boot id, so any reader can tell a live run from
a dead one without trusting a quiet stream; they carry the owner session captured at launch. A new
`status` subcommand reads them: a one-shot plain or JSON listing of "my children" by default, a
`--watch` table redrawn in place with plain ANSI (no package), and a `--wait` that blocks until the
children end. The files are advisory: they drive display and waking, never a gate, scope or ledger
verdict. Status is pulled from files and never pushed over the relay. Completion keeps its existing
routes: the Claude Code background-task notification, or the `sy11a-unit-run` wake through
`msg-relay`; `status --wait` adds one optional route, a single wake per batch. The files are written
always (additive, best effort, no output or exit-code change), with an environment kill switch. No
daemon is needed in lean-worker; a machine-wide dashboard daemon can read the same files.

## 1. What the operator asked, and the constraints

The orchestrating session launches workers detached and learns nothing until the launcher prints its
result block (`LaunchRun.cs:2531-2541`, verified; nothing else is printed while the worker runs). The
wanted view: every run launched by this session, with turns, tokens, cost, elapsed time, state
(running, wrapped-up, done, failed), last tool activity, live; and a completion signal.

Constraints taken as given:
- The launcher is a .NET console app with no runtime package; `architecture.yaml:2-5`, `:14-17`
  allow the `LeanWorker` layer only `@dotnet-host` (verified).
- It targets `net8.0` with `RollForward=LatestMajor` (`LeanWorker.csproj:6-7`), so it runs on the
  newest .NET runtime installed; this repository pins the SDK at 11.0.100 (`global.json:2-4`), while
  an installed copy of the skill carries no `global.json` and builds with any SDK 8 or later
  (`Directory.Build.props:3-5`, `install.sh` copies only `skills/lean-worker`). The design uses only
  APIs present in .NET 8.
- The run directories and `runs.jsonl` are the source of truth.
- Two worker runtimes (`opencode run`, `claude -p`); the parent is either an opencode TUI session or a
  Claude Code session.
- Minimal moving parts; a basic flow must not need a daemon.
- Today's launcher behaviour is unchanged by default.

## 2. Survey

The long per-tool detail, with line-level source links, is in [survey-fleet.md](survey-fleet.md)
and [survey-tui-builtins.md](survey-tui-builtins.md). The table condenses it. "Daemon?" means a
long-lived process besides the agents themselves.

| Tool | Version | Discovery | Streaming | Rendering | Completion | Daemon? | What goes wrong |
|---|---|---|---|---|---|---|---|
| [herdr](https://github.com/herdrdev/herdr/tree/e8d7d9705a7a2c89cef4dd42079379f05685af87) TUI | 0.9.5 | owns every pty; agent found by the pane's foreground process group and a list of 24 agent names | screen text matched against per-agent manifests every 300/500 ms; optional hook/plugin reports over a socket | ratatui + crossterm multiplexer, sidebar | toast, OSC terminal notification, OS notification, sound; `herdr agent wait` | yes (server) | blocked dialogs read as idle or done (#5593, #6063, #4573); sources disagree (#3993, #5222) |
| [herdr-core](https://github.com/herdrdev/herdr-core/tree/c7517808d64f06609e4ecf0db1757005373cc3a3) `hc` | 0.1.21 | terminal records with holder processes; parent/child tree | JSON-RPC events with `seq`, 15 s heartbeat, `missed` plus a fresh snapshot | none ("Core draws nothing") | parent notes after a 3 s quiet period; `hc wait --children all` | yes | no status unless the program speaks OSC 7501; wrong finished-agent wait before 0.1.21 (#221) |
| [Herdr app](https://github.com/herdrdev/herdr-app/tree/317e673f1f1568b876cbaa0b41dabeb9153ecbea) | 0.1.16 | client of herdr-core | herdr-core events | GPU UI on vendored gpui | herdr-core's | herdr-core | not surveyed beyond the stack |
| [cmux](https://github.com/manaflow-ai/cmux/tree/5c8be11aa8e3f2ed2734c57981301f32aa62ffec) | 0.65.2 | its own panes; hook session JSON with pid | agent hooks over a socket; OSC 9/99/777 and OSC 7501 | Swift/AppKit + libghostty | rings, panel, desktop notification | the app is the server | a notification on every turn (#2077); none when background work runs (#43180); a 3 s fallback for missed Stop hooks |
| [workmux](https://github.com/raine/workmux/tree/748997d6e9a6cf4d8f25035ee996f85251bd65a9) | 0.1.284 | per-pane JSON in XDG state; tmux options | hooks call the CLI; dashboard refresh 2 s | tmux tab icons + ratatui dashboard | done icon until focus; stale after 1 h | no (optional sidebar daemon) | state deleted when a tmux query fails (#209, open); status written to the wrong pane by inherited `TMUX_PANE` (#304) |
| [agent-deck](https://github.com/asheshgoplani/agent-deck/tree/80494e527393b7ead00da30018e6ee7e60e9d7a0) | 1.16.39 | tmux sessions + SQLite + hook files | hooks while fresh (2 min), then a corpus-tested pane classifier; poll 2 to 10 s | Bubble Tea + web UI | notify-daemon writes parent inboxes and desktop notifications | optional | concurrent TUIs deleted each other's sessions (#1550, closed); a dead idle child never reported (#2806) |
| [ccmanager](https://github.com/kbwo/ccmanager/tree/b0948dd25b41511d67c2b0db386082d21a9fdfbd) | 4.4.11 | in-process ptys; `sessions.json` written on every change | headless xterm scanned every 100 ms, 1.5 s idle debounce | Ink | user status-change hooks | no | idle while busy (#227); a resize fires the idle hook (#73) |
| [claude-squad](https://github.com/smtg-ai/claude-squad/tree/070eb9387cda2be2e64aa1a32ee81be160c457a1) | 1.0.20 | tmux sessions + `instances.json` | `capture-pane` every 500 ms, hash diff | Bubble Tea, alt screen | the row's status only | optional AutoYes daemon | git lock race with the 500 ms loop (#284); no resume after reboot (#212) |
| [Conductor](https://www.conductor.build/llms-full.txt) | 0.98.0 | worktrees under `~/conductor/workspaces`; internals unverified | public API polled about every 15 s; internal transport unverified | native Mac GUI | completion sound; iOS app | a background service (details unverified) | chats stuck "working" (0.95.0 fix); a queued prompt reads idle before it starts (API docs) |
| [omnara](https://github.com/omnara-ai/omnara/tree/d3634b0c7e76d83a8868d95d03b872475d63066d) legacy | 2025-10 tree | the wrapper registers an instance in Postgres | transcript tail to an API, 30 s heartbeat, 60 s online TTL | web + mobile | Expo push, SMS | no local daemon (SaaS) | "waiting for input" spam from a screen-timing heuristic (#81, #145); replay on resume (#215) |
| [omnara](https://github.com/omnara-ai/omnara/tree/313ac70cc1991e013c6fc54813cb13eae6be6992) current | omnarad 0.2.27 | agents are event logs; machines run `omnarad` | SSE with a `sequence` id, 10 s heartbeat, `Last-Event-ID` | web dashboard, CLI | webhooks, Slack/Discord | server + machine daemon | SSE drops at about 16 s and resume misses deltas (#502) |
| [vibe-kanban](https://github.com/BloopAI/vibe-kanban/tree/3f095f0186f6e68d67143d38b02da2238f6524d8) | 0.1.46 (sunsetting) | SQLite rows + an in-memory child map, no pid stored | stream-json stdout to WebSocket; `try_wait` every 250 ms | React/Vite web, Tauri | sound + OS push | yes (local server) | Claude runs stuck "running" forever (#2495, open); restart marks rows failed without looking at processes |
| [claude_code_agent_farm](https://github.com/Dicklesworthstone/claude_code_agent_farm/tree/d88ba78ea5e50abed8e7f60e2cf00ce678b6375d) | no releases | one tmux session, a pane per agent, heartbeat files | `capture-pane` every 10 s, substring match | rich `Live` table at 1 Hz | none (auto-restart when a heartbeat is over 120 s old) | no | `~/.claude` corruption on concurrent start (CHANGELOG) |
| [k9s](https://github.com/derailed/k9s/tree/558caafe7ba067467de46b320cc22ef11fef9c34) | 0.51.0 | Kubernetes informer cache | cache re-read every 2 s (minimum 2 s), overlapping refreshes dropped | tview/tcell, changed cells highlighted | n/a | no | n/a (genre reference) |
| [lazygit](https://github.com/jesseduffield/lazygit/tree/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4) | 0.66.3 | git | timers (2 s, 10 s, 60 s) plus refresh after its own actions | gocui fork on tcell v3, dirty-cell writes | n/a | no | n/a (genre reference) |
| [btop](https://github.com/aristocratos/btop/tree/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a) | 1.4.8 | /proc | timer, 2000 ms default | hand-rolled ANSI: absolute moves, one write per frame inside `?2026`, columns dropped by width | n/a | no | n/a (genre reference) |
| [Claude Code](https://code.claude.com/docs/en/agent-view) | 2.1.330 (local) | `/tasks`; `claude agents --json`; `CLAUDE_CODE_SESSION_ID` in children | stream-json `system/task_*`, `background_tasks_changed`, `result` | `/tasks` dialog, footer count, agent view | a task notification on a later turn; `asyncRewake` hooks; no hook on a background Bash exit | a supervisor for `--bg` sessions | background commands have time limits in unattended and `-p` sessions; lost or leaked notifications (#20754, #110924) |
| [opencode 1.x](https://github.com/anomalyco/opencode/tree/f4e363c8295fa90a03dddc451882fffe8fbce357) | 1.18.35 (local), 1.19.6 | child sessions by `parentID`; only `OPENCODE_PID` in children | `run --format json` (`step_finish` with cost and tokens, `tool_use` on completion); SSE `/event` | opentui TUI | `session.idle`; a background subagent injects a synthetic prompt | no | `run` hangs (#62636, #62681); events dropped from JSON output (#64851, #62380) |
| [opencode 2.x](https://github.com/anomalyco/opencode/tree/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1) | 2.3.2 | adds `OPENCODE_SESSION_ID` in shell children; `opencode service status` | adds `retry`, `background_wait`; `session.execution.*` | opentui | background shell "notified when it completes"; `experimental.idle` hook | background service, process daemon | `session.idle` never emitted (#55358); hidden wake turns (#63894) |

License caveat: `claude_code_agent_farm` records "MIT + OpenAI/Anthropic Rider" in its CHANGELOG
(survey). It is read for ideas only; no code from it may be copied.

What the survey teaches this design (each point is taken up in section 4):
- **Status from the program, not the screen.** Every scraping tool (herdr TUI, claude-squad,
  ccmanager, the agent farm, legacy omnara) has open mis-detection bugs that follow each agent
  release; the structured ones do not have that class of bug. lean-worker is in the best position
  of all: the launcher already parses the worker's structured stream (`ClaudeRuntime.cs:220-262`,
  `OpencodeRuntime.cs:348-392`, verified). It is herdr-core's model, with the launcher as the
  program that reports its own status.
- **Never judge "done" from silence, never trust process exit alone.** vibe-kanban #2495 (Claude
  alive after `result`), opencode #62636 and lean-worker's own #60 (an opencode run hung about 10 h
  on an API step) show both failure directions.
- **A durable record written at launch with pid, start time and group id**, so a restarted reader
  can tell a live run from a recycled pid (ccmanager, agent farm; vibe-kanban stores no pid and
  cannot tell).
- **Reapers delete on proof only, file by file** (workmux #209, agent-deck #1550).
- **Done stays visible until seen** (cmux, workmux, herdr-core).
- **Notify the parent agent, coalesced** (herdr-core parent notes with `notes_quiet`, `hc wait
  --children`, agent-deck inboxes).
- **A cheap refresh at 1-2 s** (k9s, btop), one write per frame, columns dropped by width (btop).

## 3. Existing infrastructure today

Full detail with file:line in [current-state.md](current-state.md); the claims below were
re-checked in this session (its verification log).

**Launcher telemetry.**
- The run dir is `<runs-root>/runs/<yyyyMMdd-HHmmss>-<safe-name>`, local wall-clock stamp, no
  uniqueness suffix (`LaunchRun.cs:1019-1027`). Same name in the same second means one shared dir.
- Live on disk: `stream.jsonl`, flushed per kept line (`WorkerProcess.cs:206-210`); `hook.log`
  per tool call only when wrap-up is on; `wrapup.json` once when spend passes the wrap-up share
  (`Meter.cs:60-75`). Turns, tokens, cost and the last tool live only in memory (`Meter.Spent`,
  `Outcome.Turns`).
- At the end: `summary.json` and `report.md` atomically (`Launcher.cs:230-243`), one ledger row by
  a bare append (`LaunchRun.cs:2519-2529`), the result block on stdout.
- Not on disk anywhere: the launcher's pid, the worker's pid or group, the owner, a start record, a
  phase. A run whose launcher died looks exactly like a running one: `stream.jsonl` without
  `summary.json`.
- The worker is a session and group leader through `/usr/bin/setsid --wait` (`Prepared.cs:50-60`).
  The launcher has no signal handlers (grep, 0 hits): killing it leaves the worker running.
- Elapsed time is wall clock (`LaunchRun.cs:1020`, `:1161`); timestamps mix local time
  (`wrapup.json`, claude `hook.log`) and UTC (opencode `hook.log`).

**Relay and unit runner, as their users see them** (current-state section 5b; verified in the
private companion note):
- `msg-relay send <name>` delivers a text message to a named agent session from any process. There
  is no relay daemon.
- A message to an opencode session starts a model turn at once; only opencode sessions registered
  with the relay are reachable.
- A Claude Code session receives a message at its next tool call or prompt; an idle one is not
  woken.
- Relay names change when a session restarts; a message to the old name fails.
- `sy11a-unit-run <owner> <unit> [--log <file>] -- <cmd>` runs `<cmd>` as a detached
  `systemd-run --user` unit and sends `<owner>` one `UNIT DONE` message at its end, with the result,
  the exit status and the last lines of the log. It has no switch to skip that message.
- The unit does not inherit the session's environment: a launcher started this way sees no
  `CLAUDE_CODE_SESSION_ID`. This shapes section 4.4.
- The operator's rule is that agents never wait inside a turn: a job longer than a few minutes runs
  as such a unit, and the agent ends its turn after the launch.

**What the skill tells the orchestrator.** Claude Code: launch with `run_in_background` and wait for
the notification; opencode: raise the bash tool's `timeout` above the worker's limit
(`skills/lean-worker/SKILL.md:139-146`, verified). It reads only the result block and never
`stream.jsonl` (`SKILL.md:174-198`). The skill does not mention the relay or units.

**A prior private investigation** founded a machine-wide dashboard daemon for the operator's agent
sessions; this design's files are meant to be readable by it (section 8).

## 4. Recommended architecture

### 4.1 Data plane

Two new files per run, both written by the launcher only. Field names are proposals; the values in
the examples are invented.

**`<run-dir>/status.json`: the run's current state, replaced atomically.**

```json
{
  "schema": "lean-worker.status/1",
  "seq": 57,
  "updated_at": "2026-11-22T13:02:31.412Z",
  "run": {
    "id": "20261122-135850-fix-save-w1",
    "dir": "<repo>/.lean-worker/runs/20261122-135850-fix-save-w1",
    "name": "fix-save-w1",
    "chain_id": "20261122-135850-fix-save-w1",
    "round": 1,
    "parent_run": null
  },
  "launcher": { "pid": 41233, "start_ticks": 7432110, "boot_id": "5c2f0a4e-..." },
  "worker": { "pid": 41240, "pgid": 41240, "sid": 41240, "start_ticks": 7432151 },
  "owner": {
    "keys": ["claude-session:0f3e9a12-...", "pid:20480@7401233"],
    "source": "env",
    "runtime": "claude",
    "relay_name": "example-track-1a"
  },
  "spec": {
    "profile": "code-minimax", "runtime": "opencode", "provider": "minimax-coding-plan",
    "model": "MiniMax-M3", "budget_usd": 3.0, "wrap_up_usd": 2.4,
    "timeout_minutes": 60, "started_at": "2026-11-22T12:58:50.104Z",
    "deadline_at": "2026-11-22T13:58:50.104Z", "worktree": null
  },
  "phase": "worker",
  "live": {
    "api_calls": 12, "turns": null,
    "tokens": { "input": 3100, "cache_write": 0, "cache_read": 399000, "output": 9870, "thinking": 3200 },
    "cost_usd": 0.4123,
    "wrapped_up": false,
    "elapsed_ms": 221304,
    "last_event_at": "2026-11-22T13:02:27.090Z",
    "last_tool": { "name": "bash", "at": "2026-11-22T13:02:27.090Z", "state": "completed" }
  },
  "end": null
}
```

The `dir` field holds the absolute path; `<repo>` stands for it here.

- `phase` is one of `starting` (run dir made, worker not yet spawned), `worker`, `gate` (with
  `gate_round` in `live`), `finishing` (worktree decision, stats, summary), `done`, `failed`
  (the launcher caught an exception and is about to print `LEAN-WORKER LAUNCH FAILED`). `wrapped_up`
  is a flag inside `worker`, because the worker keeps running after wrap-up.
- `end` is null until `done` or `failed`, then for example
  `{"status": "success", "exit_code": 0, "at": "...", "summary": "summary.json"}`. Its `status`
  takes the ledger's vocabulary (`success`, `wrapped-up`, `error`, `crashed`, `no-result`,
  `timed-out`, `budget-exceeded`, `truncated`, `out-of-scope`; `LaunchRun.cs:1991-2044`) plus two
  **new** failure statuses that the ledger does not have: `launch-failed` (the catch block; with
  `error`, and with `after_summary: true` and `summary_status` when the summary was already
  written) and `launcher-signalled` (slice 5). The reader adds states of its own that are never
  written to a file: `ending`, `orphaned`, `lost`, `gone`, `unknown`, `unreadable`, `legacy`,
  `shared-dir`.
- `start_ticks` is field 22 of `/proc/<pid>/stat` (clock ticks since boot); `boot_id` is
  `/proc/sys/kernel/random/boot_id`. Claude Code's own per-session files use the same pair against
  pid reuse (observed, current-state verification log). Both are plain file reads, inside the
  host-API rule.
- `worker.pid` is `p.Id`, which is also the group id because `setsid` without a fork makes the
  worker the group leader; `ProcessGroup.ResolveAsync` checks that `pgrp == pid` (inference from
  `Prepared.cs:50-60`, `ProcessGroup.cs:93-113`). `setsid` also makes it a session leader, so its
  session id `sid` equals its pid and is recorded too; `start_ticks` is the worker's own start time.
  Without setsid, `pgid` and `sid` are null and the reader never reports `orphaned` for the run.
- `api_calls` counts model calls for both runtimes, deduplicated by id as the ledger's `api_calls`
  is: one per `step_finish` for opencode, one per distinct `message_start` id for claude. It only
  grows. `turns` is the runtime's own turn count where it exists: opencode's steps
  (`Outcome.Turns`) live, claude's `result.num_turns` only at the end (null before). The table and
  the one-shot show **CALLS** (`api_calls`) for every run, so no number drops when a claude run
  completes; `turns` is in `--json` and in the ended row's detail.
- `cost_usd` is the Meter's own token-priced spend (`Meter.Spent`), not the worker's reported cost;
  it lags one step (claude finalises output at `message_delta`, opencode at `step_finish`).
- `last_tool`: claude's `tool_use` blocks appear in the `assistant` event when the call starts, so
  the table shows the running tool; opencode 1.x emits `tool_use` only on completion or error
  (survey B2), so for opencode it is the last finished tool and a long tool call shows as quiet.
- All new timestamps are UTC with `Z`. `elapsed_ms` comes from a `Stopwatch` started with the run,
  so it is monotonic; `started_at` stays wall clock for display.

**Write discipline.**
- Every write builds the whole object and goes through the existing temp-plus-rename helper
  (`Launcher.WritePlainAsync`, `Launcher.cs:230-243`, or a synchronous twin for the stream thread).
  A reader sees the old or the new file, never a torn one.
- One lock serialises every status write: the pump thread (stream events) and the main thread (phase
  changes) both take it, build the object from the current state, assign `seq` (one more than the
  last) and replace the file, all inside it. So no older object is ever published after a newer one,
  and `seq` is a total order for a reader.
- When: at each phase change, at wrap-up, and after each stream line that changes a displayed field
  (a finished turn, a tool start or finish, an error or retry event). Deltas that change nothing
  shown cause no write. The measured rate of kept lines in recent runs is at most about 1.3 per
  second, and the qualifying events are far fewer (current-state, verification log), so there is no
  throttle and no timer. If a later runtime streams faster, a one-second coalescing floor is the
  fallback. Slice 1: a line that changes nothing shown still rewrites the file when the last write
  is 30 seconds old, so `last_event_at` stays current; still no timer, the stream lines drive it.
- No separate heartbeat. Liveness is proven by pid plus start time (below), not by a fresh mtime;
  `updated_at` and `last_event_at` are shown as ages only.
- Best effort: an `IOException` or `UnauthorizedAccessException` on a status write never fails the
  run. The first failure adds one note to the summary's `notes`; later writes are still tried.
- Written in the run dir only, which the gate's trust set skips (`LaunchRun.cs:1508-1516`) and the
  invoking-repo snapshot excludes (`LaunchRun.cs:1365-1370`), both verified: the new file changes no
  gate or scope verdict.
- The first write happens right after the run dir is created (`PrepareTaskAsync`). Launches that
  fail before that (option and trust-snapshot errors, exit 2 within seconds) leave no record, as
  today.
- Order at the end: `summary.json`, `report.md` and the ledger row first, then `status.json` with
  `phase: done`, then the result block on stdout and the exit. So a terminal status file always has
  its summary next to it. If anything after the summary throws (the ledger append, the print), the
  catch block writes `phase: failed` with `end.status: launch-failed`, `after_summary: true` and the
  summary's status, and the launcher exits 2 as today.

**`$XDG_STATE_HOME/lean-worker/runs/<stamp>-<launcher-pid>-r<round>.json`: the per-user index.**

```json
{
  "schema": "lean-worker.pointer/1",
  "created_at": "2026-11-22T12:58:50.131Z",
  "run_dir": "<repo>/.lean-worker/runs/20261122-135850-fix-save-w1",
  "chain_id": "20261122-135850-fix-save-w1",
  "parent_run": null,
  "owner_keys": ["claude-session:0f3e9a12-...", "pid:20480@7401233"],
  "launcher": { "pid": 41233, "start_ticks": 7432110, "boot_id": "5c2f0a4e-..." }
}
```

- Written once, atomically, right after the first `status.json`; never rewritten. The directory is
  made with mode 0700 (`XDG_STATE_HOME` defaults to `~/.local/state`).
- File per run, not a shared list: no lock, no append race (the ledger's bare append is already a
  known risk, `LaunchRun.cs:2528`), no whole-table rewrite that could drop another writer's row
  (agent-deck #1550).
- A pointer instead of a scan: the runs roots are many (one per repository that uses the skill, plus
  worktrees and any `--runs-root`), and a scan cannot find a run under a root it was not told about.
  A scan of the current repo's runs root stays available as `status --here`.
- The owner keys sit in the pointer so `status` filters without opening every run dir.

**What the status files are trusted for.** The worker runs as the same user as the launcher and can
write the run dir and the per-user index alike, so nothing in these files is a security boundary.
They are trusted for two things only: **display** (the table) and **waking** (when `--wait`
returns). They are never used for a gate verdict, a scope verdict, the ledger row, `summary.json`,
the launcher's exit code or the result block; the launcher computes all of those from its own memory
as it does today. One rule decides when a run counts as ended, for the table and for `--wait` alike:

> A run is **ended** when the launcher identity recorded at launch is provably gone (step 5 below:
> a clear mismatch, not a failed probe). Its final state is then the status file's `end` if it has
> one, else the status in `summary.json` if that file exists, else `lost` or `orphaned` (step 5). A
> terminal phase written while that launcher is still alive is shown as `ending` and is not final.

Why `end` before `summary.json`: `end` is the launcher's last word and matches what the caller saw
(its stdout and exit code). In the one case where they differ, a failure after a successful summary
(the ledger append threw), the launcher printed `LEAN-WORKER LAUNCH FAILED` and exited 2, so the
row shows `failed (after summary: success)` and `--wait` counts it as failed. `summary.json` decides
only when the launcher died between the summary and its last status write.

The rule makes a premature or stray terminal write harmless (an honest launcher writes `done` a
moment before it prints and exits; the reader waits for the exit). It does not stop a hostile
worker, which could rewrite both files; that is the same exposure `summary.json` has today.

**What a reader does, per pointer.**
1. Read the pointer; on a parse or I/O error, show an `unreadable` row and move on (never delete).
   Two pointers that name the same `run_dir` (the same-second collision, section 5) make both rows
   `shared-dir`: they are shown, never trusted, and never end a `--wait` as `success`.
2. Read `<run_dir>/status.json`; on a parse error, retry once (a rename cannot tear it, but a
   foreign writer could). Missing file: the run dir was removed; show `gone`.
3. Prove liveness of the launcher from the pointer's identity: same `boot_id` as now,
   `/proc/<pid>/stat` exists, its field 22 equals `start_ticks`, and its state is not `Z` or `X`.
4. All true: the run is live. Its phase is the status file's, or `ending` when that phase is `done`
   or `failed`.
5. Provably gone (boot id differs, no such pid, or other start ticks): the run is ended. Final state
   as the rule above says. When neither an `end` nor `summary.json` exists, decide between `lost`
   and `orphaned`:
   - boot id differs: `lost`, with no process scan (numbers from another boot mean nothing);
   - same boot, and `/proc/<worker.pgid>/stat` exists with field 22 equal to `worker.start_ticks`:
     the worker itself is alive, `orphaned`;
   - same boot, and that pid exists with other start ticks: the number was recycled, `lost`;
   - same boot, the leader gone, and some `/proc/*/stat` has pgrp equal to `worker.pgid` and session
     equal to `worker.sid`: `orphaned` (members the worker left behind). Linux does not hand out a
     pid number while it is still the id of a live process group or session (inference from the
     kernel's pid allocation; not tested here), so such members can only be the worker's;
   - otherwise `lost`.
   An `orphaned` row shows the group id together with the worker's start time and never suggests a
   kill command: anything that acts on the group must re-check the same identity at that moment.
   An ended row is cached and not re-read.
6. A probe that fails for another reason (permission, an unexpected `/proc` format) gives
   `unknown`, never `lost`, and the run is not ended.
7. Quietness is an annotation, never a state: a live run with `last_event_at` older than 10 minutes
   shows `quiet 41m` with a `!`. This is the #60 case: the launcher is alive, the worker is alive,
   and nothing has come for a long time. The design shows it; it does not decide it. Slice 1
   annotates quiet only in the `worker` phase, since no stream lines arrive during a gate round,
   and a zombie group leader does not count as alive (the group's members are looked for).

**Reaping.** Any `status` call may delete a pointer, one file at a time, only on proof: the run is
ended and ended more than 7 days ago, or it is `lost` and the pointer is more than 7 days old. An
`orphaned`, `unknown`, `ending` or `unreadable` row is never reaped. `ENOENT` on delete is success
(another reader got there first). Run dirs are never touched.

**Legacy runs and crashes.**
- A run dir without `status.json` (an older launcher) has no pointer, so it never appears in an
  owner-scoped table. `status --here` and `status --run <dir>` show it as a directory-only `legacy`
  row: its name, its stamp, whether `summary.json` exists (and then its status) and the age of
  `stream.jsonl`; liveness `unknown`, because no pid was recorded. This departs from the starting
  position, which wanted the fallback to judge liveness by pid: for a legacy run there is no pid to
  judge. Re-reading the counters from `stream.jsonl` through the runtime parsers and the Meter (as
  `cost --claude <file>` already re-prices a stream) is a later, optional slice (slice 7).
- A run whose launcher crashed after this change keeps its last `status.json`: the counters are
  current to the last turn, and step 5 gives `orphaned` or `lost`. No re-parse is needed.
- Survives a crashed launcher: the run dir, `stream.jsonl`, the last `status.json`, the pointer.
  Lost: the summary, the report, the ledger row, the group cleanup, the opencode scratch config
  home in `/tmp` (as today).

**Considered and not chosen.**
- *A per-run `status.jsonl` appended on every step.* It duplicates `stream.jsonl` (the full event
  history) and the ledger (the final row). The table needs the current state only. A reader that
  wants a time series can sample `status.json` or re-derive from the stream. Dropped.
- *A separate immutable start record* (`run.json`) next to the live record. One file with a stable
  `spec`/`launcher`/`owner` part and a changing `live` part is one write path and one read; the
  immutable copy that matters for discovery is the pointer. Merged.
- *Re-parsing `stream.jsonl` on every refresh.* It is the only option for legacy runs, but it costs
  a full read per run per tick, needs the price book in the reader, and cannot give liveness.

### 4.2 CLI surface

One new subcommand, dispatched in `Program.cs` like `stats`, parsed with `Commands.Flags`, and
implemented in new classes (`RunStatus` for the writer side, `StatusCommand`, `StatusTable`) rather
than in `LaunchRun` or `Commands`, which already carry coupling and complexity debt
(`architecture.yaml:21-57`). All flags are proposals.

```
lean-worker status [scope] [mode] [options]
  scope (one of; default: the calling session's children)
    --owner <key>        runs whose owner keys include <key>
    --here               runs under this repository's runs root (scan; includes legacy run dirs)
    --all                every run in the per-user index
    --run <dir>          one run
  mode (one of; default: one-shot plain text)
    --json               one-shot JSON
    --watch              redraw a table in place until q or Ctrl+C (TTY); a line per change otherwise
    --wait               block until every run in scope has ended (scope fixed at start)
    --whoami             print the caller's owner keys; with --key, the one key to pass to --owner
  options
    --since <dur>        also list runs that ended within <dur> (default: all of the owner's; 24h for --here/--all)
    --interval <s>       refresh period for --watch and --wait (default 2, minimum 1)
    --timeout <dur>      --wait gives up after <dur> (default 120m)
    --grace <dur>        --wait stops waiting on unknown or unreadable rows after <dur> (default 10m)
    --started-after <t>  --wait (and the listing) take only runs started at or after <t>
    --expect <n>         --wait first waits (up to --grace) until <n> runs are in scope, then fixes it
    --any                --wait returns when the first run ends
    --bell               --watch rings the terminal bell when a run ends
```

**How "the calling session" is found** (section 4.4): the same resolution the launcher uses at
launch. If nothing identifies the caller (a terminal pane, a cron job), `status` without a scope
exits 2 with "cannot tell which session you are; pass --owner, --here or --all". `status --whoami`
prints the keys that resolution finds, one per line; `status --whoami --key` prints the single key
to put on a launch's `--owner` (the session id when there is one, else the `pid:<pid>@<ticks>` key),
so nobody builds a key by hand.

**Which runs root `--here` reads.** The read-only commands today use `--runs-root`, else
`.lean-worker` beside the caller (`Commands.cs:39-41`), while the launcher re-anchors a relative
runs root on the task file's git root when `<git-root>/.lean-worker` exists (`LaunchRun.cs:600-617`).
`--here` follows the launcher: `--runs-root` if given, else `<git root of the current directory>/
.lean-worker` if it exists, else `.lean-worker` beside the caller. So from a subdirectory of a
checkout it reads the same root the launcher wrote to. A run whose task file lived in another
repository is recorded under that repository and is not listed by `--here`; the owner scope and
`--all` still find it through the index.

**How `--wait` bounds itself.**
- **Scope fixed at start.** `--wait` takes the runs that match its scope and have not ended at the
  moment it starts, and waits only for those. Runs launched afterwards are not joined; the first
  line of its output says so: `waiting on 3 runs matched at 13:02:31Z; runs launched later are not
  included`. Without `--expect`, a waiter started before its runs' pointers exist would wait on
  nothing; so a batch recipe records the time before it launches and passes `--started-after <t>
  --expect <n>`: the waiter takes runs started at or after `<t>` as they appear until there are
  `<n>`, then fixes the scope. If fewer than `<n>` appear within the grace, it returns 8 and names
  the shortfall.
- **A finite default.** `--timeout` defaults to 120 minutes (the launcher's default worker limit is
  60, `Options.cs:102`, plus gate rounds); exit 7 when it passes.
- **Rows that cannot be resolved.** An `unknown` or `unreadable` row (a failed probe, a pointer that
  does not parse) stops holding the wait after `--grace` (default 10 minutes) of staying that way.
  When every other run in scope has ended and such rows remain, `--wait` returns 8 and the listing
  names them. A `gone` row (run dir removed) counts as ended, not `success`.

**Exit codes** (proposal; 0/1/2/3 match the launcher's meanings, `LaunchRun.cs:282-285`,
`Program.cs:41-42`):

| code | one-shot, `--watch` | `--wait` |
|---|---|---|
| 0 | listed (even when some runs failed) | every run in scope ended, each with final state `success` (gate chains: clean) |
| 1 | n/a | at least one run ended not `success`, or ended `orphaned` or `lost` |
| 2 | usage error, or no caller identity | usage error, or no caller identity |
| 3 | n/a | none failed, at least one `wrapped-up` |
| 6 | n/a | no run matched the scope (never a silent 0: a wrong owner must not read as "all done") |
| 7 | n/a | `--timeout` (default 120m) passed with runs still live |
| 8 | n/a | the rest ended, but rows stayed `unknown` or `unreadable` past `--grace`, or fewer than `--expect` runs appeared; the listing names them |

**What `--wait` exit 0 means.** Every run in its fixed scope has a launcher that has exited, and
each run's final state (the ended rule in 4.1) is `success`. It is a wake-up signal: it may be
taken to mean "now read the results". It may not be taken as verification: the orchestrator still
reads each run's result block or `report.md` and checks the done-criterion with its own command, as
`SKILL.md:174-198` already requires. A worker that forged the files could produce a false 0; the
check that follows the wake is what catches it.

`--wait` prints the one-shot listing when it returns, so the caller's notification carries the
summary.

**One-shot output** (the orchestrator's next tool call; plain text, no escapes, one line per run,
sorted attention first, then live, then ended; names are never cut; all names invented):

```
lean-worker status: owner cc:0f3e9a12 (example-track-1a), 4 live, 1 ended, 1 needs attention
orphaned  inventory-caches-w4         oc glm-5.3         95 calls  $1.10/$3.00  20m00s  launcher gone, worker group 41290 (started 12:41:07) alive
running   tests-inventory-retries-t1  cc sonnet           9 calls  $0.62/$3.00  47m10s  quiet 41m, last Bash
wrap-up   gate-sweep-hardening-w2     cc sonnet          31 calls  $2.43/$3.00  18m02s  last Edit 1m12s ago
gate r2/3 inventory-steps3-w2         oc glm-5.3-flash  117 calls  $0.97/$3.00  12m40s  gate round 2 running
running   fix-save-w1                 oc MiniMax-M3      12 calls  $0.41/$3.00   3m41s  last bash 4s ago
success   docs-install-w1             oc MiniMax-M3      84 calls  $0.55/$3.00   5m59s  .lean-worker/runs/20261122-135101-docs-install-w1
```

CALLS is `api_calls` for both runtimes (4.1); it only grows. It is meant to be cheap in the
orchestrator's context: one header and one line per run, 100 to 150
characters each. `--json` prints `{"schema": "lean-worker.status-list/1", "generated_at": ...,
"caller": {...}, "runs": [...]}` where each run is its `status.json` plus the reader's derived
`state` and `liveness` fields.

**The `--watch` table at 80 columns:**

```
owner cc:0f3e9a12 (example-track-1a)   live 4  ended 1  attention 1   14:02:31
STATE        NAME               RT  CALLS     COST  BUDG  ELAPSED  LAST
orphaned     inventory-caches~  oc     95    $1.10   37%   20m00s  read 19m
running      tests-inventory-~  cc      9    $0.62   21%   47m10s  quiet 41m !
running      fix-save-w1        oc     12    $0.41   14%    3m41s  bash 4s
wrap-up      gate-sweep-harde~  cc     31    $2.43   81%   18m02s  edit 1m12s
gate r2/3    inventory-steps3~  oc    117    $0.97   32%   12m40s  gate 40s
success      docs-install-w1    oc     84    $0.55   18%    5m59s  -
```

**At 120 columns:**

```
owner cc:0f3e9a12 (example-track-1a)   live 4  ended 1  attention 1   14:02:31
STATE        NAME                          RT  MODEL          CALLS       IN/OUT     COST  BUDG  ELAPSED  LAST
orphaned     inventory-caches-w4           oc  glm-5.3           95     2.8M/60k    $1.10   37%   20m00s  read 19m
running      tests-inventory-retries-t1    cc  sonnet             9     610k/12k    $0.62   21%   47m10s  quiet 41m !
running      fix-save-w1                   oc  MiniMax-M3        12    402k/9.9k    $0.41   14%    3m41s  bash 4s
wrap-up      gate-sweep-hardening-w2       cc  sonnet            31     1.9M/41k    $2.43   81%   18m02s  edit 1m12s
gate r2/3    inventory-steps3-w2           oc  glm-5.3-flash    117     3.1M/88k    $0.97   32%   12m40s  gate 40s
success      docs-install-w1               oc  MiniMax-M3        84     2.2M/51k    $0.55   18%    5m59s  -
```

(The examples were rendered by a script that applies the rules below; the longest line is 78 and
117 characters.) `IN/OUT` is input plus cache tokens over output tokens. A gate chain is one row,
showing the latest round. Nested runs (section 4.4) are indented under their parent run.

**Columns and the dropping order.** STATE (11), NAME (16 to 28), CALLS (5), COST (7) and ELAPSED (7)
are never dropped. Columns are separated by two spaces. When the terminal is too narrow, columns go
in this order: OWNER (shown only with `--here`/`--all`), PROFILE, IN/OUT, MODEL, RT, BUDG, LAST. NAME
takes what is left, at most 28, and is cut with a trailing `~` (ASCII, so no width ambiguity), never
below 16. PROFILE fits from 121 columns. Below 54 columns (the five fixed columns) the frame shows
only `terminal too narrow (54 columns needed)`, as btop does.

**Order and acknowledgement.** Rows needing attention (`orphaned`, `lost`, `unknown`, failed ends,
quiet runs) first, then live runs by start time, then ended runs, newest first. A run that ended
while the table was open is shown in bold until `a` (acknowledge all) is pressed; `--bell` rings
once per such transition. That is the operator's completion signal in the pane (cmux and workmux
keep "done" until seen; survey).

### 4.3 Rendering

Hand-rolled ANSI on `System.Console`, no package. Spectre.Console or Terminal.Gui would break the
host-APIs-only rule (`architecture.yaml:2-5`, `:14-17`); loosening a gate is the operator's call
(section 7). The table needs no widget: fixed columns, no input beyond `q` and `a`. Every API named
here exists in .NET 8, the launcher's target framework (`LeanWorker.csproj:6`); the SDK this
repository pins (11.0.100, `global.json:2-4`) builds it but adds nothing the design relies on.

**TTY mode** (`!Console.IsOutputRedirected`, and stdin a TTY for keys):
- Start: `ESC[?1049h` (alternate screen), `ESC[?25l` (hide the cursor).
- Each frame, built in one `StringBuilder` and written with one `Console.Out.Write` plus a flush:
  `ESC[?2026h` (begin synchronized output; terminals that do not know it ignore it), `ESC[H`
  (home), each row followed by `ESC[K` (erase to end of line) and `\r\n`, then `ESC[J` (erase
  below), then `ESC[?2026l`. No full-screen clear per frame; every cell is cut to its width, so
  no line wraps.
- Colour only for STATE, and only when `NO_COLOR` is unset: `ESC[31m` failed, lost and orphaned,
  `ESC[33m` wrap-up, ending and quiet, `ESC[32m` success, `ESC[1m` for unacknowledged ends, `ESC[0m`
  after each.
- Width and height from `Console.WindowWidth`/`WindowHeight`, read every frame (btop's backstop);
  `PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ...)` only triggers an immediate redraw
  (the API is documented for .NET 6 and later per the survey's link; not tried here). Rows beyond the
  height are cut, with a last line `... 7 more (status --all for the full list)`.
- Exit on `q`, Ctrl+C, SIGTERM or SIGHUP: in a `finally` and in the signal handlers, write
  `ESC[0m`, `ESC[?25h`, `ESC[?1049l`. Keys are read with `Console.KeyAvailable` and
  `Console.ReadKey(intercept: true)` between frames, so no raw-mode code is needed.

**Non-TTY mode.** `--watch` with redirected output never writes an escape. It prints one
line per transition:

```
2026-11-22T13:02:31Z fix-save-w1 running -> success 12 calls $0.41 3m41s
2026-11-22T13:04:10Z inventory-caches-w4 running -> orphaned (launcher gone, group 41290 started 12:41:07 alive)
```

That is the shape the Claude Code Monitor tool turns into events, one per line (survey B1; its
watches end after 5 minutes by default and 30 at most, so it suits a short batch, not a 60-minute
run).

**Refresh.** Every 2 s by default, minimum 1 s (k9s and btop both default to 2 s and k9s enforces a
minimum; survey). A refresh lists the pointer directory, reads each non-ended `status.json` and its
`/proc/<pid>/stat`; ended rows are cached. If a refresh is still running when the next is due, the
next is skipped (k9s drops overlapping refreshes).

**Cost of `dotnet run`.** The skill runs the launcher as `dotnet run --project ... -c Release`
(`SKILL.md:152`), which checks the build on every start. That is fine for `--watch` and `--wait`
(one start) and acceptable for a one-shot call; the start-up time was not measured here.

### 4.4 Ownership: "children of this session"

**Captured at launch, in this order** (the first that applies sets `owner.source`; all keys found
are recorded):
1. `--owner <key>` on the launch command line (proposal). Needed for detached units: a launcher
   started by `sy11a-unit-run` or `systemd-run` is not a descendant of the session and does not
   inherit its environment (section 3). The session's own shell expands the argument before the
   unit starts, so `--owner "$(lw status --whoami --key)"` (section 4.2; `lw` is shorthand defined in
   4.5) works with no change to the unit runner, under either runtime. Under Claude Code that key is
   `claude-session:$CLAUDE_CODE_SESSION_ID`.
2. `LEAN_WORKER_OWNER` in the environment (proposal), for wrappers that cannot edit the command line.
3. `LEAN_WORKER_RUN_ID` in the environment (proposal): this launcher runs inside another lean-worker
   run. The owner is that run (`parent_run`), and the session keys found in the environment are
   recorded but not used for matching, because they are inherited: an opencode 1.x worker passes the
   top-level session's `CLAUDE_CODE_SESSION_ID` straight through to its shell (the launcher passes
   its whole environment, `WorkerProcess.cs:167-181`; inference, not run). Today only opencode
   workers get `LEAN_WORKER_RUN_DIR` (`OpencodeRuntime.cs:232`, verified); the claude runtime sets
   neither, so the new variable must be added to both runtimes' environments.
4. Claude Code: `CLAUDE_CODE_SESSION_ID` (key `claude-session:<id>`) and `CLAUDE_PID` with that
   pid's start ticks (key `pid:<pid>@<ticks>`). Both documented (survey B1). The session id changes
   on `/clear`; the pid key keeps a cleared session the owner of its earlier runs.
5. opencode 2.x: `OPENCODE_SESSION_ID` (key `opencode-session:<id>`). opencode 1.x: only
   `OPENCODE_PID` (key `pid:<pid>@<ticks>`, the ticks being field 22 of `/proc/$OPENCODE_PID/stat`,
   counted after the closing `)` of the command name, which may itself contain spaces; this is why
   `status --whoami --key` builds the key rather than a shell one-liner). The 1.18.35 installed on
   the operator's machine is 1.x (verified version; the variable names are from the survey's source
   reading).
6. Relay name, display only: `MSG_RELAY_NAME`, else the nearest Claude Code session found by walking
   parent processes, the same way `msg-relay` names a sender (section 3). Never a match key: names
   change on restart and can collide.
7. Nothing found: the run is unowned, visible with `--here`, `--all` and `--run`.

**At read time** the caller computes its own keys the same way (steps 4 to 6) and matches any run
whose `owner_keys` intersect them, plus, transitively, runs whose `parent_run` is one of those
runs. The relay name is re-resolved for the header (a session restarted under a new name shows its
new name).

**What this does not solve.** A Claude Code session restarted from a handoff is a new session with a
new id and a new pid (inference: a fresh start cannot keep either). It does not own its
predecessor's runs. The design's answer is `status --here`: the new session's first step lists every
run of its repo, live or recently ended, from the files, which is the one record that survives the
restart (section 4.5). A stable track identity across restarts would need a name that the restarting
supervisor carries; that is an open question for the operator (section 7).

**A worker inside a worker.** Run B launched from inside run A's worker records `parent_run = A`;
the top session's table shows B indented under A. A claude worker's own Bash children get the
worker's `CLAUDE_CODE_SESSION_ID` (documented for Bash subprocesses; whether `claude -p` sets it the
same way is unverified), which also makes A's worker the owner; `parent_run` gives the same answer
without relying on it.

### 4.5 Wake-up and completion

**Rule (kept from the starting position):** status is pulled from the files; the relay carries
completion only, at most one message per launch, never progress. A message to an opencode session
starts a paid model turn, and a message to an idle Claude Code session is not seen until its next
prompt or tool call (section 3). Progress ticks over the relay would cost a turn each and still not
reach an idle Claude Code session.

**The launcher itself sends no relay message.** Today's completion routes already exist and each
sends exactly one signal; a launcher-side send would double them. The table does not replace them;
it complements them by showing what happens between launch and end, and it is the record a
restarted session reads.

Per parent runtime and route (the private companion note has the same table with the relay
tools' internals). In the recipes, `lw` stands for `dotnet run --project "<skill-dir>/launcher" -c
Release --`, `<key>` for `"$(lw status --whoami --key)"` expanded by the orchestrator's shell, and
`<t0>` for a time recorded with `date -u +%FT%TZ` just before the batch is launched.

| Parent | Route | What wakes it | Cost | Survives the parent's restart |
|---|---|---|---|---|
| Claude Code, busy or idle | today's skill: the launcher in a `run_in_background` Bash call | the harness's completion notification when the launcher exits; in an idle interactive session it started a new turn in the one observation available (see the notes) | one turn per run | no: the background task belongs to the session (inference); the files and the worker do |
| Claude Code, busy | `sy11a-unit-run <me> <unit> --log <f> -- <launch> --owner <key>` | `UNIT DONE`, seen at the next tool call | none extra | the unit does; the message goes to the old name and fails |
| Claude Code, idle | the same unit route | not woken by the relay | n/a | as above |
| Claude Code, idle, batch | units launched without a message (below), each with `--owner <key>`, then one `lw status --wait --owner <key> --started-after <t0> --expect <n> --timeout 25m [--any]` in a `run_in_background` call | the harness notification when the waiter returns: one turn per batch (or per run with `--any`) | one turn per batch, plus one per timeout re-arm | the units do; the waiter does not, and is re-armed by the new session after `status --here` |
| opencode TUI, not registered with the relay | today's skill: a blocking bash call with a raised `timeout` | the bash call returns | the turn is held, no tokens spent while waiting | no |
| opencode, registered with the relay | `sy11a-unit-run <me> <unit> --log <f> -- <launch> --owner <key>` per run | the message is a prompt: a turn at once | one turn per run | the unit does; the message needs the registration to be current |
| opencode, registered, batch | units without a message, each with `--owner <key>`, then one `sy11a-unit-run <me> <wait-unit> --log <f> -- lw status --wait --owner <key> --started-after <t0> --expect <n> --timeout 120m` | one `UNIT DONE` when the waiter ends, its log tail being the `--wait` listing | one turn per batch | as above |
| opencode 2.x TUI | a background shell running `lw status --wait --owner <key> --started-after <t0> --expect <n> --timeout 120m` | the shell's completion notice ("you will be notified when it completes", survey B2) | one turn per batch | unverified |

Notes on the table:
- **The idle-wake evidence.** Observed in the authoring session of this design on 2026-11-22 (Claude
  Code 2.1.330, an interactive session): after its turn had ended, the completion of background tasks
  re-invoked the session with a task notification, several times. That is one observation of an
  interactive, attended session; it was not tried in an unattended or `-p` session (section 11).
  Known risks from the survey (B1): notifications not delivered when several agents complete at once
  (#20754, closed) and notifications leaking across `/clear` (#110924, open). A lost notification
  leaves the files as the fallback: `status` at the next prompt shows what ended.
- **Every waiter has an explicit `--timeout`.** A user unit has no runtime limit of its own, so the
  opencode waiter unit gets `--timeout 120m`; the Claude waiter gets `25m` (next note). On exit 7
  or 8 the owner reads the listing and re-arms or investigates.
- **The `--wait` waiter in Claude Code and the rule that agents never wait inside a turn.** A
  `run_in_background` call ends the turn at once; nothing waits inside a turn. What the rule adds is
  durability: the waiter dies with the session, the unit does not. So the waiter wakes, the unit
  carries the work, and the files carry the record. Background commands have time limits in
  unattended and `-p` sessions (30 minutes by default, 2 hours at most; survey B1), hence the
  `--timeout 25m` and exit code 7: a timeout is itself a wake, and the session re-arms. Whether the
  operator's long-running coordinator sessions count as unattended was not verified.
- **"Units without a message."** `sy11a-unit-run` always sends its `UNIT DONE` message. A batch
  either accepts one wake per run, or launches its workers with plain `systemd-run --user --collect`
  and lets the single waiter unit wake the owner. The second bypasses `sy11a-unit-run`, which the
  operator's rule names as the launcher of long jobs; a switch in `sy11a-unit-run` to skip the
  message would keep one launcher (section 7).
- **After a restart** the only signal that survives is the files. The skill should tell an
  orchestrator to run `status --here` at the start of a session, before launching new work.
- **The operator** is not woken by the relay at all; the operator watches the `--watch` pane, where
  ended runs stay bold until acknowledged and `--bell` rings.

**The terminal pane.** The starting position had the pane opened by the operator's existing session
opener. That tool opens agent sessions only, with no mode for an arbitrary command (verified in the
private companion note). A pane is one generic line instead (Konsole as the example terminal):

```
systemd-run --user --collect --unit "lw-status-$(date +%H%M%S)" --setenv=DISPLAY="${DISPLAY:-:0}" \
  konsole --workdir "$PWD" -e dotnet run --project "<skill-dir>/launcher" -c Release -- \
  status --watch --owner "claude-session:$CLAUDE_CODE_SESSION_ID" --bell
```

On a Wayland session the terminal also needs `WAYLAND_DISPLAY` and `XDG_RUNTIME_DIR`; the line above
passes only what it names. A more robust form passes each variable only when the caller has it:

```
systemd-run --user --collect --unit "lw-status-$(date +%H%M%S)" \
  ${DISPLAY:+--setenv=DISPLAY="$DISPLAY"} ${WAYLAND_DISPLAY:+--setenv=WAYLAND_DISPLAY="$WAYLAND_DISPLAY"} \
  ${XDG_RUNTIME_DIR:+--setenv=XDG_RUNTIME_DIR="$XDG_RUNTIME_DIR"} \
  konsole --workdir "$PWD" -e dotnet run --project "<skill-dir>/launcher" -c Release -- \
  status --watch --owner "$(dotnet run --project "<skill-dir>/launcher" -c Release -- status --whoami --key)" --bell
```

Neither form was tried; on Wayland it is unverified (section 11). The pane is not a descendant of the
session, so `--owner` is required. It is `status --watch`, not
a third implementation.

### 4.6 The daemon question

**For a daemon:** one reaper instead of every reader; push instead of 2 s polling; a cached
machine-wide view; the place for desktop notifications and history.

**Against a daemon in lean-worker:**
- The data is durable files written by the process that knows the facts. A reader is stateless and
  cheap: a directory listing and a few small reads per refresh.
- Supervision already has a daemon: systemd runs the units, and the harness runs background tasks.
  The wake routes ride on both.
- A daemon costs install steps, a unit, a socket and its security (ownership of the socket file, the
  runtime directory, a token). The operator's private dashboard daemon spent a good part of its
  pre-install hardening on exactly that (verified in the private companion note).
- A daemon that is down must not make the basic flow fail, so the file path has to exist anyway.

**Conclusion:** no daemon in lean-worker. A machine-wide, push-based view with history and desktop
notifications is what the operator's private dashboard daemon is for; when it runs, it can read
`status.json` and the pointer index instead of guessing from run dir names (section 8).

## 5. Failure modes

| Mode | How it shows today | How the design detects it | What the user sees |
|---|---|---|---|
| Launcher killed (SIGKILL, OOM) | `stream.jsonl` without `summary.json`; looks like a running run; no ledger row | pointer plus `status.json`; launcher pid gone or start ticks differ; then the group check of step 5 (same boot, leader with the recorded start ticks, or members with the recorded group and session) | `orphaned` with the group id and the worker's start time while the group lives, then `lost`; no kill hint; `--wait` returns 1 |
| Worker group id recycled, or a reboot | n/a | boot id differs: `lost` with no scan; a leader pid with other start ticks: `lost` | never a false `orphaned` that names an unrelated group |
| Launcher throws | `LEAN-WORKER LAUNCH FAILED` on stdout, exit 2, no summary | the catch block writes `phase: failed`, `end.status: launch-failed`; final once the launcher has exited | `ending`, then `failed` with the message |
| Launcher throws after a successful summary (ledger append) | `summary.json` says success, stdout says `LAUNCH FAILED`, exit 2 | `end` (`launch-failed`, `after_summary: true`) wins over `summary.json` (4.1) | `failed (after summary: success)`; `--wait` returns 1 |
| Launcher gets SIGTERM or SIGHUP | dies at once; the worker keeps running | without a handler: as SIGKILL. With slice 5: the handler records `phase: failed`, `end.status: launcher-signalled`, then lets the default exit proceed | `failed (signalled)`, or `orphaned` while the group lives |
| Worker hung on an API step (#60) | nothing until the timeout, which did not fire in #60 | launcher and worker alive; `last_event_at` old | `running` with `quiet 47m !`; never declared dead by the table |
| Claude alive after `result` (the vibe-kanban #2495 shape) | the launcher waits for exit; the timeout ends it | `end` is only written by the launcher after the worker exits; liveness by pid | `running`, `quiet`; then the launcher's own status |
| Worker exits, launcher still in the gate or summary | no file shows it | `phase: gate` with the round, `phase: finishing` | `gate r2/3`, `finishing` |
| Terminal phase written early, or a stray `done` | n/a | not final while the recorded launcher is alive (the rule in 4.1) | `ending` for a moment, then the final state |
| Pid reused after the launcher died | n/a (no pid recorded) | start ticks differ | `lost`, not a false `running` |
| Reboot | stale run dirs look running | `boot_id` differs | `lost` |
| Old launcher (no `status.json`) | n/a | no pointer; `--here` finds the dir | a directory-only `legacy` row, liveness `unknown` (counters from the stream only with slice 7) |
| Stale stream files of long-dead runs | indistinguishable from live ones | not in the index; `--here` shows them as `legacy` only inside `--since` | hidden by default |
| Status write fails (disk full, permission) | n/a | best effort; one note in the summary | the row ages (`updated` old) but liveness stays right |
| Torn or foreign `status.json` | n/a | atomic rename; parse retry once; else `unreadable` | `unreadable`, never `lost` |
| Two sessions in one repo | both share the runs root and the ledger; nothing separates their runs | owner keys | each sees its own children; `--here` shows both with an OWNER column |
| Same name, same second, one run dir | `task.md` overwritten, two writers in one dir | two pointers name one `run_dir`; the one `status.json` flips between the two launchers' writes | two rows over one directory, both marked `shared-dir` and neither to be trusted (each can show the other run's counters); the fix (a unique dir, slice 6) is an operator question |
| Ledger rows interleaved by concurrent appends | possible (whether .NET append uses `O_APPEND` is unverified); torn rows silently dropped by `stats` | `status.json` and pointers are per run, no shared append | unaffected; the ledger risk stays as it is |
| A probe fails (`/proc` unreadable) | n/a | `unknown`, never deleted | `unknown`; it stops holding a `--wait` after `--grace`, which then returns 8 naming it |
| A poisoned or unparsable pointer | n/a | `unreadable`, never deleted | as `unknown`: exit 8 after `--grace` |
| `--wait` on an owner that keeps launching | n/a | the scope is fixed at start (or after `--expect` runs appeared); later runs are not joined | the header says so; the wait ends with the runs it took |
| Waiter started before its runs' pointers exist | n/a | `--started-after <t0> --expect <n>` | waits until `<n>` runs appear; exit 8 if they do not within `--grace` |
| Waiter unit with no time limit | n/a | `--timeout` defaults to 120m; every recipe names one | exit 7 at the limit |
| Two readers reap at once | n/a | unlink per file, `ENOENT` is success, no table rewrite | nothing |
| Owner restarted under a new relay name | the `UNIT DONE` message goes to the old name and fails after retries | the files survive; `status --here` at session start | the new session sees ended and live runs of its repo |
| Owner session `/clear`ed | n/a | the pid key still matches | the same table |
| Nested launcher inherits the top session's id | n/a | `LEAN_WORKER_RUN_ID` wins; inherited keys not matched | the nested run indented under its parent |
| Status pane not a descendant | n/a | no caller identity | exit 2 with the hint to pass `--owner` |
| Background `--wait` stopped by the harness time limit | n/a | `--timeout` below the limit, exit 7 | a wake that says "still running", and the session re-arms |
| Harness completion notification lost (#20754) | n/a | none from the design; the files keep the record | the next `status` shows the ended runs |
| Live claude count drops at the end | n/a | CALLS (`api_calls`) is shown, which only grows; `turns` only in detail | a steady count |
| Duplicate wakes (unit message plus a waiter) | n/a | the skill names one route per launch | one wake per launch or per batch |
| Wall-clock step or suspend | elapsed shifts (`LaunchRun.cs:1161`) | `elapsed_ms` from a `Stopwatch`; liveness never from clocks | elapsed stays right; ages computed by the reader may jump once |
| Mixed time zones in run files | `wrapup.json` and claude `hook.log` local, opencode `hook.log` UTC | the new files are UTC only | consistent ages |
| Quota deltas with concurrent runs on one plan | each run's `quota_before/after` includes the other runs' use (inference) | the table shows token-priced cost per run, not live quota deltas | no false per-run quota figure |
| A forged status file or pointer (the worker runs as the same user) | `summary.json` can be forged the same way today | nothing can tell; the files are trusted for display and waking only (4.1) | possibly a wrong row or an early wake; gate, scope, ledger and exit code are unaffected, and the orchestrator's own check after the wake catches a false success |
| Pointer directory growth | n/a | reaping after 7 days on proof | bounded |

## 6. Migration path

**Default behaviour.** The launch command line, stdout, the result block, the exit codes,
`summary.json`, `report.md` and the ledger row stay exactly as they are. What changes by default:
two new files (`status.json` in the run dir, a pointer under `$XDG_STATE_HOME/lean-worker/runs/`),
and, from slice 2, one new environment variable in the worker's environment.

**Always on, not behind a flag** (the operator rules; section 7). A flag would defeat the point: a
run launched without it is invisible, and the orchestrator would have to remember it on every
launch. The additions are outside every gate and scope check (verified, section 4.1), best effort,
and small. A kill switch, `LEAN_WORKER_STATUS=off` (proposal), turns both files off for anyone who
needs today's exact file set.

**Steps that keep things working at each point:**
1. The writer ships first. Existing readers of the run dirs and the ledger (`stats`, other tools)
   ignore the new file.
2. The reader ships in the same slice as the writer and handles a run dir with and without
   `status.json` from day one (a directory-only `legacy` row).
3. The skill text changes only when `status` exists: `status --here` at session start, `status`
   one-shot instead of reading files, `--owner "$(lw status --whoami --key)"` on unit launches, the
   batch waiter with `--started-after`, `--expect` and `--timeout`. `SKILL.md`'s "do not read
   `stream.jsonl`" stays.
4. `schema` versions in both files follow the ledger's rule (README "What is recorded": fields may
   be added within a version; a changed meaning raises it).

## 7. Open questions for the operator

Each with this design's recommendation.

*Ruled 2026-11-22:* the operator took the recommendations for questions 1, 3, 4, 5, 6, 10 and 11,
declined a rendering package (2) and changes in other repositories (7), and declined slice 7
(legacy counters) as not needed.

1. **Status files always on, or behind a flag?** Recommendation: always on, with the
   `LEAN_WORKER_STATUS=off` kill switch (section 6). It touches "today's launcher behaviour unchanged
   by default" only by adding files.
2. **A rendering package** (Spectre.Console 0.57.2 or Terminal.Gui 2.6.0) against `architecture.yaml`'s
   host-APIs-only rule. Recommendation: no; hand-rolled ANSI covers this table. A package would loosen
   a gate rule, which is reserved for the operator (`triage:operator`).
3. **A per-user index under `$XDG_STATE_HOME/lean-worker/`** is the launcher's first write outside the
   runs root and the quota cache. Recommendation: yes, file per run, as in 4.1.
4. **A new worker environment variable** (`LEAN_WORKER_RUN_ID`, both runtimes) for nested runs.
   Recommendation: yes; it is additive and it closes the misattribution through inherited
   session ids.
5. **A record-only signal handler** (SIGTERM, SIGINT, SIGHUP write `phase: failed` and let the
   default exit proceed). Recommendation: yes, as its own slice; whether the handler should also stop
   the worker group belongs to #121's hardening, not here.
6. **The same-second run dir collision.** Recommendation: a unique dir name (a `-<pid>` suffix only
   when the dir already exists). It changes a dir name in a case that corrupts runs today; it needs a
   ruling because other tools parse the stamp from the name.
7. **Changes in the operator's private relay tools** (none required for slices 1 to 7): a switch in
   `sy11a-unit-run` to skip its `UNIT DONE` message for batch launches; a command mode in the session
   opener for the status pane. Recommendation: the first if batches become common; the pane needs no
   change (the generic line in 4.5).
8. **A stable track identity across restarts** (so a restarted coordinator owns its predecessor's
   runs). Recommendation: not now; `status --here` covers it. Revisit if the restarting supervisor
   gains a stable track name it exports.
9. **Who shows the machine-wide view.** Recommendation: lean-worker's `status` stays session- and
   repo-scoped; the private dashboard daemon reads the new files for the machine-wide view
   (section 8).
10. **The `--wait` waiter in Claude Code** as an endorsed route next to `sy11a-unit-run`.
    Recommendation: endorse it for batches and idle owners, under the reading in 4.5; the
    single-run default stays today's background launch.
11. **The `--wait` defaults** (`--timeout` 120m, `--grace` 10m). Recommendation: these values;
    they are policy, and a shorter grace makes a transient `/proc` error end a wait early.

## 8. Relation to the prior investigation

A prior private investigation founded a machine-wide dashboard daemon for the operator's agent
sessions; this design's files are meant to be readable by it. The two do not compete: lean-worker's
`status` is scoped to one session or one repository, needs no daemon and no install, and ships with
the skill; the dashboard is the machine-wide view. The status file and the pointer index give such a
reader what the run dirs alone cannot: which runs are live, orphaned or lost, and their live
counters. The private companion note records the overlap and the differences in detail.

## 9. Implementation order

Smallest useful slice first; each ships on its own, behind the default-on rule of section 6 once the
operator rules on question 1.

| Slice | Delivers | Needs | Repositories |
|---|---|---|---|
| 1. Writer plus a first reader | `status.json` (phases, pids with start ticks, boot id and session id, CALLS and the other live counters, last tool, `end`; one write lock; best effort; kill switch) and `status --run <dir>` and `status --here` one-shot and `--json`, with liveness, the ended rule, the group check and the directory-only `legacy` row | `RunStatus`, `StatusCommand`, hooks at phase changes, in the line handler and in the catch block; tests over recorded streams of both runtimes | lean-worker |
| 2. Owner capture and the pointer index | owner keys at launch, `--owner`, `LEAN_WORKER_OWNER`, `LEAN_WORKER_RUN_ID` in both runtimes' worker environments, pointer files; the default caller scope, `--owner`, `--all`, `--whoami`, nested rows, `shared-dir` detection, reaping | slice 1; operator answers 3 and 4 | lean-worker |
| 3. `status --wait` | the waiter with the fixed scope, `--started-after`, `--expect`, `--any`, `--timeout` (default 120m), `--grace`; exit codes 0/1/3/6/7/8; skill text for both runtimes (`status --here` at session start, `--owner` on unit launches, the recipes of 4.5) | slice 2; operator answer 11 | lean-worker (SKILL.md, templates) |
| 4. `status --watch` | the live table: ANSI renderer, column dropping, acknowledgement, `--bell`, the non-TTY line mode; the pane line in the skill | slice 2 (slice 1 for `--here`) | lean-worker |
| 5. Record-only signal handler | `failed (signalled)` instead of a silent orphan | slice 1; operator answer 5 | lean-worker |
| 6. Unique run dir on collision | no shared run dir, so no `shared-dir` rows | operator answer 6 | lean-worker (and readers that parse dir names) |
| 7. Optional: legacy counters | turns, calls and cost for pre-status runs, re-read from `stream.jsonl` with the runtime parsers and the Meter | slice 1 | lean-worker |
| 8. The dashboard daemon reads the new files | machine-wide live rows, orphaned and lost runs | slices 1-2 | the operator's private dashboard repository |
| 9. Optional relay-tool switches | skip the `UNIT DONE` message; a pane mode | operator answer 7 | the operator's private tools repository |

Slice 1 is useful by itself: the orchestrator can look at one run or at its repository mid-run.
Slices 1 to 7 touch only this repository. Each is verified with this repository's per-change set
(`AGENTS.md`, "Verify"); slice 1's tests can replay recorded streams.

## 10. Where this design departs from the starting position

- **One status file, not a start record plus a live record.** The immutable part that discovery
  needs is the pointer; the run dir holds one file with a stable and a live part (4.1).
- **No `status.jsonl`.** The append log duplicates `stream.jsonl` and the ledger (4.1).
- **No heartbeat field or timer.** Liveness is pid plus start ticks plus boot id; a heartbeat would add
  a timer to prove what `/proc` already proves. `updated_at` and `last_event_at` remain, as ages.
- **A terminal phase is final only after the launcher has exited.** The status files are advisory:
  display and waking only (4.1).
- **The stream fallback cannot judge liveness for legacy runs.** Old launchers recorded no pid; the
  first reader shows a directory-only `legacy` row with liveness `unknown`, and counters from the
  stream are a later, optional slice (4.1, slice 7).
- **`orphaned` needs the worker's identity, not just a group number.** Same boot, and the group
  leader with the recorded start ticks or members with the recorded group and session; otherwise
  `lost`. The row never offers a kill (4.1 step 5).
- **`--wait` is bounded.** Its scope is fixed at start, it has a 120-minute default limit, rows that
  cannot be resolved release it after a grace period with exit 8, and every recipe names a
  `--timeout` (4.2, 4.5).
- **CALLS, not turns, in the table.** A live claude run has no turn count; calls only grow (4.1).
- **Owner keys come from `status --whoami --key`**, so no recipe builds a key by hand (4.2, 4.4).
- **The pane is not opened by the existing session opener.** It cannot run an arbitrary command; the
  pane is one generic `systemd-run ... konsole` line (4.5).
- **Ownership by keys, with the pid key and `LEAN_WORKER_RUN_ID`.** The session id changes on `/clear`
  and is inherited by nested launchers through opencode 1.x workers; the pid key and the run id cover
  both. The relay name is display only.
- **`--wait` has exit code 6 for "nothing matched"**, so a wrong owner never reads as all done.
- **Kept:** pull from durable files; completion only over the relay, one per launch; pid plus start
  time against reuse; never "quiet means dead"; file-per-run pointers with proof-only reaping; three
  modes of one subcommand; default behaviour unchanged except added files.

## 11. What was not verified

- That `claude -p` sets `CLAUDE_CODE_SESSION_ID` for its own Bash children as the interactive CLI does.
- What an opencode or claude worker does when the launcher dies and its stdout reader disappears
  (EPIPE or SIGPIPE).
- Whether .NET's file append mode uses `O_APPEND` on Linux.
- What happens to a `run_in_background` launcher when its Claude Code session exits; whether the
  operator's long-running sessions count as unattended for the background time limit.
- Whether a background-task completion wakes an idle Claude Code session in an unattended or `-p`
  session. It was observed only in one interactive, attended session (4.5).
- That Linux never reuses a pid number while it is still a live process group or session id (the
  basis of the `orphaned` check when the group leader has exited, 4.1 step 5).
- The status pane under Wayland (`WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR` passed into the unit), and
  either pane line at all (4.5).
- opencode 2.x behaviour (`OPENCODE_SESSION_ID`, background shells): read from source by the survey,
  not run; the operator's machine has 1.18.35.
- `PosixSignal.SIGWINCH` handling and `Console.WindowWidth` on a pipe under the .NET 8 API surface
  the launcher targets (`LeanWorker.csproj:6`), on whatever newer runtime `RollForward=LatestMajor`
  picks on a given machine; neither was tried.
- The start-up time of `dotnet run --project ... status`.
- Every survey link except the ones re-checked in the verification log of
  [current-state.md](current-state.md).

