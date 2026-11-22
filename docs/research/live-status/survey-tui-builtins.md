# Survey: TUI process tables (k9s, lazygit, btop) and agent built-ins (Claude Code, opencode)

Date: 2026-11-22. Method: shallow clones of release tags (grep and read), official docs fetched live, `gh`
issue search,
local commands (`claude --version`/`--help`, `claude agents --json`, `opencode --help`, `env`). Every claim
links to its
source. "unverified" means no primary source or local command was checked for it. "not found" means the source
has
nothing on it.

Provenance: written by a survey session for the live-status design ([README.md](README.md)) and copied here
unchanged apart from this note. The design session re-checked the local versions (`claude --version` 2.1.330,
`opencode --version` 1.18.35) and the names of the `CLAUDE*` variables in a Claude Code Bash child (`env`);
the
linked sources were not re-fetched.

## Versions looked at

| Item | Version / commit | Date |
|---|---|---|
| k9s | v0.51.0 `558caafe7ba0` (latest release) | 2026-06-06 |
| lazygit | v0.66.3 `e0fbfd50c36e` (latest) | 2026-11-07 |
| btop | v1.4.8 `cf1da5900b1e` (latest) | 2026-10-25 |
| tcell (lazygit dep) | v3.6.0 `4bed18057fce` | 2026-10-11 |
| k9s UI deps | `derailed/tview v0.8.5`, `derailed/tcell/v2 v2.3.1-rc.4` (k9s go.mod) | - |
| Spectre.Console | 0.57.2 `bbbb5729dde2` (latest stable tag; NuGet also has 0.57.3-alpha.*) | - |
| Terminal.Gui | v2.6.0 (NuGet 2.6.0, GitHub release) | 2026-11-21 |
| Claude Code | local `2.1.330 (Claude Code)`; CHANGELOG top = 2.1.330 | 2026-11-22 |
| opencode | local `1.18.35`; latest releases v2.3.2 (`c0ad18b402eb`, 2026-11-18) and v1.19.6 (`f4e363c8295f`, 2026-11-17). The repo is now `anomalyco/opencode` (`gh api repos/sst/opencode` resolves to it). Two lines are live: 1.x (what is installed here) and 2.x | - |

Note: the local opencode (1.18.35) is behind both live lines. The 2.x line changes the event model, plugin API
and
shell env (details in B2). A .NET tool that parses `opencode run --format json` has to handle both.

---

# PART A: terminal-UI process tables

## A1. k9s (v0.51.0)

