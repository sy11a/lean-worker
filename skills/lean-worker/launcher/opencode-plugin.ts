// opencode plugin the launcher installs into a worker's clean config home. It blocks every tool call once the
// launcher has written <run>/wrapup.json (spend passed the wrap-up share of the budget), so the worker's last
// message is a handoff. It also gives the worker's shell commands the user's own XDG_CONFIG_HOME back.
//
// Redirect guard: opencode's `edit` permission rules do not cover shell redirects, so any allowed command could
// `echo x >> .git/config` and turn a later `git diff` (diff.external) or `git status` (core.fsmonitor) into a
// command runner. Claude Code already denies that; opencode needs the check in the launcher. The guard scans
// the bash command for write redirects, allows `/dev/null`, file-descriptor duplicates (`2>&1`, `>&-`) and
// paths under `.lean-worker/inbox/`. It parses a small bash subset near the redirect, and fails closed
// otherwise. When the command contains a `>`, it refuses line continuation, `$((`, `((`, `$[`, backtick,
// `${`, `#` (comment), `case`, `$$` (pid), a heredoc combined with `$(` or `<(`, and a heredoc combined
// with any `[` (e.g. `[ -f a ]`, `*.[ch]`); heredoc delimiters other than `NAME`/`'NAME'`/`"NAME"`/`\NAME`
// are also denied. An unterminated
// quote in the redirect target is denied.
// Denials are appended to `<run>/redirect-denied.log` (never to `hook.log`, which the launcher counts as one
// hook check per line via `LaunchRun.cs:587`).
import { appendFileSync, existsSync, readFileSync } from "fs"
import { join } from "path"

type PluginInput = { tool: string; args?: Record<string, unknown> }
type PluginOutput = { args?: Record<string, unknown> }
type PluginContext = { directory: string; worktree?: string }
type Hook = (input: PluginInput, output: PluginOutput) => Promise<void> | void

const FORBIDDEN_PATH_CHARS = ["$", "`", "*", "?", "[", "{", "~", "\\"]

const isTerminator = (c: string): boolean => {
  return c === " " || c === "\t" || c === "\n" || c === ";" || c === "&" || c === "|" ||
    c === "<" || c === ">" || c === "(" || c === ")"
}

const stripQuotes = (w: string): string => {
  if (w.length >= 2 && ((w[0] === "'" && w[w.length - 1] === "'") || (w[0] === '"' && w[w.length - 1] === '"'))) {
    return w.slice(1, -1)
  }
  return w
}

const dequoteWord = (w: string): string => {
  let out = ""
  let i = 0
  while (i < w.length) {
    const c = w[i]
    if (c === "'" || c === '"') {
      const j = w.indexOf(c, i + 1)
      if (j === -1) return w
      out += w.slice(i + 1, j)
      i = j + 1
    } else {
      out += c
      i++
    }
  }
  return out
}

const isAllowedPath = (raw: string, directory: string): boolean => {
  const t = dequoteWord(raw)
  if (t === "/dev/null") return true
  const dir = directory.replace(/\/+$/, "")
  let rest = ""
  if (t === ".lean-worker/inbox/" || t.startsWith(".lean-worker/inbox/")) rest = t.slice(".lean-worker/inbox/".length)
  else if (t === "./.lean-worker/inbox/" || t.startsWith("./.lean-worker/inbox/")) rest = t.slice("./.lean-worker/inbox/".length)
  else if (dir !== "" && (t === dir + "/.lean-worker/inbox/" || t.startsWith(dir + "/.lean-worker/inbox/"))) rest = t.slice((dir + "/.lean-worker/inbox/").length)
  else return false
  for (const seg of rest.split("/")) {
    if (seg === "..") return false
    if (seg === "") return false
  }
  for (const c of FORBIDDEN_PATH_CHARS) {
    if (t.includes(c)) return false
  }
  return true
}

type Heredoc = { delim: string; stripTabs: boolean }

const findSubEnd = (s: string, i: number): number => {
  let depth = 1
  while (i < s.length) {
    const ch = s[i]
    if (ch === "'") {
      let j = i + 1
      while (j < s.length && s[j] !== "'") j++
      if (j >= s.length) return -1
      i = j + 1
      continue
    }
    if (ch === "$" && s[i + 1] === "'") {
      let j = i + 2
      while (j < s.length) {
        if (s[j] === "\\" && j + 1 < s.length) { j += 2; continue }
        if (s[j] === "'") break
        j++
      }
      if (j >= s.length) return -1
      i = j + 1
      continue
    }
    if (ch === '"') {
      let j = i + 1
      while (j < s.length) {
        if (s[j] === "\\" && j + 1 < s.length) { j += 2; continue }
        if (s[j] === '"') break
        if (s[j] === "$" && s[j + 1] === "(") {
          const e = findSubEnd(s, j + 2)
          if (e === -1) return -1
          j = e + 1
          continue
        }
        j++
      }
      if (j >= s.length) return -1
      i = j + 1
      continue
    }
    if (ch === "\\") {
      if (i + 1 >= s.length) return -1
      i += 2
      continue
    }
    if (ch === "(") { depth++; i++; continue }
    if (ch === ")") {
      depth--
      if (depth === 0) return i
      i++
      continue
    }
    i++
  }
  return -1
}

