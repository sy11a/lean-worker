# Survey: how parallel-agent supervision tools build their "runs table"

Researched 2026-11-22. Primary sources only: source code (shallow clones at the commits listed), official docs
and
changelogs, and GitHub issue trackers. Anything I could not check against a primary source is marked
**unverified**.
Where a source says nothing, the entry reads **not found**.

Provenance: written by a survey session for the live-status design ([README.md](README.md)) and copied here
unchanged apart from this note and the re-check marks below. The design session re-checked herdr-core's README
(`hc wait --children all`, "Core draws nothing", `notes_quiet = 3`, the 180 s check-in), vibe-kanban #2495
(open), workmux #209 (open) and agent-deck #1550 (closed) on 2026-11-22; the other links were not re-fetched.

## Versions and commits examined

| Tool | Repo / source | Version | Commit / date examined |
|---|---|---|---|
| herdr (TUI) | github.com/herdrdev/herdr | v0.9.5 (2026-10-14 release; `Cargo.toml` 0.9.5) | `e8d7d9705a7a` (2026-11-20) |
| herdr-core (`hc`) | github.com/herdrdev/herdr-core | 0.1.21 (2026-11-12, `crates/herdr-core/Cargo.toml`) | `c7517808d64f` (2026-11-17) |
| Herdr app | github.com/herdrdev/herdr-app | 0.1.16 (`Cargo.toml`) | `317e673f1f15` (2026-11-18) |
| claude-squad | github.com/smtg-ai/claude-squad | v1.0.20 (2026-08-20) | `070eb9387cda` (2026-11-13) |
| Conductor | closed source; conductor.build docs, changelog, `llms-full.txt` | 0.98.0 (2026-11-20) | docs fetched 2026-11-22 |
| omnara (current) | github.com/omnara-ai/omnara | omnarad-v0.2.27 (2026-11-20) | `313ac70cc199` (2026-11-19) |
| omnara (legacy CLI wrapper) | same repo, older tree | pip `omnara`, pre-pivot | `d3634b0c7e76` (2025-10-10) |
| vibe-kanban | github.com/BloopAI/vibe-kanban | v0.1.46 (2026-11-15) | `3f095f0186f6` (2026-11-15) |
| claude_code_agent_farm | github.com/Dicklesworthstone/claude_code_agent_farm | no releases (CHANGELOG.md says so) | `d88ba78ea5e5` (2026-09-21) |
| cmux (analogue) | github.com/manaflow-ai/cmux | v0.65.2.2 (`MARKETING_VERSION = 0.65.2`) | `5c8be11aa8e3` (2026-11-22) |
| workmux (analogue) | github.com/raine/workmux | v0.1.284 | `748997d6e9a6` (2026-11-21) |
| agent-deck (analogue) | github.com/asheshgoplani/agent-deck | v1.16.39 | `80494e527393` (2026-11-16) |
| ccmanager (analogue) | github.com/kbwo/ccmanager | v4.4.11 | `b0948dd25b41` (2026-10-27) |

Link shorthands used below (all point at the commits above):
`HR`=https://github.com/herdrdev/herdr/blob/e8d7d9705a7a2c89cef4dd42079379f05685af87,
`HC`=https://github.com/herdrdev/herdr-core/blob/c7517808d64f06609e4ecf0db1757005373cc3a3,
`HA`=https://github.com/herdrdev/herdr-app/blob/317e673f1f1568b876cbaa0b41dabeb9153ecbea,
`CS`=https://github.com/smtg-ai/claude-squad/blob/070eb9387cda2be2e64aa1a32ee81be160c457a1,
`VK`=https://github.com/BloopAI/vibe-kanban/blob/3f095f0186f6e68d67143d38b02da2238f6524d8,
`OM`=https://github.com/omnara-ai/omnara/blob/313ac70cc1991e013c6fc54813cb13eae6be6992,
`OL`=https://github.com/omnara-ai/omnara/blob/d3634b0c7e76d83a8868d95d03b872475d63066d,
`AF`=https://github.com/Dicklesworthstone/claude_code_agent_farm/blob/d88ba78ea5e50abed8e7f60e2cf00ce678b6375d,
`CM`=https://github.com/manaflow-ai/cmux/blob/5c8be11aa8e3f2ed2734c57981301f32aa62ffec,
`WM`=https://github.com/raine/workmux/blob/748997d6e9a6cf4d8f25035ee996f85251bd65a9,
`AD`=https://github.com/asheshgoplani/agent-deck/blob/80494e527393b7ead00da30018e6ee7e60e9d7a0,
`CC`=https://github.com/kbwo/ccmanager/blob/b0948dd25b41511d67c2b0db386082d21a9fdfbd.
Issue references `#N` point at the tool's own repository.

Context worth knowing first:
- **The name.** "herd" in the first brief was the wrong name. The coordinator's correction says the tool is
  **herdr** (herdr.dev). herd.build refused connections (ECONNREFUSED) and GitHub has several unrelated
  "herd"/"herdr"
  repos (for example brac/herdr, a claudectl fork, and herd-os/herd). I dropped all of them. herdr.dev links
  to the
  herdrdev org, which has three repos: **herdr** (the TUI multiplexer), **herdr-core** (`hc`, a headless
  terminal
  server) and **herdr-app** (a GPU desktop app on herdr-core).
- **Two tools are winding down.** vibe-kanban's README says "Vibe Kanban is sunsetting". The blog post
  (vibekanban.com/blog/shutdown, 2026-04-10) says Bloop is shutting down and the repo goes
  community-maintained.
  Conductor's changelog page header reads "Conductor is joining Meta".
- **omnara pivoted.** It is now a hosted/self-hosted "managed agent platform" (Go, Postgres event log). The
  earlier
  product, which wrapped `claude` and mirrored it to phone and web, is only in the git history. I cover both
  and mark
  which is which.

---

## 1. herdr (herdrdev: herdr TUI, herdr-core, Herdr app)

