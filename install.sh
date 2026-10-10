#!/usr/bin/env bash
# Installs the lean-worker skill for orchestrating sessions in Claude Code and/or opencode, on Linux or macOS.
# Runs without any agent. Steps:
#   1. Checks prerequisites: .NET SDK 8+, claude (with --bare) when Claude Code orchestrates, opencode when
#      opencode orchestrates, and an API key (optional).
#   2. Copies skills/lean-worker into each orchestrator's skill directory: ~/.claude/skills and
#      ~/.config/opencode/skills (default), or a project's .claude/skills and .opencode/skills (--scope project).
#   3. Builds the launcher once, so the first worker run starts immediately.
#   4. With --project <path>, prepares that project:
#        - .lean-worker/project.md and profiles.json from the templates (existing files are never overwritten)
#        - .gitignore entries for .lean-worker/runs/, .lean-worker/inbox/ and .lean-worker/runs.jsonl
#        - an allow rule so the orchestrator can start workers without a permission prompt: in
#          <project>/.claude/settings.json (Claude Code) and <project>/opencode.json (opencode); backed up
#          first; needs jq; --skip-permission to skip
#        - with --add-rule, the delegation rule (templates/orchestrator-rule.md) appended to the instruction
#          file each orchestrator reads: CLAUDE.md (Claude Code), AGENTS.md (opencode); once per file
#   5. With --smoke-test, runs one tiny read-only worker (Haiku, budget $0.10) and prints its result block.
# Safe to re-run: it updates the skill and leaves your project files alone, including
# .lean-worker/prices.json and ~/.config/lean-worker/prices.json.
#
# Usage: ./install.sh [--project <path>] [--scope user|project] [--orchestrator claude|opencode|both]
#                     [--skip-permission] [--add-rule] [--smoke-test]
# --orchestrator defaults to both when opencode is on PATH, else claude.
set -euo pipefail

project="" scope="user" skip_permission=0 smoke=0 orchestrator="" add_rule=0
while [ $# -gt 0 ]; do
    case "$1" in
        --project) project="${2:?--project needs a path}"; shift 2 ;;
        --scope) scope="${2:?--scope needs user or project}"; shift 2 ;;
        --orchestrator) orchestrator="${2:?--orchestrator needs claude, opencode or both}"; shift 2 ;;
        --skip-permission) skip_permission=1; shift ;;
        --add-rule) add_rule=1; shift ;;
        --smoke-test) smoke=1; shift ;;
        -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
        *) echo "unknown option: $1 (see --help)" >&2; exit 1 ;;
    esac
done

step() { printf '\n== %s\n' "$1"; }
ok() { printf '   ok    %s\n' "$1"; }
warn() { printf '   WARN  %s\n' "$1"; }
die() { printf '   FAIL  %s\n' "$1"; exit 1; }

repo_root="$(cd "$(dirname "$0")" && pwd)"
source_dir="$repo_root/skills/lean-worker"
[ -f "$source_dir/SKILL.md" ] || die "run this script from the downloaded repository (skills/lean-worker not found next to it)"
case "$scope" in user|project) ;; *) die "--scope must be user or project" ;; esac
[ "$scope" = project ] && [ -z "$project" ] && die "--scope project needs --project"
[ "$add_rule" = 1 ] && [ -z "$project" ] && die "--add-rule needs --project"
if [ -z "$orchestrator" ]; then if command -v opencode >/dev/null; then orchestrator=both; else orchestrator=claude; fi; fi
case "$orchestrator" in claude|opencode|both) ;; *) die "--orchestrator must be claude, opencode or both" ;; esac
uses() { [ "$orchestrator" = both ] || [ "$orchestrator" = "$1" ]; }
if [ -n "$project" ]; then
    [ -d "$project" ] || die "project path not found: $project"
    project="$(cd "$project" && pwd)"
fi

# ---------- 1. prerequisites ----------
step 'Checking prerequisites'
ok "orchestrator: $orchestrator"
if command -v claude >/dev/null; then
    claude --help 2>&1 | grep -q -- '--bare' || die "this Claude Code version has no --bare; update Claude Code"
    ok "claude: $(claude --version 2>&1 | head -1)"
elif uses claude; then die "'claude' (Claude Code) is not on PATH"
else ok "no claude on PATH: workers can use only the opencode runtime"; fi

command -v dotnet >/dev/null || die "'dotnet' is not on PATH; install the .NET SDK 8 or newer"
sdks="$(dotnet --list-sdks 2>/dev/null | awk '{split($1, v, "."); if (v[1] >= 8) print $1}' | paste -sd, - | sed 's/,/, /g')"
[ -n "$sdks" ] || die ".NET SDK 8 or newer not found (dotnet --list-sdks: $(dotnet --list-sdks 2>/dev/null | paste -sd '; ' -))"
ok "dotnet SDK: $sdks"