// Returns the offending target on denial, or null when the command is allowed.
const scanCommand = (cmd: string, directory: string): string | null => {
  if (!cmd.includes(">")) return null
  if (cmd.includes("\\\n")) return "unsupported shell syntax: line continuation"
  if (cmd.includes("$((")) return "unsupported shell syntax: arithmetic expansion"
  if (cmd.includes("`")) return "unsupported shell syntax: backtick"
  if (cmd.includes("${")) return "unsupported shell syntax: parameter expansion"
  if (cmd.includes("#")) return "unsupported shell syntax: comment"
  if (cmd.includes("((")) return "unsupported shell syntax: arithmetic command"
  if (cmd.includes("$[")) return "unsupported shell syntax: old arithmetic"
  if (/\bcase\b/.test(cmd)) return "unsupported shell syntax: case"
  if (cmd.includes("$$")) return "unsupported shell syntax: pid"
  if (/(?<!<)<<(?!<)/.test(cmd) && (cmd.includes("$(") || cmd.includes("<("))) return "unsupported shell syntax: heredoc with substitution"
  if (/(?<!<)<<(?!<)/.test(cmd) && cmd.includes("[")) return "unsupported shell syntax: heredoc with subscript"
  const n = cmd.length
  let i = 0
  const heredocs: Heredoc[] = []

  const handleWriteOperator = (start: number, op: string): { nextI: number; denied: string | null } => {
    const opEnd = start + op.length
    let k = opEnd
    while (k < n && (cmd[k] === " " || cmd[k] === "\t")) k++
    const tStart = k
    let tEnd = k
    let inSingle = false
    let inDouble = false
    while (k < n) {
      const ch = cmd[k]
      if (!inSingle && !inDouble && ch === "\\" && k + 1 < n) {
        k += 2
        tEnd = k
        continue
      }
      if (!inDouble && ch === "'") {
        inSingle = !inSingle
        k++
        tEnd = k
        continue
      }
      if (!inSingle && ch === '"') {
        inDouble = !inDouble
        k++
        tEnd = k
        continue
      }
      if (!inSingle && !inDouble && ch === "(") return { nextI: k, denied: cmd.slice(tStart, k) }
      if (!inSingle && !inDouble && isTerminator(ch)) break
      k++
      tEnd = k
    }
    const target = cmd.slice(tStart, tEnd)
    if (target === "") return { nextI: k, denied: "" }
    if (k >= n && (inSingle || inDouble)) return { nextI: k, denied: target }
    if (op === ">&") {
      const stripped = stripQuotes(target)
      if (stripped === "-" || /^[0-9]+$/.test(stripped)) return { nextI: k, denied: null }
    }
    if (!isAllowedPath(target, directory)) return { nextI: k, denied: target }
    return { nextI: k, denied: null }
  }

  while (i < n) {
    const atLineStart = i === 0 || cmd[i - 1] === "\n"
    if (atLineStart && heredocs.length > 0) {
      const top = heredocs[0]
      let j = i
      while (j < n && cmd[j] !== "\n") j++
      let line = cmd.slice(i, j)
      if (top.stripTabs) {
        let k = 0
        while (k < line.length && line[k] === "\t") k++
        line = line.slice(k)
      }
      if (line === top.delim) heredocs.shift()
      i = j < n ? j + 1 : n
      continue
    }

    const c = cmd[i]

    if (c === "$" && cmd[i + 1] === "'") {
      let j = i + 2
      while (j < n) {
        if (cmd[j] === "\\" && j + 1 < n) { j += 2; continue }
        if (cmd[j] === "'") break
        j++
      }
      if (j >= n) return "$'"
      i = j + 1
      continue
    }
    if (c === "'") {
      let j = i + 1
      while (j < n && cmd[j] !== "'") j++
      if (j >= n) return "\u0027"
      i = j + 1
      continue
    }
    if (c === '"') {
      let j = i + 1
      while (j < n) {
        if (cmd[j] === "\\" && j + 1 < n) { j += 2; continue }
        if (cmd[j] === '"') break
        if (cmd[j] === "$" && cmd[j + 1] === "(") {
          const end = findSubEnd(cmd, j + 2)
          if (end === -1) return "unsupported shell syntax: unterminated $("
          const sub = cmd.slice(j + 2, end)
          let k = 0
          let heredoc = false
          while (k < sub.length) {
            if (sub[k] === "<" && sub[k + 1] === "<") {
              if (sub[k + 2] === "<") { k += 3; continue }
              heredoc = true
              break
            }
            k++
          }
          if (heredoc) return "unsupported shell syntax: heredoc in command substitution"
          const d = scanCommand(sub, directory)
          if (d !== null) return d
          j = end + 1
          continue
        }
        j++
      }
      if (j >= n) return '"'
      i = j + 1
      continue
    }
    if (c === "\\") {
      if (i + 1 >= n) return "\\"
      i += 2
      continue
    }

    if (c === "<" && cmd[i + 1] === "<") {
      if (cmd[i + 2] === "<") { i += 3; continue }
      let j = i + 2
      let stripTabs = false
      if (cmd[j] === "-") { stripTabs = true; j++ }
      while (j < n && (cmd[j] === " " || cmd[j] === "\t")) j++
      const s = j
      const re = /^(?:([A-Za-z_][A-Za-z0-9_]*)|'([A-Za-z_][A-Za-z0-9_]*)'|"([A-Za-z_][A-Za-z0-9_]*)"|\\([A-Za-z_][A-Za-z0-9_]*))/
      const m = re.exec(cmd.slice(s))
      let delim = ""
      let mLen = 0
      if (m !== null) {
        delim = m[1] ?? m[2] ?? m[3] ?? m[4] ?? ""
        mLen = m[0].length
      }
      const after = s + mLen
      const tail = after < n ? cmd[after] : ""
      const okTail = after >= n || tail === " " || tail === "\t" || tail === "\n" ||
        tail === ";" || tail === "&" || tail === "|" || tail === "(" || tail === ")" ||
        tail === "<" || tail === ">"
      if (delim === "" || !okTail) return "unsupported shell syntax: heredoc delimiter"
      heredocs.push({ delim, stripTabs })
      i = after
      continue
    }
    if (c === "<") {
      if (cmd[i + 1] === "(") { i += 2; continue }
      if (cmd[i + 1] === "&") { i += 2; continue }
      if (cmd[i + 1] === ">") {
        const r = handleWriteOperator(i, "<>")
        if (r.denied !== null) return r.denied === "" ? "<>" : r.denied
        i = r.nextI
        continue
      }
      i++
      continue
    }

    if (c === ">") {
      if (cmd[i + 1] === "(") return ">("
      if (cmd[i + 1] === ">") {
        const r = handleWriteOperator(i, ">>")
        if (r.denied !== null) return r.denied === "" ? ">>" : r.denied
        i = r.nextI
        continue
      }
      if (cmd[i + 1] === "|") {
        const r = handleWriteOperator(i, ">|")
        if (r.denied !== null) return r.denied === "" ? ">|" : r.denied
        i = r.nextI
        continue
      }
      if (cmd[i + 1] === "&") {
        const r = handleWriteOperator(i, ">&")
        if (r.denied !== null) return r.denied === "" ? ">&" : r.denied
        i = r.nextI
        continue
      }
      const r = handleWriteOperator(i, ">")
      if (r.denied !== null) return r.denied === "" ? ">" : r.denied
      i = r.nextI
      continue
    }

    if (c === "&" && cmd[i + 1] === ">") {
      const op = cmd[i + 2] === ">" ? "&>>" : "&>"
      const r = handleWriteOperator(i, op)
      if (r.denied !== null) return r.denied === "" ? op : r.denied
      i = r.nextI
      continue
    }

    i++
  }
  if (heredocs.length > 0) return "<<"
  return null
}