### Discovery
- **herdr TUI.** Herdr owns every pane's pty, so it has no external scan. Inside a pane it finds the agent by
  resolving the pane shell's **foreground process group** and normalising the process name/argv0 against a
  list of
  24 known agents (`HR/src/detect/mod.rs`, `foreground_job`, `normalized_process_name`, `Agent::ALL`).
  - Wrappers hide the real process, so the docs offer `HERDR_AGENT=<agent>` as an override. Runtimes without a
    foreground process group get `HERDR_PROCESS_DETECTION=child-groups`
    (`HR/docs/next/website/src/content/docs/agents.mdx`,
    "VMs and sandbox wrappers").
  - Agents running inside tmux in a pane are not seen: the pane process is `tmux` (same doc, tmux section).
- **herdr-core.** Each terminal is a record in the core. `hc ls` lists them, and `terminal.list` over a Unix
  socket
  (named pipe on Windows) returns JSON-RPC (`HC/README.md`, `HC/schema/README.md` "Connecting").
  - Each terminal runs under a "holder" process with its own socket beside the client socket
    (`HC/docs/security.md`). Agents started inside an `hc` terminal are registered as its children
    (`HC/README.md` "Agents run agents").

### Streaming
- **herdr TUI.** A per-pane detection task reads the live bottom of the pane buffer:
  - every **300 ms** once an agent is identified, **500 ms** while unidentified, **50 ms** during a pending
    release
    (`HR/src/pane.rs` ~L2852, `TICK_IDENTIFIED` etc.);
  - it matches that text against per-agent TOML "detection manifests" (`HR/src/detect/manifests/*.toml`).
  - Manifests are auto-updated from herdr.dev and can be overridden in
    `~/.config/herdr/agent-detection/<agent>.toml`
    (agents.mdx "Detection manifests").
  - Optional integrations push state over the socket. The Claude hook only reports the session id/transcript
    path on
    `SessionStart`, via `herdr pane report-agent-session`
    (`HR/src/integration/assets/claude/herdr-agent-state.sh`).
    The OpenCode plugin maps bus events to states (`permission.asked`→blocked, `session.status` busy→working,
    etc.;
    `HR/src/integration/assets/opencode/herdr-agent-state.js` L19-45, L184).
  - Clients talk to the server over a socket API with `events.subscribe` / `events.wait`
    (`HR/docs/next/website/src/content/docs/socket-api.mdx`).
- **herdr-core.** `events.subscribe` returns a snapshot with a `seq`, then JSON-RPC notifications (`created`,
  `status`,
  `turn`, `finished`, `lost`, `foreground`, ...) with `seq` +1 per event (`HC/schema/README.md` "Events").
  - A heartbeat arrives every **15 s**. A slow subscriber gets `missed` plus a fresh snapshot. `seq` restarts
    with
    each core process, so clients must re-snapshot after reconnecting.
  - The foreground process is "read about twice a second" (same section).
  - `hc wait` / `hc wait --children all` block on state. A waiting `hc run` checks in after **180 s** by
    default
    (`HC/README.md`).

### Rendering
- **herdr TUI:** ratatui 0.30 + crossterm 0.29 (`HR/Cargo.toml` L34, L41). It is a full multiplexer with a
  sidebar
  that rolls agent state up to tabs and workspaces (agents.mdx intro).