if command -v opencode >/dev/null; then ok "opencode: $(opencode --version 2>&1 | head -1) (runtime \"opencode\" available)"
elif uses opencode; then die "'opencode' is not on PATH"
else ok "no opencode on PATH: only the claude runtime is available (optional)"; fi

if [ -z "${ANTHROPIC_API_KEY:-}" ]; then
    ok "no ANTHROPIC_API_KEY: workers will run in lean mode on your Claude Code login (Pro/Max/Team/Enterprise subscription). Make sure 'claude' is logged in."
else ok "ANTHROPIC_API_KEY is set: workers use the key (lean mode with wrap-up; bare mode when wrap-up is off)"; fi

# ---------- 2. copy the skill, 3. build the launcher ----------
opencode_config="${XDG_CONFIG_HOME:-$HOME/.config}/opencode"
targets=()
if uses claude; then
    if [ "$scope" = user ]; then targets+=("$HOME/.claude/skills/lean-worker"); else targets+=("$project/.claude/skills/lean-worker"); fi
fi
if uses opencode; then
    if [ "$scope" = user ]; then targets+=("$opencode_config/skills/lean-worker"); else targets+=("$project/.opencode/skills/lean-worker"); fi
fi
for target in "${targets[@]}"; do
    step "Installing the skill at $target"
    mkdir -p "$target/launcher"
    # Replace the skill's own files but keep a previous build (bin/obj) to save time.
    for f in SKILL.md models.md; do cp "$source_dir/$f" "$target/$f"; done
    rm -rf "$target/templates" && cp -R "$source_dir/templates" "$target/templates"
    find "$target/launcher" -maxdepth 1 -type f -delete
    find "$source_dir/launcher" -maxdepth 1 -type f -exec cp {} "$target/launcher/" \;
    ok "skill installed"
    if ! build_out="$(dotnet build "$target/launcher" -c Release -v q -nologo 2>&1)"; then echo "$build_out"; die "launcher build failed"; fi
    ok "launcher built"
done
launcher="${targets[0]}/launcher"
run_cmd="dotnet run --project \"$launcher\" -c Release --"

# Appends the delegation rule to an instruction file once; a symlinked file already covered is skipped.
rule_done=""
add_rule_to() {
    local file="$1" real
    real="$(readlink -f "$file" 2>/dev/null || echo "$file")"
    case " $rule_done " in *" $real "*) ok "$(basename "$file"): the rule is already in $(basename "$real")"; return ;; esac
    rule_done="$rule_done $real"
    if [ -f "$file" ] && grep -qF 'lean-worker:orchestrator-rule' "$file"; then ok "$(basename "$file"): delegation rule already present"; return; fi
    if [ -s "$file" ]; then printf '\n' >> "$file"; fi
    cat "$source_dir/templates/orchestrator-rule.md" >> "$file"
    ok "$(basename "$file"): delegation rule added"
}