export const LeanWorkerWrapUp = async (context: PluginContext) => {
  const runDir = process.env.LEAN_WORKER_RUN_DIR
  const original = process.env.LEAN_WORKER_XDG_CONFIG_HOME
  if (original !== undefined) {
    if (original === "") delete process.env.XDG_CONFIG_HOME
    else process.env.XDG_CONFIG_HOME = original
  }
  const wrapUpActive = runDir !== undefined && process.env.LEAN_WORKER_WRAPUP === "1"
  const marker = runDir !== undefined ? join(runDir, "wrapup.json") : ""
  const directory = context.directory

  const hook: Hook = async (input, output) => {
    if (wrapUpActive && runDir !== undefined) {
      appendFileSync(join(runDir, "hook.log"), `${new Date().toISOString()} ${input.tool}\n`)
      if (existsSync(marker)) throw new Error(JSON.parse(readFileSync(marker, "utf8")).reason)
    }
    if (input.tool === "bash" && output.args !== undefined && typeof output.args.command === "string") {
      const denied = scanCommand(output.args.command, directory)
      if (denied !== null) {
        if (runDir !== undefined) {
          appendFileSync(join(runDir, "redirect-denied.log"), `${new Date().toISOString()} redirect-denied ${denied}\n`)
        }
        const refusal = denied.startsWith("unsupported shell syntax:")
          ? `lean-worker: redirect check refused ${denied}; write files with the edit tool or under .lean-worker/inbox/`
          : `lean-worker: redirect to ${denied} is not allowed; write files with the edit tool or under .lean-worker/inbox/`
        throw new Error(refusal)
      }
    }
  }
  return { "tool.execute.before": hook }
}