- **herdr-core:** "Core draws nothing" (`HC/README.md`).
- **Herdr app:** GPU UI on a vendored `gpui` (Zed's UI framework) (`HA/Cargo.toml` L20-23).

### Completion
- **herdr TUI.** Every pane is `working`, `blocked`, `idle` or `unknown` (`HR/src/detect/mod.rs`
  `AgentState`).
  - Notifications are configurable as an in-app toast, outer-terminal OSC notification (Ghostty, iTerm2,
    Kitty,
    WezTerm, detected via `TERM_PROGRAM`/`TERM`), OS notification (`terminal-notifier`/osascript on macOS,
    D-Bus via
    `zbus`), or off. Sound plays mp3 files
    (`HR/src/terminal_notify.rs`, `HR/docs/next/website/src/content/docs/configuration.mdx` "Notifications",
    "Sound").
  - Scripts and agents use `herdr agent wait <pane> --until done` (socket-api.mdx L81-84).
- **herdr-core.** "The parent gets notes when a child needs attention or ends". Delivery waits for a quiet
  period
  (`notes_quiet = 3` s) and holds while the user has an unfinished draft (`draft_wait = 60` s) (`HC/README.md`
  "Draft wait", "Agents run agents"). This is a message back into the parent agent.

### What goes wrong (issues in herdrdev/herdr unless noted)
- **Screen heuristics mis-detect state constantly**, even with per-agent manifests:
  - #5593 Claude AskUserQuestion shows idle instead of blocked when the pane is scrolled up;
  - #6063 a Claude WebFetch permission dialog is reported as done;
  - #4573 AskUserQuestion reports idle under `--agent`;
  - #5490 a Codex plan picker shows as idle;
  - #4429 Devin permission menus with 8+ options never classify as blocked;
  - #5799 Copilot shows idle during work;
  - #352 a stale "esc to interrupt" keeps Codex stuck working;
  - #3530 Antigravity/Grok report idle while still generating.
- **The authors document the danger themselves:** "automation that trusts a mistaken idle state can type into
  an
  approval dialog" (agents.mdx "Blocked state"). The 0.9.5 CHANGELOG dropped Codex hooks and went back to
  screen
  detection because the hooks timed out (#5246).
- **Sources disagree:**
  - #3993 `agent explain` says working while `agent list` says idle;
  - #4875 the server stays 'working' while detection says idle;
  - #5014 an aborted OMP approval leaves the pane blocked permanently;
  - #5222 an agent that finishes with no client attached shows `idle`, not `done`.
- **Identity drift:** #216 the hook breaks after a pane renumber (stale `HERDR_PANE_ID`); #5231 clients start
  duplicate servers when discovery fails, duplicating agent panes.
- **Processes do not survive restarts.** After a server or machine restart "the original processes do not
  survive".
  Herdr restores the layout and may resume agent sessions natively (`HR/README.md`;
  `.../session-state.mdx` table). The 0.9.4 changelog records crash-safe save files (#5150) and keeping the
  server
  alive on process/thread exhaustion (#3324).

### Daemon? Status derivation?
- **Daemon: yes in all three.** The herdr TUI runs a background server and the client attaches; since 0.9.4
  closing
  the launching shell no longer stops it (CHANGELOG 0.9.4, `HR/src/server/`). herdr-core is the server, and
  the app is
  a client of herdr-core.
- **Status derivation:**
  - herdr TUI: **screen scraping by manifest** plus process identification, with structured reports from
    integrations winning where present (agents.mdx "How status works").
  - herdr-core: **structured only**. "It does not guess from screen text or recognize agent brands"; status is
    whatever the program reports through **OSC 7501** (Program Status Protocol, rev 0.4 dated 2026-10-10,
    published on
    superlogical.com/rex/docs/build/program-status). Programs that don't emit it have no status
    (`HC/README.md`
    "Status comes from the program"; `HC/schema/README.md` "Status").
  - herdr-core terminal end reasons are explicit: `exited`, `killed`, `server_stop`, `host_shutdown`,
    `holder_lost`,
    `stopped_by_restart` (schema README).

---

## 2. claude-squad (smtg-ai)

### Discovery
- Each instance is a tmux session named `claudesquad_<title>` (`CS/session/tmux/tmux.go` L60 `TmuxPrefix`).
- Instances are persisted as JSON in `~/.claude-squad/instances.json` (plus `state.json`), with title, path,
  branch,
  status, program, worktree data (repo path, worktree path, session name, base commit) and diff stats
  (`CS/config/state.go` L12-13, `CS/config/config.go` L26, `CS/session/storage.go`).
- On start it restores from that file and reattaches if `DoesSessionExist()` (`CS/session/instance.go` ~L254,
  L542).
- `CleanupSessions` kills by prefix (`CS/session/tmux/tmux.go` L499).

### Streaming
- **Capture command:** `tmux capture-pane -p -e -J -t <session>` (`CS/session/tmux/tmux.go` L478).
- **Poll interval:** a self-chaining Bubble Tea command sleeps **500 ms**, then captures every active instance
  and
  computes git diff stats (`CS/app/app.go` L945-956, "overlapping ticks are impossible").
- **Status rule:** the capture is hashed. Changed since the last tick means `Running`; unchanged means `Ready`
  (`CS/session/tmux/tmux.go` L244-267, `HasUpdated`; `CS/app/app.go` L238-250).

### Rendering
- Bubble Tea v1.3.4 + bubbles + lipgloss (`CS/go.mod` L9-11), with `tea.WithAltScreen()` (`CS/app/app.go`
  L30).
- A list pane plus a preview/diff tab window; attach hands the tty to tmux.

### Completion
- Statuses are `Running`, `Ready` ("waiting for user input"), `Loading` and `Paused` (worktree removed, branch
  kept)
  (`CS/session/instance.go` L18-28).
- No bell or desktop notification code found: a grep for notify/bell/beep in `app/ ui/ session/ daemon/` finds
  only
  signal handling. The only signal is the list row.
- Prompt detection exists only to auto-accept. The literal strings are "No, and tell Claude what to do
  differently"
  (Claude), "(Y)es/(N)o/(D)on't ask again" (aider) and "Yes, allow once" (Gemini). On a match it taps Enter,
  but only
  when `AutoYes` is set (`CS/session/tmux/tmux.go` L253-259; `CS/session/instance.go` L359-366).

### What goes wrong
- #284: a pause/checkout fails when `IsDirty()` hits index-lock contention with the **500 ms metadata loop
  running
  `git add -N .`**. The worktree gets deleted while the status stays active, in an unrecoverable state.
- Capture failures: #51 and #218 "error capturing pane content: exit status 1". In #218 the root cause was
  actually a
  broken claude install, surfaced as a capture error.
- Slow UI with many sessions: #215 (multi-second keystroke latency) and #151.
- Input and prompt handling: #266 SendPrompt sends text before the CLI is ready, so the prompt is lost; #325
  Bubble
  Tea's input reader races the tmux attach reader for stdin.
- #212: no resume after reboot. tmux sessions die with the machine, and only the JSON row survives.
- "Changed hash ⇒ Running" is a pure activity heuristic. Any redraw (spinner, clock) reads as Running and a
  silent
  long tool call reads as Ready. This is inferred from the code; I found no issue that pins it.

### Daemon? Status derivation?
- No daemon for the UI. An optional background daemon exists only for AutoYes: it polls sessions every
  `daemon_poll_interval` = **1000 ms** by default and writes `daemon.pid` (`CS/daemon/daemon.go` L36, L120;
  `CS/config/config.go` L95).
- Status comes from **screen scraping** (pane-hash diff plus literal prompt strings).

---

## 3. Conductor (conductor.build; closed-source Mac app)

The source is not public, so everything about internals beyond the docs is **unverified**.

### Discovery
- Every workspace is a git worktree + branch, "usually created under `~/conductor/workspaces/<repo
  name>/<workspace
  name>`". App state lives in `~/Library/Application Support/com.conductor.app`
  (`https://www.conductor.build/llms-full.txt` ~L3995 and ~L889).
- Agents run "through the Claude Code setup available on your machine"
  (docs/guides/parallel-agents/run-multiple-claude-code-sessions).
- How it tracks the agent processes: **not found**. Whether it uses `claude -p` stream-json, the Agent SDK or
  a pty is
  **unverified**. The 0.36.3 changelog title "Submit a Prompt, SDK Memory Leaks" (2026-02-23) suggests an SDK
  integration; that is also **unverified**.

### Streaming
- Local internal transport: **not found**.
- The public API (`api.conductor.build/v0`, REST + MCP) is **polling-only**:
  - `GET /v0/sessions/{id}/status` returns `idle`/`working`/errored;
  - the cookbook says "poll … every ~15 seconds" and read `messages?after=<id>`;
  - no outbound webhooks or events (www.conductor.build/docs/api; llms-full.txt ~L977-988, L1107, L1140).
- Changelog 0.98.0 (2026-11-20): "Conductor now recovers automatically when its background service stops
  unexpectedly". So a background service exists; its protocol is **unverified**.

### Rendering
- Native Mac GUI. The toolkit is **unverified**. It has a sidebar grouped by status (Backlog / In progress /
  In review
  / Done; changelog 0.95.0) and per-workspace terminal, diff and checks tabs.

### Completion
- A **completion sound**: custom completion sounds in 0.93.0 (2026-11-04); "Fixed Conductor freezing for a few
  seconds when playing the completion sound" in 0.98.0; "Sounds & colors" in 0.52.0.
- An iOS app offers remote control (llms-full.txt ~L3637).
- Since 0.95.0 (2026-11-16), agents can start other agents, "wait for its reply", and set workspace statuses.
- Desktop notifications: only "stacked notifications" are mentioned (changelog fix). The details are **not
  found**.

### What goes wrong
- No public issue tracker found. From the changelog fixes:
  - "Fixed Claude chats showing as working after publishing an artifact" (0.95.0): a mis-detected status;
  - "Fixed a new cloud workspace's first message sometimes getting lost if Conductor quit while the workspace
    was
    being created" (0.95.0): a run lost on crash;
  - "Fixed new workspaces sometimes getting stuck on 'Running setup script…' when the setup script finished
    instantly" (0.94.x): a missed fast transition;
  - "Fixed a crash loop when opening a chat with a very long history" (0.95.0).
- The API docs warn about a race: a queued prompt reports `idle` before it starts, so "wait until you have
  seen
  `working`" before trusting the next `idle`, and "a very fast turn can start and finish between polls"
  (llms-full.txt ~L1140).

### Daemon? Status derivation?
- A background service exists (0.98.0); its details are **unverified**.
- Status derivation is **unverified**. The API exposes a structured `idle`/`working`/error session status.

---

## 4. omnara

### 4a. Legacy omnara (pip `omnara` CLI wrapper, tree at 2025-10-10)

**Discovery**
- Each wrapped run registers an agent instance row with the Omnara backend (Postgres). The wrapper launches
  `claude`
  under a local pty (`OL/src/integrations/cli_wrappers/claude_code/claude_wrapper_v3.py`, `import pty`).
- It locates the transcript by polling for `~/.claude/projects/.../<agent_instance_id>.jsonl` every **0.5 s**
  (same file ~L427-439).

**Streaming**
- **Transcript tail:** the wrapper tails the Claude JSONL (0.1 s sleep loop; it switches files on session
  reset,
  ~L447-515) and posts each message to the API.
- **Heartbeat:** a POST to `/agents/instances/{id}/heartbeat` every **30 s ± 2 s jitter** (~L241, L387-417).
- **Server to browser:** Postgres `LISTEN/NOTIFY` per instance feeds an SSE endpoint, with a 30 s wait timeout
  for
  heartbeats (`OL/src/backend/api/agents.py` ~L290-330; NOTIFY in `OL/src/servers/api/routers.py` ~L394-405).
- **Online status:** the web UI shows an instance as online if its last heartbeat is within a **60 s TTL**
  (`OL/apps/web/src/components/dashboard/chat/ChatWorkingIndicator.tsx` L9-11).

**Rendering**
- Web dashboard and mobile apps. The terminal stays the normal Claude TUI (README).

**Completion**
- Mobile **push notifications** through Expo (`OL/src/servers/shared/notifications.py`,
  `exponent_server_sdk`).
  Twilio SMS/email is also present (`OL/src/servers/shared/twilio_service.py`).
- "Needs input" is inferred: Claude counts as idle when "esc to interrupt" has not appeared on screen for
  `idle_delay = 3.5 s` (`claude_wrapper_v3.py` ~L195, L629-634, L1053-1069). The wrapper then calls
  `request_user_input`.

**What goes wrong**
- #81 "waiting for input" spammed under Warp/Alacritty or on low-resource machines (screen-timing heuristic);
- #145 80+ notifications in a short span with auto-accept;
- #215 resuming a conversation re-sends notifications for every past interaction (transcript replay);
- #76 subagent messages arrive out of order and duplicated, each with a notification;
- #115 output missing from the dashboard.

**Daemon? Status derivation?**
- No local daemon: the wrapper is the parent process and the server is SaaS or self-hosted.
- Status comes from the transcript (structured) for content, plus **screen scraping** ("esc to interrupt") for
  the
  idle/needs-input decision.

### 4b. Current omnara (managed agent platform)

**Discovery**
- Agents are rows in the control plane, where each agent is an event log in Postgres.
- Machines run a daemon (`cmd/daemon`, released as `omnarad`) and attach to agents through bindings. "The
  machine does
  not own the agent … Disconnecting a machine does not kill the agent" (`OM/docs/machines/overview.mdx` L11).
  - Machines report a `connection_state` of `online`/`asleep`/`offline`. After a grace period, tool calls
    waiting on an
    unreachable machine return a retryable `machine_unreachable` (same doc L30-32).

**Streaming**
- `GET /events` gives a paginated history with monotonically increasing `sequence`.
- `GET /events/stream` is SSE with the sequence as the SSE `id`, heartbeat comments every **10 s**, and a
  `Last-Event-ID` resume (`OM/docs/events/streaming.mdx`).

**Rendering**
- Web dashboard and CLI (`npx omnara agents chat`) (`OM/README.md`).

**Completion**
- Timeline events (`model_output` with `stop_reason`, `tool_result`) and webhooks
  (`OM/docs/events/webhooks.mdx`).
  Slack/Discord/GitHub surfaces are listed in the README diagram.

**What goes wrong**
- #502: the SSE stream still closes every connection at ~16 s despite the 10 s heartbeat (#421), and
  `Last-Event-ID`
  resume does not cover delta frames.
- #514: resuming a Claude session rewrites the session JSONL and the thread gets permanently stuck.

**Daemon? Status derivation?**
- Server plus a per-machine daemon.
- Status is fully **structured** (the agent loop runs in Omnara's own workers, not in a scraped CLI).

---

## 5. vibe-kanban (BloopAI)

### Discovery
- Every agent run is an `execution_processes` row in local **SQLite**
  (`VK/crates/db/src/models/execution_process.rs`
  L253-302, `find_running`, status `'running'`).
- Live children are kept in an in-memory `child_store` map in the server
  (`VK/crates/local-deployment/src/container.rs`
  ~L810-845).
- The DB row has no pid column (grep for `pid` in the model finds none).

### Streaming
- **Structured stdout.** Claude is launched with `--verbose --output-format=stream-json
  --input-format=stream-json
  --include-partial-messages --replay-user-messages`, and the control protocol runs over stdin/stdout,
  including
  `can_use_tool` approvals (`VK/crates/executors/src/executors/claude.rs` ~L188-194;
  `VK/crates/executors/src/executors/claude/protocol.rs` L54-100). Amp, Droid and Cursor are also run in
  stream-json
  mode.
- Output goes into a `MsgStore`: a tokio broadcast channel of 100000 messages, with a 100000 KiB history and
  JSON-Patch messages (`VK/crates/utils/src/msg_store.rs` L13, L39, L70).
- The browser receives it over **WebSockets**: `/execution-processes/{id}/raw-logs/ws`, `/normalized-logs/ws`
  and
  `/stream/session/ws` (`VK/crates/server/src/routes/execution_processes.rs` ~L285-305).
- Exit detection polls `try_wait()` every **250 ms** (`container.rs` ~L828-845).

### Rendering
- React 18 + Vite web UI served by the local Rust (axum) server (`VK/packages/local-web/package.json`). A
  Tauri desktop
  wrapper also exists (`VK/crates/tauri-app`).

### Completion
- `NotificationService` plays a sound (afplay / paplay ...) and sends an OS push. The Tauri app can register
  its own
  notifier (`VK/crates/services/src/services/notification.rs` L10-112).
- Approvals are surfaced in the UI through the control protocol.

### What goes wrong
- **#2495 (open): "Claude Code executor never signals completion — sessions stuck in 'running' forever".**
  With
  `-p --output-format=stream-json` plus stream-json input, Claude stays alive waiting on stdin after the
  `result`, and
  the executor returns `exit_signal: None`.
  - At the examined commit, `claude.rs` L714 still returns `exit_signal: None`.
  - `protocol.rs` now breaks the read loop on a `Result` message (L90-92). Whether that closes the run is
    **unverified**.
- **Restart handling.** On startup `cleanup_orphan_executions` marks every DB row still `running` as `Failed`
  with no
  exit code (`VK/crates/services/src/services/container.rs` L276-328). It does not look for or kill the actual
  process (no pid stored). That a survivor could keep running is my inference, **unverified**.
- #3205: after macOS sleep/wake, `opencode serve` children become unresponsive zombies and VK keeps hitting
  the stored
  port with no lifecycle management.
- #3227: a session stays "running" on mobile because the WebSocket does not reconnect after tab suspension.
- Worktree build-up: #765 asks for automatic worktree cleanup; #2907.

### Daemon? Status derivation?
- Yes: a local server process (`npx vibe-kanban`) owns all children.
- Status is **structured** (process exit, stream-json `result`, control protocol), not scraped.

---

## 6. AgentFarm-class: claude_code_agent_farm (Dicklesworthstone)

I picked this repository as the representative of the class. Other tmux-based runners are covered as herdr
analogues
in section 7 (workmux and agent-deck are both tmux-substrate).

Licence note: `CHANGELOG.md` (2026-02-21) records "MIT + OpenAI/Anthropic Rider", which restricts use by those
companies. Read it for ideas only; don't copy code.

### Discovery
- One tmux session with a `controller` window plus one pane per agent (`AF/claude_code_agent_farm.py`
  ~L1415-1419,
  `tmux new-session -d -s <session> -n controller`).
- All agents share one checkout, with no worktrees (grep finds none).
- Files written to the project directory:
  - per-agent heartbeat files `.heartbeats/agentNN.heartbeat` (~L240-246, L498-510);
  - a monitor state file `.claude_agent_farm_state.json`, written via tmp + rename (L62, ~L2251-2290).

### Streaming
- `tmux capture-pane -t <target> -p`, with retries (L191-201).
- The check runs every `--check-interval` = **10 s** (L2401-2403, loop L1934-1940).
- Status is decided by substring matching on the captured pane:
  - ready = "Welcome to Claude Code!", "│ >", box borders (L310-321);
  - working = "✻ Thinking", "● Bash(", "esc to interrupt" (L326-328);
  - context % is parsed from the screen (L290).
- A heartbeat is stamped on every `tmux_send` and when the agent is seen working or ready.

### Rendering
- `rich` `Live` table refreshed at `refresh_per_second=1`, in the tmux controller window, fed from the state
  file
  (L27, ~L2532-2548).
- Heartbeat age is coloured: green <30 s, yellow <60 s, red otherwise (L587-596).

### Completion
- No notification. The tool's goal is auto-restart. `needs_restart` returns:
  - `error` if the heartbeat is older than **120 s**;
  - `context` (send `/clear`) under the context threshold;
  - `idle` for prolonged idleness;
  - restarts use exponential backoff (L528-557, L1940-1945).

### What goes wrong
- From the CHANGELOG:
  - **`~/.claude` corruption when many agents start concurrently.** Fixed with a launch lock
    (`~/.claude/.agent_farm_launch.lock`, `O_CREAT|O_EXCL`), settings backup/restore, and a stagger raised
    from 4 s
    to 10 s with a 15 s post-launch wait (CHANGELOG ~L328-335; code L1574-1605).
  - An "adaptive timeout = 3x the median" was added to prevent false stuck detections (CHANGELOG ~L143).
  - A false-positive guard on settings-error detection (~L352-353).
- The issue tracker has only 5 issues (none about detection). #4 asks which coordination rules survive at 20+
  agents.
- On Ctrl-C it kills the tmux session and deletes the state file (L720-738). Crash recovery: **not found**.

### Daemon? Status derivation?
- No daemon: the Python process is the monitor and tmux is the substrate.
- Status comes from **screen scraping** plus heartbeat-file mtime.

---

## 7. herdr's analogues

**How I chose them.** herdr describes itself as an agent-aware terminal multiplexer and runtime: it owns the
agents' terminals, shows working/blocked/idle per pane, and notifies. herdr.dev/compare names no competitors.
I took
the closest tools from a third-party comparison series about herdr
(flaviocopes.com/herdr-alternatives, /herdr-vs-cmux, /herdr-vs-tmux-zellij; **secondary sources**, used only
to
pick names).
- **Picked from that series:** cmux (the main named rival) and workmux (agent status in tmux/Zellij tabs).
- **Picked by function:** agent-deck and ccmanager, tmux/pty session managers whose core feature is per-agent
  status
  detection, like herdr's. ccmanager's README positions itself against claude-squad; agent-deck uses tmux as
  its
  substrate.
- **Skipped:** tmux, Zellij and Ghostty (not agent-aware); T3 Code and bb (thread IDEs, a different shape);
  claude-squad and Conductor (already covered).

### 7a. cmux (manaflow-ai) — native macOS terminal for agents

- **Discovery.** cmux is the terminal, so panes and workspaces are its own objects. Agent sessions are bound
  to them
  by hooks.
  - Session hooks write `~/.cmuxterm/<agent>-hook-sessions.json` with agent session ID, workspace ID, surface
    ID, cwd,
    pid when available, lifecycle (`running`/`idle`/`needsInput`/`unknown`) and a sanitised launch command
    (`CM/docs/agent-hooks.md` "What the hooks record").
  - For Claude, a `claude` wrapper injects the hook settings, so no user settings edits are needed. Hooks
    exist for
    about 20 other agents, including OpenCode plugins (same doc, table).
- **Streaming.**
  - Claude Code hook events (UserPromptSubmit, Stop, Notification, PermissionRequest;
    `CM/CLI/FeedEventClassifier.swift`
    L216-274) go to the app over its socket API.
  - The terminal also parses OSC 9/99/777 notifications and **OSC 7501** program status (rev 0.3)
    (`CM/docs/program-status.md`, `CM/README.md` L122).
  - Program-status `working`/`blocked` records are dropped on a new prompt (OSC 133 A) or process exit;
    `done`/`error`
    stay until the pane is focused.
- **Rendering.** Swift/AppKit with libghostty for the terminal, and a vertical sidebar showing branch, PR,
  cwd, ports
  and the latest notification (`CM/README.md` L57, L122).
- **Completion.** Blue "notification rings" on panes, lit tabs, a notification panel that jumps to the latest
  unread,
  and desktop notifications. Claude's `PushNotification` tool is bridged via PostToolUse (`CM/README.md`
  L29-42;
  agent-hooks.md L71).
- **What goes wrong:**
  - #2077 notifications fire on every Claude turn, not just task completion;
  - #43180 turns that end with background work running never post a notification;
  - #12928 the sidebar shows "Running" and "Needs input" at once (0.64.25);
  - #46102 auto-approved Grok tools set "Needs input";
  - #2910 wrong "Needs Input" title after compaction;
  - #1004 desktop notifications are not suppressed when the window is focused;
  - #40686 orphaned background daemons spawn unthrottled loops.
  - cmux added a 3-second fallback because Claude turns can end without a Stop hook (Esc interrupt, Stop-hook
    timeout): with no hook for 3 s after the 7501 `working` record ends, the row is marked Idle
    (program-status.md
    last section).
- **Daemon/status.** The app is the server (socket API; `CM/docs/cli-contract.md`). Status is **structured**
  (hooks
  + OSC 7501), with an explicit fallback for missed hooks.

### 7b. workmux (raine) — tmux/Zellij worktree-per-window with agent status

- **Discovery.**
  - Agent state files live at `$XDG_STATE_HOME/workmux/agents/{backend}__{instance}__{pane_id}.json`, keyed by
    multiplexer pane id (`WM/src/state/store.rs` L20-31).
  - tmux user options are also used: `@workmux_pane_status`, `@workmux_token`, `@workmux_sidebar_daemon_pid`
    (`WM/src/multiplexer/tmux.rs` L33-49, L551-555).
  - Before use, the store checks liveness with `validate_agent_alive` (`store.rs` L1541).
- **Streaming.** Agent hooks call the CLI:
  - Claude plugin: `SessionStart` → `workmux register-agent`; `UserPromptSubmit`/`PostToolUse` → working;
    `Notification` (permission_prompt|elicitation_dialog) → waiting; `Stop` → done
    (`WM/.claude-plugin/plugin.json`).
  - Dashboard timing: tick **250 ms**, refresh **2 s**, preview refresh 500 ms (100 ms while typing)
    (`WM/src/command/dashboard/mod.rs` L209-216).
- **Rendering.** Icons in tmux `window-status-format` (🤖 working, 💬 waiting, ✅ done, which auto-clears on
  focus)
  plus a ratatui 0.30 dashboard (`WM/Cargo.toml` L54; `WM/docs/src/content/docs/guide/status-tracking.mdx`).
- **Completion.** ✅ in the tab until focus; dashboard sort order Waiting > Done > Working > Stale
  (docs/guide/dashboard/index.mdx).
  - "Stale" means no activity for a fixed threshold. Done/Working/Waiting never go stale unless interrupted
    (`WM/src/agent_staleness.rs`); #298 notes the threshold is fixed at 1 hour.
- **What goes wrong:**
  - #209 reconciliation deletes agent state when a tmux query *fails*, not only when the pane is gone;
  - #304 a Codex shared app-server daemon passes an inherited `TMUX_PANE`, so status is written to another
    pane;
  - #252 the cwd fallback misroutes without `TMUX_PANE` (the issue asks for "refuse-to-guess");
  - #366 the sidebar daemon loses its pid option after a tmux server restart;
  - #262 Codex auto-approval flashes waiting.
- **Daemon/status.** No core daemon; tmux holds the processes, plus an optional sidebar daemon. Status is
  **structured** (hooks), with no screen scraping for state.

### 7c. agent-deck (asheshgoplani) — tmux-substrate session manager (TUI + web)

- **Discovery.**
  - Every session is a tmux session `agentdeck_*` (`AD/docs/architecture.md` "tmux is the substrate").
  - Session metadata lives in per-profile **SQLite `state.db`**.
  - Hook handlers write `hooks/<instance-id>.json`, and the TUI watches those files with fsnotify
    (`AD/internal/session/hook_watcher.go`, per architecture.md).
- **Streaming.** Two layers (`AD/docs/status-detection.md`):
  - Pane frame: `capture-pane`, through a tmux control-mode pipe when connected, classified by a pure function
    `ClassifyPaneFrame(tool, frame)` scored against a **golden corpus** of captured panes
    (`internal/tmux/testdata/status_corpus`).
  - Instance layer: hooks win while fresh (Claude hooks "fresh for 2 min"; Codex 20 s/5 s), and a flip away
    from
    running is debounced by one sample.
  - Status polling starts at **2 s** and backs off to 10 s (`AD/internal/ui/home.go` L1634-1635); remote
    sessions
    are polled every 15 s.
- **Rendering.** Bubble Tea v1.3.10 TUI (`AD/go.mod` L11), plus a web UI on `127.0.0.1:8420` with a terminal
  websocket (architecture.md "Web").
- **Completion.** A `notify-daemon` (`session.TransitionDaemon`) turns status transitions into **parent
  notifications written to `inboxes/`**, wakes "conductors" and raises desktop notifications (architecture.md
  L59-72).
  The Stop hook can deliberately block Claude's turn end to inject inbox messages
  (`AD/docs/claude-status-hooks.md`).
- **What goes wrong:**
  - #1550 concurrent TUIs silently delete each other's sessions (a stale full-table DELETE-NOT-IN sweep);
  - #2283 the hooks dir is never pruned (1,430+ fds per TUI);
  - #2649 mcp-proxy processes orphaned with ppid 1;
  - #2806 an idle child that dies is never reported to its parent;
  - #2792 a false 'waiting' completion notice is sent to the parent;
  - #2900 some Codex turn ends produce no running→waiting transition, so the parent never wakes;
  - #610 CLI status disagrees with TUI/web;
  - #1867 status misread as idle when the tmux client has no UTF-8 locale.
  - The status-detection doc itself catalogues edge cases: background workflows, a stale "Waiting for N
    background
    agents" line, and long-lived background shells that "can outlive the work by hours".
- **Daemon/status.** tmux holds the processes. There is an optional notify-daemon and an optional core daemon
  on a
  Unix socket (`AD/docs/daemon-protocol.md`). Status is **hybrid**: hooks first, screen scraping as fallback,
  with a
  corpus-tested classifier.

### 7d. ccmanager (kbwo) — self-contained pty session manager (no tmux)

- **Discovery.** Sessions are in-process children, using a Bun terminal / pty plus an `@xterm/headless`
  emulator per
  session (`CC/package.json` L71-74; `CC/src/services/bunTerminal.ts`).
  - Records are written synchronously on every change to `sessions.json`, so "a crash, a `kill -9`, or a
    closed
    terminal still leaves an accurate record behind". The file is re-read before each write for multi-instance
    safety
    (`CC/src/services/sessionRestoreStore.ts` header).
- **Streaming.** A `setInterval` of **100 ms** per session runs a per-agent state detector on the headless
  xterm
  buffer (`CC/src/services/sessionManager.ts` L19, L694-757; `CC/src/services/stateDetector/*.ts`).
  - Claude idle is debounced by `IDLE_DEBOUNCE_MS = 1500`, with the comment "Claude Code sometimes appears
    idle in
    terminal output while still actively processing" (`CC/src/services/stateDetector/claude.ts` L17-50).
  - Busy detection only inspects the block above the prompt box, because cursor-addressed redraws leave
    fragments in
    the buffer (same file, L75-80).
- **Rendering.** Ink 6 (React for CLIs) (`CC/package.json` L74).
- **Completion.** User-configured **status-change hooks** (shell commands per idle/busy/waiting, with
  `CCMANAGER_NEW_STATE`, `CCMANAGER_WORKTREE_PATH`, etc.) for noti, Slack and so on
  (`CC/docs/status-hooks.md`).
- **What goes wrong:**
  - #227 shows idle while Claude is busy;
  - #113/#117 the detection strategy had to be rewritten for new Claude Code versions;
  - #84 the same for new Codex versions;
  - #73 resizing while busy triggers the idle hook;
  - #26 a crash that left ccmanager unable to start.
  - Restore re-runs the launch command; output and conversation are not restored (README L264-270). Children
    die
    with the ccmanager process: no daemon or tmux, so I infer they don't survive, **unverified** at runtime.
- **Daemon/status.** No daemon. Status is **screen scraping** on an in-process terminal emulator.

---

## Comparison table

| Tool | Version | Discovery | Streaming | Rendering | Completion | Daemon? | Known failure (issue) |
|---|---|---|---|---|---|---|---|
| herdr TUI | 0.9.5 | owns ptys; foreground process group + agent name list | manifest match on live bottom buffer every 300/500 ms; integration reports over unix socket; `events.subscribe` | ratatui + crossterm, multiplexer | toast / OSC terminal notif / OS notif / sound; `agent wait` | yes (headless server) | blocked dialogs read as idle/done (#5593, #6063, #4573) |
| herdr-core `hc` | 0.1.21 | terminal records + holder procs; parent/child tree | JSON-RPC events with `seq`, 15 s heartbeat, `missed`+snapshot | none ("draws nothing") | parent notes on child attention/end; `hc wait` | yes | finished-agent wait reported wrongly before 0.1.21 (#221); status absent unless program speaks OSC 7501 |
| claude-squad | 1.0.20 | tmux `claudesquad_*` + `instances.json` | `capture-pane` every 500 ms, hash diff | Bubble Tea, alt-screen | row status only | optional AutoYes daemon (1 s, pid file) | git-lock race with 500 ms loop corrupts instance (#284) |
| Conductor | 0.98.0 | worktrees under `~/conductor/workspaces` (internals unverified) | API: poll status ~15 s; internal unverified | native Mac GUI | completion sound; iOS app | background service (unverified details) | chats stuck "working" (0.95.0 fix); first message lost on quit |
| omnara legacy | 2025-10 tree | wrapper registers instance in Postgres; JSONL path poll 0.5 s | JSONL tail → API; heartbeat 30 s; LISTEN/NOTIFY → SSE; 60 s online TTL | web + mobile | Expo push, SMS | no local daemon; SaaS | notification spam / replay on resume (#81, #145, #215) |
| omnara current | omnarad 0.2.27 | agents = event logs; machines via `omnarad` | SSE with `sequence` id, 10 s heartbeat, Last-Event-ID | web dashboard, CLI | webhooks, Slack/Discord | server + machine daemon | SSE drops at ~16 s, resume misses deltas (#502) |
| vibe-kanban | 0.1.46 (sunsetting) | SQLite `execution_processes` + in-memory child map (no pid) | stream-json stdout → MsgStore → WebSocket; `try_wait` 250 ms | React/Vite web, Tauri | sound + OS push | yes (local server) | Claude run stuck "running" forever (#2495); restart marks rows failed without touching processes |
| claude_code_agent_farm | no releases | one tmux session, pane per agent; heartbeat files | `capture-pane` every 10 s, substring match | rich `Live` 1 Hz in tmux window | none (auto-restart, heartbeat >120 s) | no | `~/.claude` corruption on concurrent launch (CHANGELOG) |
| cmux | 0.65.2 | own panes; hook session JSON with pid | Claude/agent hooks over socket; OSC 9/99/777 + OSC 7501 | Swift/AppKit + libghostty | rings, panel, desktop notif | app is server | notif on every turn (#2077); none when background work runs (#43180) |
| workmux | 0.1.284 | per-pane JSON in XDG state; tmux options | hooks → CLI; dashboard refresh 2 s | tmux tab icons + ratatui dashboard | ✅ until focus; stale after 1 h | no (optional sidebar daemon) | state deleted when a tmux query fails (#209); wrong-pane writes (#304) |
| agent-deck | 1.16.39 | tmux `agentdeck_*` + SQLite + hook files | hooks (fresh 2 min) > corpus-tested pane classifier; poll 2→10 s | Bubble Tea + web UI | notify-daemon → parent inbox + desktop | optional notify/core daemons | concurrent TUIs delete each other's sessions (#1550) |
| ccmanager | 4.4.11 | in-process ptys + `sessions.json` written per change | headless xterm scan every 100 ms, 1.5 s idle debounce | Ink | user status-change hooks | no | idle while busy (#227); resize fires idle hook (#73) |

---

## Patterns worth copying

1. **Get status from the agent, not the screen.** The tools that scrape (herdr TUI, claude-squad, agent farm,
   ccmanager, legacy omnara) all have open mis-detection bugs that track each new agent release. The
   structured ones
   (vibe-kanban's stream-json, herdr-core's OSC 7501, omnara's event log, workmux/cmux hooks) do not have that
   class
   of bug.
   - For `claude -p --output-format stream-json`, the `result` line is the completion signal.
   - For `opencode run`, its JSON event output or plugin bus is (the opencode output-format specifics are
     **unverified** here).
2. **Durable registry row written at launch, before the child runs.** Write it atomically (tmp + rename: agent
   farm
   state file, herdr save files #5150) and synchronously per change (ccmanager). Store **pid + process start
   time +
   pgid**, so a restarted table can tell a live run from a recycled pid. vibe-kanban's lack of a pid is the
   counter-example.
3. **Explicit terminal reasons.** herdr-core's `exited` / `killed` / `server_stop` / `host_shutdown` /
   `holder_lost`, plus exit code and signal. A row is never just "gone".
4. **Snapshot + monotonic `seq` + heartbeat + "missed ⇒ re-snapshot".** herdr-core and omnara use this for any
   live
   view. Subscribe first, then read.
5. **Acknowledged "done".** Done/error stay visible until seen (cmux, workmux, herdr-core 7501 semantics).
   Working
   and blocked clear on process exit.
6. **Freshness windows for every signal**, plus a fallback when the terminal event never arrives. cmux marks
   Idle
   after 3 s without a hook; agent-deck lets hooks win only while fresh (2 min). This covers Esc-interrupts
   and
   timed-out Stop hooks.
7. **Notify the parent agent, not just the human.** Examples: herdr-core parent notes, `hc wait --children`,
   agent-deck inboxes, Conductor 0.95.0 "wait for its reply". Respect a quiet period before delivery
   (herdr-core `notes_quiet` 3 s).
8. **Corpus-test any heuristic you keep.** agent-deck scores its pane classifier against captured golden
   frames, and
   herdr offers `herdr agent explain` to show why a state was chosen.
9. **Serialise agent start-up when agents share a config directory.** Agent farm's lock + stagger was needed
   to stop
   `~/.claude` corruption.

## Mistakes to avoid

1. **Treating "output stopped changing" as idle or done** (claude-squad hash diff, omnara's 3.5 s "esc to
   interrupt"
   timer, ccmanager's debounce). It flickers on redraws, misses long silent tool calls, and spams
   notifications
   (omnara #81).
2. **Assuming a headless agent exits when its task ends.** vibe-kanban #2495: stream-json input keeps Claude
   alive,
   so a supervisor waiting only on process exit shows "running" forever.
3. **Marking DB rows failed on restart without checking the process.** That gives false "failed" rows plus
   untracked
   survivors (vibe-kanban `cleanup_orphan_executions`).
4. **Deleting registry state when a probe fails.** A failed query is not "gone" (workmux #209). A whole-table
   reconcile from one process deletes other writers' rows (agent-deck #1550).
5. **Routing status by inherited environment or cwd.** Pane ids leak into child daemons (workmux #304, #252;
   herdr
   #216 stale `HERDR_PANE_ID`). Pass an explicit run id and refuse to guess.
6. **Replaying history as fresh events.** Resume re-sent thousands of notifications in omnara #215; herdr
   #1270 is
   similar. Notify only on transitions observed live.
7. **Heavy work inside the refresh loop.** claude-squad runs `git add -N .` every 500 ms (#284 lock
   contention,
   #215 latency). Keep the table refresh cheap and do git stats on a slower cadence.
8. **Unbounded per-run artefacts.** Hook files never pruned (agent-deck #2283) and worktrees never cleaned
   (vibe-kanban #765). Give each run a cleanup path tied to its terminal state.
9. **Trusting an early `idle`.** A queued prompt reports idle before it starts (Conductor API docs). Require
   `working` to have been seen, or check the transcript.