# ---------- 4. prepare the project ----------
if [ -n "$project" ]; then
    step "Preparing project $project"
    lw="$project/.lean-worker"
    mkdir -p "$lw/inbox"
    for f in project.md profiles.json; do
        if [ -e "$lw/$f" ]; then ok ".lean-worker/$f exists, left unchanged"
        else cp "$source_dir/templates/$f" "$lw/$f"; ok ".lean-worker/$f created from template (fill it in)"; fi
    done

    gi="$project/.gitignore"
    added=""
    for entry in .lean-worker/runs/ .lean-worker/inbox/ .lean-worker/runs.jsonl; do
        if [ -f "$gi" ] && grep -qxF "$entry" "$gi"; then continue; fi
        if [ -s "$gi" ] && [ -n "$(tail -c1 "$gi")" ]; then printf '\n' >> "$gi"; fi
        printf '%s\n' "$entry" >> "$gi"
        added="$added $entry"
    done
    if [ -n "$added" ]; then ok ".gitignore: added$added"; else ok ".gitignore already covers .lean-worker"; fi

    if [ "$skip_permission" = 0 ] && uses claude; then
        rule='Bash(dotnet run --project:*)'
        settings="$project/.claude/settings.json"
        mkdir -p "$project/.claude"
        if ! command -v jq >/dev/null; then
            warn "jq not found: add the permission rule $rule to .claude/settings.json yourself"
        else
            existed=1
            [ -f "$settings" ] || { echo '{}' > "$settings"; existed=0; }
            jq empty "$settings" 2>/dev/null || die ".claude/settings.json is not valid JSON; fix it or use --skip-permission"
            if jq -e --arg r "$rule" '(.permissions.allow // []) | index($r)' "$settings" >/dev/null; then
                ok "permission rule already present: $rule"
            else
                if [ "$existed" = 1 ]; then
                    backup="$settings.bak-$(date +%Y%m%d-%H%M%S)"
                    cp "$settings" "$backup" && ok "backed up settings to $backup"
                fi
                tmp="$(mktemp)"
                jq --arg r "$rule" '.permissions.allow = ((.permissions.allow // []) + [$r])' "$settings" > "$tmp" && mv "$tmp" "$settings"
                ok "added permission rule to .claude/settings.json: $rule"
            fi
        fi
    fi

    if [ "$skip_permission" = 0 ] && uses opencode; then
        pattern='dotnet run --project*'
        oc="$project/opencode.json"
        if [ ! -f "$oc" ] && [ -f "$project/opencode.jsonc" ]; then
            warn "the project has opencode.jsonc: add \"permission\": {\"bash\": {\"$pattern\": \"allow\"}} to it yourself"
        elif ! command -v jq >/dev/null; then
            warn "jq not found: add \"permission\": {\"bash\": {\"$pattern\": \"allow\"}} to opencode.json yourself"
        else
            existed=1
            [ -f "$oc" ] || { echo '{"$schema": "https://opencode.ai/config.json"}' > "$oc"; existed=0; }
            jq empty "$oc" 2>/dev/null || die "opencode.json is not valid JSON; fix it or use --skip-permission"
            if jq -e --arg p "$pattern" '.permission.bash == "allow" or (.permission.bash | type == "object" and .[$p] == "allow")' "$oc" >/dev/null; then
                ok "opencode permission already present: $pattern"
            else
                if [ "$existed" = 1 ]; then backup="$oc.bak-$(date +%Y%m%d-%H%M%S)"; cp "$oc" "$backup" && ok "backed up opencode.json to $backup"; fi
                tmp="$(mktemp)"
                # A plain string (e.g. "ask") becomes the catch-all of an object, so its meaning is kept.
                jq --arg p "$pattern" '.permission.bash = ((.permission.bash // {}) | if type == "string" then {"*": .} else . end) + {($p): "allow"}' "$oc" > "$tmp" && mv "$tmp" "$oc"
                ok "added opencode permission to opencode.json: $pattern"
            fi
        fi
    fi

    if [ "$add_rule" = 1 ]; then
        uses claude && add_rule_to "$project/CLAUDE.md"
        uses opencode && add_rule_to "$project/AGENTS.md"
    fi
fi

# ---------- 5. smoke test ----------
if [ "$smoke" = 1 ] && ! command -v claude >/dev/null; then warn "smoke test skipped: it runs on Claude Haiku and needs claude"; smoke=0; fi
if [ "$smoke" = 1 ]; then
    step 'Smoke test (Haiku, read-only, budget $0.10)'
    work="${project:-$(mktemp -d "${TMPDIR:-/tmp}/lean-worker-smoke-XXXXXXXX")}"
    task_dir="$work/.lean-worker/inbox/smoke-test"
    mkdir -p "$task_dir"
    printf '# Task: smoke test\n\nDo not read or change any file. Reply with exactly one line: lean-worker smoke test OK\n' > "$task_dir/task.md"
    set +e
    (cd "$work" && dotnet run --project "$launcher" -c Release -- --task "$task_dir/task.md" --model haiku --effort low --tools Read --max-budget-usd 0.1 --no-project-notes)
    smoke_exit=$?
    set -e
    if [ "$smoke_exit" = 0 ]; then ok "smoke test passed"; else warn "smoke test did not succeed (exit $smoke_exit); read the block above"; fi
fi

# ---------- next steps ----------
step 'Done'
echo "   Launcher command (the skill uses it for you):"
echo "     $run_cmd --task .lean-worker/inbox/<name>/task.md --profile code"
echo
echo "   Next:"
echo "   1. Start a new Claude Code / opencode session so it sees the skill."
if [ -n "$project" ]; then
    echo "   2. In a session in $project, ask:"
    echo "        /lean-worker set up .lean-worker/project.md and profiles.json for this repository"
    echo "      then review and edit both files by hand."
    echo "   3. Delegate a task:  /lean-worker <what to do>; done when <command> passes"
else
    echo "   2. Re-run with --project <your repo> to prepare a project (templates, .gitignore, permission)."
fi