- **Refresh model: watch plus timer.** Data comes from Kubernetes dynamic shared informers, a watch-fed local
  cache
  ([internal/watch/factory.go#L30-L41](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/watch/factory.go#L30-L41)).
  The table model re-reads that cache on a timer: `updater()` loops on `time.After(rate)` with exponential
  backoff on
  errors
  ([internal/model/table.go#L203-L227](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/model/table.go#L203-L227)).
  - Default model rate is `2 * time.Second`
    ([table.go#L58](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/model/table.go#L58)).
  - The config default is `defaultRefreshRate = 2`
    ([internal/config/types.go#L7](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/config/types.go#L7)).
  - `GetRefreshRate()` raises any lower value to that minimum and logs a warning
    ([internal/config/k9s.go#L396-L415](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/config/k9s.go#L396-L415)).
  - A refresh still in flight makes the next one a no-op ("Dropping update...", CAS on `inUpdate`)
    ([table.go#L229-L246](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/model/table.go#L229-L246)).
- **Changed rows are highlighted.** `NewDeltaRow` computes per-cell differences between the old and new row
  ([internal/model1/delta.go#L8-L27](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/model1/delta.go)).
- **Rendering: tview Table on tcell.** UI changes are marshalled to the UI goroutine through `QueueUpdateDraw`
  ([internal/ui/app.go#L74-L80](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/ui/app.go#L74-L80)).
  - Flicker avoidance is tcell's: a cell buffer with dirty tracking, one buffered write per frame, cursor
    hidden while
    drawing (see the tcell notes under A2).
  - Alt screen: tcell enters it by default (`altScreen: true`, `enterCA = "\x1b[?1049h"`) ([tcell
    tscreen.go#L184-L185,
    #L208-L213](https://github.com/gdamore/tcell/blob/4bed18057fce88934c9b9a576b5f83632196de11/tscreen.go#L184)).
  - That was checked on upstream tcell v3; k9s uses the derailed v2 fork, whose sources were not checked
    (**unverified for the fork**).
  - Resize is handled by tcell/tview (EventResize, then relayout). Not traced in k9s code (**unverified**).
- **Keybindings: modal, k9s-style.**
  - `:` opens command mode (resource switch)
    ([internal/ui/app.go#L146](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/ui/app.go#L146)).
  - `/` opens filter mode, `?` shows help, Space marks a row, Ctrl+Space marks a range
    ([internal/view/table.go#L225-L240](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/view/table.go#L225-L240)).
  - Shift+letter sorts by a column: Shift-N name, Shift-A age, Shift-S status, Shift-O the selected column
    (same lines).
  - Ctrl+W toggles "wide" columns, Ctrl+Z toggles faults only, Ctrl+S saves.
  - Per-resource single-key actions on the selected row (for example describe, logs, delete) are bound per
    view (not enumerated here).
- **Screen real estate.**
  - Columns flagged `Wide` stay hidden until Ctrl+W ([internal/model1/header.go#L21,
    #L45](https://github.com/derailed/k9s/blob/558caafe7ba067467de46b320cc22ef11fef9c34/internal/model1/header.go#L21)).
    So "dropping columns" is a manual toggle, not width-adaptive. No automatic width-based dropping was found.
  - Vertical scrolling and selection come from tview's Table. The other tools are filter `/`, sort and Ctrl+Z
    (faults only).

## A2. lazygit (v0.66.3)

- **Refresh model: timers plus explicit refresh after its own actions.** `startBackgroundRoutines` starts
  goroutines on `goEvery(interval)`
  ([pkg/gui/background.go#L42-L80,
  #L150-L175](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/gui/background.go#L42)).
  - Files refresh every `refreshInterval: 10` s, fetch every `fetchInterval: 60` s, and external change
    detection every
    `externalChangeCheckInterval: 2` s
    ([pkg/config/user_config.go#L1001-L1003](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/config/user_config.go#L1001)).
  - Background refreshes use `RefreshFromWorker(... Background: true)`. No fsnotify watcher was found in
    `pkg/gui`.
- **Rendering: an in-tree gocui fork (`pkg/gocui`, a fork of awesome-gocui) on `gdamore/tcell/v3 v3.6.0`.**
  - Full flush: `flush()` runs the layout managers, draws every view, then calls `Screen.Show()`
    ([pkg/gocui/gui.go#L1590-L1618](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/gocui/gui.go#L1590)).
  - Partial flush: `flushContentOnly()` redraws only dirty views and skips layout. Its comment: "tcell's
    cell-level dirty
    tracking ensures only actually-changed cells are emitted"
    ([gui.go#L1620-L1636](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/gocui/gui.go#L1620)).
  - Resize: `flush()` detects the size change and rewraps all views. `onResize()` is empty, with `Sync()`
    commented out
    ([gui.go#L1307-L1310](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/gocui/gui.go#L1307)).
  - tcell v3 `draw()` resets a buffer, hides the cursor, writes only dirty cells, then makes one
    `buf.WriteTo(tty)` ([tcell
    tscreen.go#L1043-L1100](https://github.com/gdamore/tcell/blob/4bed18057fce88934c9b9a576b5f83632196de11/tscreen.go#L1043)).
  - Synchronized output (`?2026`): **not found** in tcell v3.6.0 sources (grep).
  - Alt screen: `\x1b[?1049h`, on by default (same file, L184 and L208-L213).
- **Keybindings: panel-focused, single keys act on the selected item.** Defaults
  ([user_config.go#L1015-L1100](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/config/user_config.go#L1015)):
  - `q` quit, `<esc>` return, `?` option/keybinding menu, `/` search.
  - `:` runs a shell command (not a command palette).
  - `<space>` select, `d` remove, `e` edit, `,`/`.` page, `<`/`>` top/bottom, `+` next screen mode, `<ctrl+s>`
    filtering menu, `@` extras.
- **Screen real estate.**
  - Screen modes (normal/half/full) cycle with `+`
    ([pkg/gui/types/common.go#L486-L489](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/gui/types/common.go#L486)).
  - Portrait mode switches on automatically when the terminal is narrow and tall
    (`PortraitModeAutoMaxWidth`/`MinHeight`)
    ([window_arrangement_helper.go#L120-L140](https://github.com/jesseduffield/lazygit/blob/e0fbfd50c36e1ef88c52297ca34750e640b4f5f4/pkg/gui/controllers/helpers/window_arrangement_helper.go#L120)).
  - Lists scroll inside panels.

## A3. btop (v1.4.8): hand-rolled ANSI

- **Refresh model: a pure timer.** `update_ms` defaults to 2000
  ([src/btop_config.cpp#L385](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_config.cpp#L385)),
  and the docs line recommends 2000 ms or more
  ([#L123](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_config.cpp#L123)).
  A collector thread builds one output string per box, and the main loop writes it.
- **The sequences, all hard-coded**
  ([src/btop_tools.cpp#L757-L769](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_tools.cpp#L757)):
  - `hide_cursor = ESC[?25l`, `show_cursor = ESC[?25h`
  - `alt_screen = ESC[?1049h`, `normal_screen = ESC[?1049l`
  - `clear = ESC[2J ESC[0;0f`, `clear_end = ESC[0J`, `clear_begin = ESC[1J`
  - `mouse_on = ESC[?1002h ESC[?1015h ESC[?1006h` (and the matching `l` sequences to turn it off)
  - `sync_start = ESC[?2026h`, `sync_end = ESC[?2026l` ("terminal synchronized output")
  - Cursor positioning is absolute, `Mv::to(line,col) = ESC[<l>;<c>f`, plus relative `ESC[nC/D/A/B`
    ([src/btop_tools.hpp#L105-L119](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_tools.hpp#L105)).
- **Flicker avoidance.**
  - No clear per frame: each box string positions the cursor absolutely and overwrites cells.
  - Static frames are drawn only when `redraw` is true, on start, resize or a forced redraw
    ([src/btop_draw.cpp#L572-L617](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_draw.cpp#L572)).
  - The whole frame goes out in one `cout <<` wrapped in `?2026h ... ?2026l` when `terminal_sync` is on
    (default `true`)
    ([src/btop.cpp#L720-L724](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop.cpp#L720),
    [btop_config.cpp#L378](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_config.cpp#L378)).
  - `cout.sync_with_stdio(false)` and `cout.tie(nullptr)` keep output buffered.
- **TTY setup and teardown.**
  - `Term::init()` runs only if `isatty(STDIN_FILENO)`. It turns off echo and ICANON, enters the alt screen,
    hides the cursor and sets the mouse mode.
  - `restore()` restores termios and writes mouse off, clear, reset, `normal_screen`, `show_cursor`
    ([btop_tools.cpp#L150-L182](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_tools.cpp#L150)).
  - Size comes from `ioctl(TIOCGWINSZ)` on stdout, falling back to `/dev/tty` (L96-L100).
- **Resize.**
  - A SIGWINCH handler ([src/btop.cpp#L310,
    #L1052](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop.cpp#L1052))
    triggers a full recalculation and redraw.
  - The loop also re-checks the size "in case of SIGWINCH not working properly" (L1133).
  - Below the minimum size btop shows a "Terminal size too small" screen (L158-L170).
- **Keybindings: single keys, no command mode**
  ([src/btop_input.cpp](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_input.cpp)).
  - `q` quit, `f` or `/` filter (L341), `e` tree, `r` reverse, left/right change the sort column.
  - `k` (or `K` with vim keys) kills the selected process (L220), Enter opens detail.
  - Escape-sequence parsing is hand-rolled (`[A`, `[5~` and so on, L47-L68).
  - Default sort is `proc_sorting = "cpu lazy"`, with `proc_reversed=false` and `proc_tree=false`
    (btop_config.cpp#L284, L321-L322).
- **Screen real estate: width-adaptive columns**
  ([src/btop_draw.cpp#L1807-L1811](https://github.com/aristocratos/btop/blob/cf1da5900b1e9eaa1c20fd7148dd812b43340b8a/src/btop_draw.cpp#L1807)).
  - `user_size = width<75 ? 5 : 10`.
  - `thread_size = width<75 ? -1 : 4`, where -1 means the column is dropped.
  - `prog_size` is 16, 8 or the remainder, by width.
  - `cmd_size` is -1 (dropped) at width 55 or less.
  - Strings are cut with `uresize`/`luresize` (Unicode-aware truncation). The process list scrolls with the
    selection.

## A4. A minimal hand-rolled equivalent

**Smallest honest set for an in-place table redraw.** A in the Source column is section A3 (btop), B is
section A2 (tcell).

| Purpose | Sequence | Source |
|---|---|---|
| Enter or leave the alt screen (optional; otherwise redraw inline) | `ESC[?1049h` / `ESC[?1049l` | A, B |
| Hide or show the cursor during the frame | `ESC[?25l` / `ESC[?25h` | A |
| Home the cursor | `ESC[H` (btop uses `ESC[0;0f`) | B `clear`, A |
| Overwrite in place, then erase the tail of each line | write the row text, then `ESC[K` (EL 0) | standard ECMA-48; not in btop's constant list (**unverified** reference) |
| Erase leftover rows when the table shrinks | `ESC[J` (ED 0) after the last row | A `clear_end`, B |
| Atomic frame on supporting terminals | `ESC[?2026h` ... `ESC[?2026l` | A |
| Inline variant (no alt screen) | `\r` then `ESC[<n>A` (CUU) to the top of the previous frame, rewrite, `ESC[2K` per line on clear | Spectre `LiveRenderable` ([L40-L96](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/Live/LiveRenderable.cs#L40)) |

Rules the three tools follow:
1. One buffered write per frame.
2. Never clear the full screen per frame.
3. Truncate every cell to the column width, so no line ever wraps. A wrap breaks the CUU arithmetic.
4. Recompute layout on SIGWINCH.
5. Restore the terminal on every exit path.

**What breaks when stdout is not a TTY:**
- Escape codes land in logs as garbage.
- CUU/ED have no meaning, so frames pile up.
- The size query fails (`TIOCGWINSZ` on a pipe; btop falls back to `/dev/tty`).
- No raw input is possible: btop simply does not init (`isatty` check).
- Spectre gates every control code on `Capabilities.Ansi`
  ([AnsiWriter.cs#L78ff](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console.Ansi/AnsiWriter.cs#L78))
  and detects redirection from `Console.IsOutputRedirected`
  ([AnsiConsoleOutput.cs#L16-L18](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/AnsiConsoleOutput.cs#L16),
  [InteractionDetector.cs](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/InteractionDetector.cs)).
- Fallback behaviour differs between Spectre widgets:
  - `Progress` switches to a `FallbackProgressRenderer` when `!(Interactive && Ansi)`
    ([Progress.cs#L162-L175](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/Live/Progress/Progress.cs#L162)).
  - `LiveDisplay` has no such fallback in the sources read. Inferred, not run: redirected, it appends every
    refresh as a full new frame.

A safe policy: render the live table only when `!Console.IsOutputRedirected` (and stdin is a TTY for keys).
Otherwise
print one line per state change, or one snapshot and exit (`--once`/`--json`). Resize:
`PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ...)`
exists in .NET 6+ ([PosixSignal
docs](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.posixsignal)).
What `Console.WindowWidth` does on a pipe is **unverified**; poll it per frame as a backstop, as btop does.

## A5. .NET options

| Option | Version | Dependencies (nuspec) | AOT / trim | Live table technique | Redirected output |
|---|---|---|---|---|---|
| Spectre.Console | 0.57.2 (nupkg 2.24 MB) | `Spectre.Console.Ansi`, `Microsoft.Bcl.TimeProvider`, `System.Memory`; TFMs net8/9/10 and netstandard2.0 | `<IsAotCompatible>true` for non-netstandard TFMs ([Spectre.Console.csproj#L7](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/Spectre.Console.csproj#L7)) | `AnsiConsole.Live(table)`: inline, no alt screen. Each refresh does `\r` + CUU(height-1), rewrites the whole renderable and hides the cursor. When the terminal shrinks it clears the display and scrollback (`ED 2`, `ClearScrollback`, home) ([LiveRenderable.cs#L40-L71](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/Live/LiveRenderable.cs#L40)). Vertical overflow defaults to `Ellipsis`, cropping `Top` ([LiveDisplay.cs#L21-L26](https://github.com/spectreconsole/spectre.console/blob/bbbb5729dde27b58deee44f447a788eea46ee451/src/Spectre.Console/Live/LiveDisplay.cs#L21)). No key input or selection | Control codes are dropped when ANSI is off. Live has no fallback renderer (see A4) |
| Terminal.Gui | 2.6.0 (nupkg 1.46 MB, net10.0 only) | ColorHelper, Markdig, TextMateSharp(+Grammars), Wcwidth, System.IO.Abstractions, Microsoft.Extensions.Configuration(.Json/.Binder)/Options/Logging.Abstractions 11.0.0, JetBrains.Annotations | `<IsTrimmable>true`, `<IsAotCompatible>true` ([csproj@v2.6.0](https://github.com/gui-cs/Terminal.Gui/blob/v2.6.0/Terminal.Gui/Terminal.Gui.csproj)); the repo has `Tests/NativeAotSmoke` | Full TUI (TableView, key bindings, alt screen; not traced in source) | **unverified** (not tested) |
| System.Console + ANSI by hand | BCL | none | AOT-safe | The A4 sequence set | Under your control |

For a "runs table" that must also behave in a Claude Code Bash tool (stdout is a pipe), hand-rolled ANSI is
the
lightest option with a clean non-TTY fallback. Spectre is a reasonable middle ground if the code takes the
fallback
path itself. Terminal.Gui is heavy for this.

---

# PART B: the built-ins

## B1. Claude Code (local 2.1.330)

**Discovery: how the session finds and lists its children.**
- Background shells: the Bash tool's `run_in_background: true` makes a background task. You list and stop them
  with `/tasks` ([tools-reference: Background
  commands](https://code.claude.com/docs/en/tools-reference#background-commands)).
- Background subagents: by default, a subagent spawned from the main conversation starts in the background
  ([sub-agents](https://code.claude.com/docs/en/sub-agents)).
- Changelog 2.1.329: `/tasks` and the footer's background count became one number covering shells, monitors,
  MCP calls, agents, workflows and scheduled tasks
  ([CHANGELOG](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md)).
- Background sessions: `claude --bg`, `claude agents`, `attach`, `logs`, `stop`, `rm`, `respawn` (local
  `claude --help`).
  - `claude agents --json` is documented as "the supported way to read session state from outside Claude
    Code". The files under `~/.claude/jobs/<id>/` "are not a stable interface"
    ([agent-view](https://code.claude.com/docs/en/agent-view)).
  - Fields: `cwd`, `kind` (`interactive`/`background`), `startedAt`; background rows add `id` and `state`
    (`working|blocked|done|failed|stopped`); live rows add `pid` and `status` (`busy|waiting|idle`);
    `waitingFor` appears when status is waiting; `sessionId` and `name` appear when set.
  - Confirmed locally: running `claude agents --json` returned exactly these keys. Background rows carry
    `state` and no `pid`; interactive rows carry `pid` and `status`.
- Supervisor: background sessions run under a supervisor service (`~/.claude/daemon/roster.json`,
  `~/.claude/jobs/<id>/state.json`) ([agent-view](https://code.claude.com/docs/en/agent-view)).
- Session identity in children:
  - `CLAUDE_CODE_SESSION_ID` is set in Bash, PowerShell and hook subprocesses and in stdio MCP servers. It
    matches the hook `session_id`, updates on `/clear`, and may hold the startup ID after `--continue` or
    `--resume` without an ID ([env-vars](https://code.claude.com/docs/en/env-vars)).
  - `CLAUDECODE=1` is set in all Claude Code subprocesses, and IDE terminals set it too.
  - `CLAUDE_CODE_CHILD_SESSION=1` is set only for direct Bash, PowerShell, Monitor, hook and statusline
    children.
  - `CLAUDE_PID` is the Claude Code process id (v2.1.214+).
  - `CLAUDE_CODE_MESSAGING_SOCKET` is the session's inbox socket for cross-session messages (v2.1.224+).
  - `CLAUDE_ENV_FILE` is documented ([env-vars](https://code.claude.com/docs/en/env-vars)).
  - Observed locally but **not found in the docs**: `CLAUDE_CODE_CHILD_NOTES_DIR` (points to
    `<scratch>/<session>/started`), `CLAUDE_CODE_SESSION_ATTENDED=1`, `CLAUDE_CODE_ENTRYPOINT=cli`,
    `AI_AGENT=claude-code_2-1-330_agent`. Treat them as undocumented.
  - The `CLAUDE_SESSION_ID` env var: **not found** (not in the docs, not in the local env).

**Streaming: per-task and per-turn state.**
- Headless `claude -p --output-format stream-json` (needs `--verbose` for full streaming;
  `--include-partial-messages` adds token deltas) ([headless](https://code.claude.com/docs/en/headless)).
- First event: `system/init` (model, tools, `mcp_servers`, `plugins`, `capabilities`). Other events:
  `system/api_retry` (`attempt`, `max_retries`, `retry_delay_ms`, `error_status`, `error`), and
  `assistant`/`user` messages with `parent_tool_use_id` for subagents.
- The last line is `result`. Fields from
  [SDKResultMessage](https://code.claude.com/docs/en/agent-sdk/typescript#sdkresultmessage):
  - Identity and outcome: `type:"result"`, `subtype` (`success | error_max_turns | error_during_execution |
    error_max_budget_usd | error_max_structured_output_retries`), `is_error`, `uuid`, `session_id`.
  - Timing and counts: `duration_ms`, `duration_api_ms`, `num_turns`, `stop_reason`, `terminal_reason?`,
    `result_index?`.
  - Cost and usage: `total_cost_usd`, `usage` (main loop only), `modelUsage` (per model, including subagents;
    preferred for cost).
  - Output: `result` (success only), `errors[]` (error variants), `structured_output?`.
  - Other: `permission_denials`.
  - Docs: `total_cost_usd` is a cumulative client-side estimate, and resumed sessions include earlier spend.
- Task events in the same stream ([typescript
  ref](https://code.claude.com/docs/en/agent-sdk/typescript#sdktaskstartedmessage)):
  - `system/task_started`: `task_id`, `run_id?`, `tool_use_id?`, `description`, `task_type`
    (`local_bash|local_agent|mcp_task|remote_agent`), `is_backgrounded?`, `spawn_depth?`, `parent_task_id?`,
    `ambient?`.
  - `system/task_progress`: `usage{total_tokens,tool_uses,duration_ms}`, `last_tool_name?`, `summary?`.
  - `system/task_updated`: `patch{status: pending|running|completed|failed|killed, end_time, error, ...}`.
  - `system/task_notification`: `status: completed|failed|stopped`, `output_file`, `summary`, `usage?`,
    `reason?: worker_restart`.
  - `system/background_tasks_changed` (v2.1.203+): `tasks` is the full live set. The docs tell you to replace
    your cache with each payload rather than pair events. Nothing is emitted at startup: reset to empty when
    the CLI process (re)starts.
- Hooks (common input `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `agent_id?`, `agent_type?`)
  ([hooks](https://code.claude.com/docs/en/hooks)):
  - `Stop`: `stop_hook_active`, `last_assistant_message`, `background_tasks[]` (`id`, `type`, `status`,
    `description`, `command`, `agent_type`), `session_crons[]`. It lets a hook tell "session done" apart from
    "paused waiting on background work".
  - `SubagentStop`: adds `agent_id`, `agent_type`, `agent_transcript_path`, `last_assistant_message`.
  - `SessionEnd`: `reason` is one of `clear|resume|logout|prompt_input_exit|other`.
  - `Notification`: `message`, `title?`, `notification_type`, with types including `idle_prompt`,
    `permission_prompt`, `agent_needs_input`, `agent_completed`. The last two cover background sessions and
    fire only while agent view is open ([agent-view](https://code.claude.com/docs/en/agent-view)).
  - `TaskCreated`/`TaskCompleted` are about the task-list tool (`task_id`, `task_subject`), **not** background
    processes.
  - No hook fires when a `run_in_background` Bash command finishes: **not found** in the hooks reference.
- Monitor tool: Claude runs a script in the background and gets each stdout line as an event, or it reads a
  WebSocket. A watch has a 5-minute deadline by default, at most 30 minutes, and 10 minutes in `-p`.
  `TaskStop` cancels it ([tools-reference:
  Monitor](https://code.claude.com/docs/en/tools-reference#monitor-tool)).
- Async hooks: `"async": true` delivers `additionalContext` on the next turn, and an `asyncRewake` hook
  exiting 2 wakes an idle session ([hooks: Run hooks in the
  background](https://code.claude.com/docs/en/hooks)). That is a documented way for an external watcher to
  push a completion back into the conversation.

**Rendering.**
- `/tasks` dialog: subagent rows, `x` stops one, and Enter opens the transcript. A completed background
  subagent stays listed for 30 s, sorted below running work
  ([sub-agents](https://code.claude.com/docs/en/sub-agents)).
- Footer: shows the background count.
- Agent view (`claude agents`): rows grouped Ready for review, Needs input, Working, Completed. Each row shows
  name, activity and age. Keys: Space peek, Enter attach, Ctrl+X stop/delete, `?` help
  ([agent-view](https://code.claude.com/docs/en/agent-view)).

**Completion back into the conversation.**
- A background subagent's or shell's result arrives as a completion notification on a later turn, "marked as
  an automated event" ([sub-agents](https://code.claude.com/docs/en/sub-agents)).
- In the SDK, the turn carries `origin.kind === "task-notification"`, and Claude Code prepends a
  no-human-input notice
  ([SDKTaskNotificationMessage](https://code.claude.com/docs/en/agent-sdk/typescript#sdktasknotificationmessage)).
- Observed locally in a transcript: XML
  `<task-notification><task-id>…</task-id><tool-use-id>…</tool-use-id><output-file>…/tasks/<id>.output</output-file><status>failed</status><summary>…`.
- Background output files live under `<scratchpad-root>/<session>/tasks/<id>.output` (observed locally).

**Known failure modes (anthropics/claude-code issues; state as of 2026-11-22).**

Docs and changelog:
- Unattended or `-p` background commands have a time limit: 30 min by default (10 min in text-prompt `-p`), 2
  h max. They are stopped with "was stopped after reaching its background time limit"
  ([tools-reference](https://code.claude.com/docs/en/tools-reference#time-limit-for-background-commands)).
- `-p` stops idle background work after 10 min (`CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS`), and commands started
  by a background subagent die when the run exits ([headless](https://code.claude.com/docs/en/headless)).
- On SIGTERM, a `-p` run kills the process tree of a running Bash command and exits 143. The in-progress turn
  records no result.
- `--bare` disables background tasks entirely.
- `background_tasks_changed` is not re-sent at startup.
- `task_notification.reason: worker_restart` exists only for cloud and Remote Control sessions.

Issues:
- #20754 (closed): notifications not delivered when several agents complete at the same time.
- #25955 (closed): completion notifications persist after compaction.
- #110924 (open): notifications from before `/clear` leak into the new conversation.
- #108903 (open): a notification wipes the assistant message being streamed.
- #82235 (closed): a headless notification after `end_turn` reclassifies the turn as `error_during_execution`.
- #90534 (closed): resume auto-fires armed background shells and watches.
- #66436 (closed): a regression killed background shells about 5 s after the result.
- #35163 (closed): stale tasks persist in system reminders.
- #78304 (closed): on Windows, background processes survive app exit.

Recent fixes (CHANGELOG):
- 2.1.330: a stopped background subagent did not always stay stopped.
- 2.1.327: the main conversation was not told a background agent finished.
- 2.1.326: agent rows showed busy after the turn ended.

## B2. opencode (local 1.18.35; source read at v1.19.6 and v2.3.2)

**Discovery.**
- Subagents are child sessions: the `task` tool creates a session with `parentID = ctx.sessionID` ([1.19.6
  tool/task.ts#L136-L160](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/tool/task.ts#L136)).
- Children are found through `session.created` events with `info.parentID`. `opencode run` tracks them that
  way
  ([cli/cmd/run.ts#L689-L695](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/cli/cmd/run.ts#L689)).
- Session listing:
  - CLI: `opencode session list --format json [-n N]`. Local 1.18.35 keys are `id`, `title`, `updated`,
    `created`, `projectId`, `directory`.
  - In 2.3.2 the same fields are listed with `parentID: null`, so root sessions only
    ([cli/.../session/list.ts#L18-L45](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/cli/src/commands/handlers/session/list.ts#L18)).
  - Server: `GET /session` and `GET /session/status` in 1.x
    ([sdk.gen.ts](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/sdk/js/src/v2/gen/sdk.gen.ts)).
  - The current docs describe a v2 `/api` surface: `GET /api/session`, `GET /api/session/active`, `GET
    /api/session/{id}/message`, `POST /api/session/{id}/interrupt`, `GET /api/event` with a `types` filter.
    The docs warn "a slow consumer overflows and fails the stream"
    ([opencode.ai/docs/server](https://opencode.ai/docs/server/)).
- 2.x has a background service: `opencode service status` prints the server URL or `stopped`
  ([service/status.ts](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/cli/src/commands/handlers/service/status.ts)).
  That gives a child process a way to find the server; how a TUI uses that service in 2.x was **unverified**.
- **Session identity in children:**
  - 1.x sets only `AGENT=1`, `OPENCODE=1`, `OPENCODE_PID=<pid>` on the main process
    ([src/index.ts#L66-L78](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/index.ts#L66)).
    The shell tool passes `process.env` plus whatever a plugin's `shell.env` hook adds; that hook receives
    `sessionID`
    ([tool/shell.ts#L416-L426](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/tool/shell.ts#L416)).
    So in 1.x no session id reaches a child unless a plugin injects one.
  - 2.3.2 sets `AGENT=1`, `OPENCODE=1`, `AI_AGENT||=opencode` and **`OPENCODE_SESSION_ID=<sessionID>`** in
    shell invocations
    ([core/src/tool/plugin/shell.ts#L422-L426](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/core/src/tool/plugin/shell.ts#L422)).
  - Locally the session env shows `OPENCODE_DISABLE_*` only, because this session is Claude Code, not
    opencode.
- Shell children: 1.x spawns the shell `detached: true` on non-Windows (its own process group)
  ([shell.ts#L293-L310](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/tool/shell.ts#L293)).

**Streaming.**
- `opencode run --format json`: one JSON object per line, `{type, timestamp, sessionID, ...data}`
  ([run.ts#L668-L681](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/cli/cmd/run.ts#L668)).
  Types in 1.19.6:
  - `tool_use` (`part` = ToolPart, emitted when `state.status` is `completed` or `error`)
  - `step_start`, `step_finish`
  - `text` (when `part.time.end` is set)
  - `reasoning` (only with `--thinking`)
  - `error`
  - The loop ends on `session.status` with `status.type === "idle"` for the root session (L689-L790).
  - Only events of the root session are emitted (`part.sessionID !== sessionID` is skipped).
- `step_finish.part` (StepFinishPart): `id`, `sessionID`, `messageID`, `type:"step-finish"`, `reason`,
  `snapshot?`, `cost`, `tokens{total?, input, output, reasoning, cache{read, write}}` ([sdk v2
  types.gen.ts#L555-L573](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/sdk/js/src/v2/gen/types.gen.ts#L555)).
  `step_start.part`: `id`, `sessionID`, `messageID`, `type`, `snapshot?`.
- ToolPart: `callID`, `tool`, `state{status, input, output, title, metadata, time{start, end}}`.
- **No final result or summary event with totals in `run --format json`: not found.** A consumer has to sum
  `step_finish.cost` and `tokens`. The `Session` object does carry `cost?` and `tokens?`
  (types.gen.ts#L170-L193).
- 2.3.2 `run` keeps `step_start`, `step_finish`, `text`, `reasoning`, `tool_use` and `error`, and adds
  `background_wait` (`jobs[{id, kind, title}]`) and `retry` (`messageID`, `attempt`, `nextRetryAt`, `error`)
  ([cli/src/run/noninteractive.ts#L364-L500](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/cli/src/run/noninteractive.ts#L364)).
  The outcome comes from `session.execution.succeeded|failed|interrupted` (L316-L337).
- SSE bus, 1.x: `GET /event` starts with `server.connected`, sends `server.heartbeat` every 10 s, and stops at
  `server.instance.disposed`. `GET /global/event` wraps events as `{payload}`
  ([handlers/event.ts#L55-L80](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/server/routes/instance/httpapi/handlers/event.ts#L55)).
  Relevant event types:
  - `session.created`, `session.updated`, `session.deleted`
  - `session.status` (`status: idle | busy | retry{attempt, message, next}`)
  - `session.idle` (published together with an idle status:
    [session/status.ts#L39-L43](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/opencode/src/session/status.ts#L39))
  - `message.updated`, `message.part.updated`, `session.error`, `session.compacted`, `permission.asked`,
    `todo.updated`
- In 2.x, issue #55358 (open) says `session.idle` is declared but never emitted, and completion arrives as
  `session.execution.*`.

**Plugin hooks usable as a completion signal.**
- 1.x
  ([packages/plugin/src/index.ts](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/plugin/src/index.ts)):
  `event({event})` receives every bus event, so it can watch for `session.idle`/`session.status`. Also
  `tool.execute.before`/`after` (`tool`, `sessionID`, `callID`, `args`; output `title`, `output`, `metadata`),
  `shell.env` (`cwd`, `sessionID?`, `callID?`), `chat.message`, `permission.ask`, `command.execute.before`.
- 2.x replaces this with the V2 plugin API: `ctx.session.hook("context"|"retry"|"experimental.idle")`.
  `experimental.idle` runs just before a session goes idle and lists `event.background` (background shells and
  subagents still running). It does not run after interruption or failure
  ([packages/plugin/src/README.md#L96-L147](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/plugin/src/README.md#L96)).

**Background tasks and completion back into the conversation.**
- 1.19.6: `task` takes `background: true`, gated by `OPENCODE_EXPERIMENTAL_BACKGROUND_SUBAGENTS=true`
  (task.ts#L97-L100).
  - On completion, `inject()` sends a synthetic user prompt (`synthetic: true`) to the parent session. Its
    text is rendered as `<task id="<childSessionID>" state="completed|error"><summary>Background task
    completed: …</summary>…</task>` (task.ts#L227-L263, #L70-L78).
  - A cancelled job injects nothing (L256-L263).
  - The registry is in memory. Its own comment: "Entries are intentionally not durable: process restart or
    owner-scope closure loses status and interrupts live work"
    ([core/src/background-job.ts#L113-L118](https://github.com/anomalyco/opencode/blob/f4e363c8295fa90a03dddc451882fffe8fbce357/packages/core/src/background-job.ts#L113)).
  - Shell has no `background` parameter in 1.19.6 (grep).
- 2.3.2:
  - The shell tool has `background` ("you will be notified when it completes"). A foreground command that
    times out moves to the background, and the result carries `shellID` and a pid while it runs
    ([core/src/tool/plugin/shell.ts#L48-L100](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/core/src/tool/plugin/shell.ts#L48)).
  - Jobs are backed by a durable SQLite `job` table "until its completion notification is admitted". Teardown
    leaves the row running "for the next server"
    ([core/src/job.ts#L240-L326](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/core/src/job.ts#L240)).
  - PTYs move to a Rust `process-daemon` with owner and handoff semantics
    ([process-daemon/README.md](https://github.com/anomalyco/opencode/blob/c0ad18b402eb2827fc1ee71c171b1177b3fc02d1/packages/process-daemon/README.md)).

**Rendering.** The TUI is built on `@opentui/core`, `@opentui/solid` and `@opentui/keymap` (package.json in
both lines).
Child sessions appear as task tool parts; `session_child_first` and `session_parent` keybinds exist in the 2.x
TUI.
A dedicated "background tasks" list in the TUI: **not found**. #52016 (open) asks to stop a background
subagent from the web app.

**Known failure modes (anomalyco/opencode issues, open unless marked).**
- #62636: `opencode run` hangs if a child session is busy when the root finishes.
- #62681: `run` hangs before creating a session when the initial SSE event is buffered.
- #64851: `--format json` omits synthetic background-completion notices and completed compactions.
- #62380: `run --format json` omits every subagent event (still in 2.x).
- #55358: `session.idle` is never emitted in V2.
- #63426 (closed): idle and completion events are lost after an event-stream gap, with no resync.
- #60245: an orphaned background child keeps an admitted completion that nothing drains.
- #53564: the server as PID 1 does not reap grandchildren, so zombies leave shells stuck "running".
- #50363: orphaned MCP server processes.
- #63894: background subagent reports wake the parent for hidden turns (2.3.0).
- #36423: no cancellation for background subagents (v2).
- #64733: no default step, time or cost bound on sessions or subagents.

---

## Comparison table, Part A

| | k9s v0.51.0 | lazygit v0.66.3 | btop v1.4.8 |
|---|---|---|---|
| Refresh trigger | informer watch cache plus a 2 s timer (minimum 2 s), backoff on error, drops overlapping refreshes | timers: files 10 s, fetch 60 s, external change 2 s; explicit refresh after own actions | timer `update_ms` 2000 |
| Redraw | tview/tcell cell diff, one write per frame | full flush or `flushContentOnly` of dirty views; tcell cell diff | absolute-positioned overwrite; static frame only on redraw |
| Flicker control | tcell buffering, cursor hidden | same (tcell v3) | `?2026` sync output (default on), cursor hidden, one `cout` per frame |
| Alt screen | yes (tcell default; fork unverified) | yes (tcell `?1049h`) | yes `?1049h` |
| Resize | tcell event, relayout (unverified detail) | size check in `flush()`, rewrap | SIGWINCH plus a size re-check, "too small" screen |
| Library | derailed/tview + derailed/tcell v2 | in-tree gocui fork + tcell v3 | hand-rolled ANSI, termios |
| Command mode | `:` resource/command bar | `:` = shell command | none |
| Help / filter | `?` / `/` | `?` menu / `/` search | none / `f` or `/` |
| Row actions | single keys per view; Space marks | single keys on the selected item | `k` kill, Enter detail, `e` tree, `r` reverse |
| Sort | Shift+N/A/S/O | per panel (n/a) | left/right picks the column, `r` reverses |
| Narrow terminals | manual `Wide` column toggle (Ctrl+W) | portrait mode, screen modes | automatic column drop (Threads under 75, Command at 55 or less), Unicode truncation |

## Comparison table, Part B

| | Claude Code 2.1.330 | opencode 1.x (1.18.35 local / 1.19.6) | opencode 2.3.2 |
|---|---|---|---|
| Discover children | `/tasks`; `claude agents --json` (documented stable); stream `task_started` / `background_tasks_changed` | `session.created` with `parentID`; `GET /session`; `session list --format json` (roots) | the same plus `/api/session/active`; `opencode service status` URL |
| Session id in a child's env | `CLAUDE_CODE_SESSION_ID` (documented), `CLAUDE_PID`, `CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION` | none (`OPENCODE=1`, `OPENCODE_PID`); a plugin can add one via `shell.env` | `OPENCODE_SESSION_ID`, `OPENCODE=1`, `AI_AGENT` |
| Stream state | stream-json: `system/init`, `assistant`/`user` (`parent_tool_use_id`), `system/task_*`, `result` | `run --format json`: `step_start`, `step_finish{cost,tokens}`, `tool_use`, `text`, `reasoning`, `error`; SSE `/event` | adds `background_wait`, `retry`; `session.execution.*` |
| Per-run totals | `result`: `num_turns`, `total_cost_usd`, `usage`, `modelUsage`, `duration_ms`, `duration_api_ms` | no result event (sum `step_finish`); `Session.cost`/`tokens` | not found beyond `step_finish` |
| Completion signal | in conversation: task-notification turn; hooks: `Stop.background_tasks`, `SubagentStop`, `Notification:agent_completed` (agent view open only), async `asyncRewake`; no hook for Bash background exit | `session.status idle` / `session.idle`; plugin `event` hook; background subagent injects a synthetic `<task …>` prompt | `experimental.idle` hook with `background[]`; `session.execution.succeeded|failed|interrupted` |
| Render | `/tasks` dialog, footer count, agent view (grouped rows, peek) | TUI task parts, child-session navigation | the same, opentui |
| Durability | background sessions under a supervisor (roster, restart on crash); in-session tasks: time limits in unattended sessions, `-p` kills on exit | in-memory registry, lost on restart | SQLite `job` table until the notice is admitted; PTY daemon handoff |
| Known gaps | #20754, #25955, #110924, #108903, #82235, #90534, #66436 | #62636, #62681, #64851, #62380, #60245, #53564 | #55358, #63426, #63894, #36423 |

---

## What a minimal .NET implementation should copy

1. **Own the registry durably; do not trust the parent's task list.** Both built-ins lose or misreport
   children on
   restart, `/clear`, compaction or stream gaps. Write one state file per run (pid, pgid, start time, the
   session id
   that launched it, its log path). The table reads those files. That covers discovery after a crash.
2. **Use a full-set snapshot plus events, not paired events.** Copy Claude Code's `background_tasks_changed`
   rule:
   each refresh rebuilds the whole set from the state files and live pids, so a missed event self-corrects.
   Detect
   stale rows with pid liveness plus start-time (pid reuse).
3. **Tag each run with its launcher's identity.** Read `CLAUDE_CODE_SESSION_ID` (Claude Code),
   `OPENCODE_SESSION_ID`
   (opencode 2.x) or nothing (1.x), and `CLAUDE_PID`/`OPENCODE_PID`. Filter the table by it. Do not depend on
   the
   undocumented `CLAUDE_CODE_CHILD_NOTES_DIR`.
4. **Parse the worker streams for per-row state.**
   - claude: the `result` line (`subtype`, `is_error`, `num_turns`, `total_cost_usd`, `duration_ms`,
     `modelUsage`) plus `system/api_retry` for a "retrying" cell.
   - opencode: sum `step_finish.part.cost`/`tokens`, count `tool_use`, and handle 2.x
     `retry`/`background_wait`.
   - Treat a missing final event as "unknown", never as success. opencode has no result event, and #62636 and
     #64851 show hangs and dropped events.
5. **Detach children into their own process group and kill by group.** opencode 1.x uses `detached` and Claude
   Code kills
   the process tree on SIGTERM. Record the pgid, and add a reaper or orphan sweep (#53564, #60245).
6. **Report completion to the parent conversation through a channel that exists.** For Claude Code:
   - Run the watcher as a `run_in_background` Bash command or a Monitor script: its exit or output becomes the
     task notification.
   - Or use an `asyncRewake` hook.
   - The Stop hook's `background_tasks` shows whether the session is still waiting.
   For opencode, a 1.x plugin `event` hook or a 2.x `experimental.idle` hook could inject a synthetic message.
7. **Rendering: copy btop, not tview.**
   - One buffered write per frame inside `ESC[?2026h … ESC[?2026l`, cursor hidden.
   - Home with `ESC[H` (or inline `\r` + `ESC[nA`), overwrite rows with `ESC[K`, then `ESC[J`.
   - Truncate every cell so nothing wraps.
   - Drop low-priority columns below width thresholds the way btop does (Threads under 75, Command at 55 or
     less).
   - Use SIGWINCH via `PosixSignalRegistration` and poll the size each frame.
   - Restore the terminal in `finally` and on Ctrl+C.
8. **Refresh at 1-2 s, like k9s and btop.** Skip a tick if the previous one is still running (k9s CAS) and
   highlight changed cells (k9s DeltaRow).
9. **Keys, if interactive: single keys on the selected row, k9s and btop style.**
   - `q` quit, Up/Down move, `/` filter, `?` help.
   - `s` cycles sort or Shift+letter sorts, `k` kills (with a confirm), Enter opens the log tail, `w` toggles
     wide columns.
   - No modal `:` command mode needed at this size.
10. **Non-TTY fallback is mandatory.** The launching session's Bash tool is a pipe. If
    `Console.IsOutputRedirected`,
    print one plain line per state change (or `--json` snapshot / `--once`), never escape codes. Spectre's
    `LiveDisplay` does not do this for you; its `Progress` does.
