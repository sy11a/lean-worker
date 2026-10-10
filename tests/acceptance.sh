#!/usr/bin/env bash
# Acceptance tests for the launcher: real workers on small models with tiny budgets (a few cents of list price
# in total). Needs claude (logged in), jq, and for the opencode and GLM cases opencode with a zai-coding-plan login.
#
# Usage: tests/acceptance.sh [--claude-only] [--keep]
#   --claude-only  skip the cases that need opencode or a z.ai GLM Coding Plan
#   --keep         keep the scratch directory for inspection
# LW_OPENCODE_MODEL=<provider/model> runs the opencode permission case on that model, even with --claude-only.
set -uo pipefail

claude_only=0 keep=0
for a in "$@"; do case "$a" in --claude-only) claude_only=1 ;; --keep) keep=1 ;; *) echo "unknown option $a"; exit 2 ;; esac; done

repo="$(cd "$(dirname "$0")/.." && pwd)"
launcher_dir="$repo/skills/lean-worker/launcher"
dotnet build "$launcher_dir" -c Release -v q -nologo >/dev/null || { echo "launcher build failed"; exit 2; }
L="$launcher_dir/bin/Release/net8.0/LeanWorker"
[ -x "$L" ] || L="dotnet $launcher_dir/bin/Release/net8.0/LeanWorker.dll"

scratch="$(mktemp -d "${TMPDIR:-/tmp}/lean-worker acceptance XXXXXX")"   # a space on purpose (R7 case 10)
cwd="$scratch/cwd" rr="$scratch/runs root"
mkdir -p "$cwd" "$rr/inbox/glob12" "$rr/inbox/count"
for x in a b c d e f g h i j k l; do touch "$cwd/${x}1.txt"; done
printf 'Call the Glob tool 12 times, one call per turn, with the patterns a* b* c* d* e* f* g* h* i* j* k* l* in that order. Then reply DONE and the number of Glob calls you made.\n' > "$rr/inbox/glob12/task.md"
printf 'Use Glob once with pattern *.txt and reply with the number of files only.\n' > "$rr/inbox/count/task.md"
glob12="$rr/inbox/glob12/task.md" count="$rr/inbox/count/task.md"
haiku=(--model claude-haiku-4-5 --effort low --no-project-notes --tools Glob)
glm=(--model zai-coding-plan/glm-5.3 --no-project-notes --tools Glob)

pass=0 fail=0
check() { if eval "$2"; then pass=$((pass + 1)); echo "  ok    $1"; else fail=$((fail + 1)); echo "  FAIL  $1"; fi; }
run() { (cd "$cwd" && $L --runs-root "$rr" "$@") > "$scratch/out.txt" 2>&1; code=$?; last="$(ls -d "$rr"/runs/* 2>/dev/null | tail -1)"; }
cmd() { (cd "$cwd" && $L "$1" --runs-root "$rr" "${@:2}") > "$scratch/out.txt" 2>&1; code=$?; }
field() { jq -r "$1" "$last/summary.json"; }
patterns() { jq -r 'select(.type=="assistant") | .message.content[]? | select(.type=="tool_use") | .input.pattern' "$last/stream.jsonl" | sort -u | tr -d '*\n'; }

echo "== 1. wrap-up triggers (claude, haiku)"
run --task "$glob12" "${haiku[@]}" --max-budget-usd 0.03 --wrap-up-at 0.3
run1="$last"
check "exit 3 and status wrapped-up" '[ $code = 3 ] && [ "$(field .status)" = wrapped-up ]'
check "wrapup.json written, report starts with HANDOFF" '[ -f "$last/wrapup.json" ] && head -1 "$last/report.md" | grep -q HANDOFF'
check "hook ran (hook.log) and user settings stayed out" '[ -s "$last/hook.log" ] && grep -q -- "--setting-sources \"\"" "$last/command.txt"'
check "metered cost within 5% of Claude Code's own" 'awk -v a="$(field .total_cost_usd)" -v b="$(field .reported_cost_usd)" "BEGIN{d=a-b; if(d<0)d=-d; exit !(d <= b*0.05)}"'

echo "== 2. continuation"
# The last tool call of a wrapped-up run is the one the hook denied; only the ones before it were done.
done1="$(jq -r 'select(.type=="assistant") | .message.content[]? | select(.type=="tool_use") | .input.pattern' "$run1/stream.jsonl" | sed '$d' | sort -u | tr -d '*\n')"
run --continue-from "$run1" "${haiku[@]}" --max-budget-usd 0.05
check "success" '[ $code = 0 ] && [ "$(field .status)" = success ]'
check "only the remaining patterns were called (first run: $done1)" '[ -z "$(comm -12 <(echo "$done1" | fold -w1 | sort) <(patterns | fold -w1 | sort))" ]'
check "task.md = original + one continuation heading" '[ "$(grep -c "^## Continuation (lean-worker)" "$last/task.md")" = 1 ] && head -1 "$last/task.md" | grep -q "Call the Glob tool"'
run2="$last"

echo "== 3. keep-hooks loads the user's and project's hooks; the default does not"
mkdir -p "$cwd/.claude"
printf '{"hooks":{"PreToolUse":[{"matcher":".*","hooks":[{"type":"command","command":"touch \\"%s/project-hook-ran\\""}]}]}}\n' "$scratch" > "$cwd/.claude/settings.json"
run --task "$count" "${haiku[@]}" --max-budget-usd 0.03
check "default: project hook did not run" '[ ! -e "$scratch/project-hook-ran" ]'
run --task "$count" "${haiku[@]}" --max-budget-usd 0.03 --keep-hooks
check "--keep-hooks: project hook ran" '[ -e "$scratch/project-hook-ran" ]'
rm -rf "$cwd/.claude"

echo "== 4. wrap-up off"
run --task "$count" "${haiku[@]}" --max-budget-usd 0.03 --wrap-up-at 0
check "no hook injected, wrap-up off in the result" '[ "$(jq ".hooks // null" "$last/settings.json")" = null ] && grep -q "wrap-up off" "$scratch/out.txt"'

echo "== 6. a price change takes effect without a rebuild"
printf '{"models":{"anthropic/claude-haiku-4-5":{"input":100,"output":500,"cacheRead":10,"cacheWrite":125}}}\n' > "$scratch/dear.json"
run --task "$count" "${haiku[@]}" --max-budget-usd 0.5 --prices "$scratch/dear.json"
check "100x price -> wrapped up on a task that normally costs under 1 cent" '[ "$(field .status)" = wrapped-up ] || [ "$(field .status)" = budget-exceeded ]'

echo "== 7. unknown-model policy"
printf '{"unknownModel":"error"}\n' > "$scratch/strict.json"
run --task "$count" --model claude-nonexistent-9 --no-project-notes --prices "$scratch/strict.json"
check "unknownModel=error refuses to launch (exit 2) and names the model" '[ $code = 2 ] && grep -q "claude-nonexistent-9" "$scratch/out.txt"'

echo "== 8. continuation of a continuation"
run --continue-from "$run2" "${haiku[@]}" --max-budget-usd 0.03
check "still exactly one continuation heading" '[ "$(grep -c "^## Continuation (lean-worker)" "$last/task.md")" = 1 ]'

echo "== quota command and chain fallback"
if [ $claude_only = 0 ]; then
    cmd quota; check "quota prints the z.ai windows" 'grep -q "zai-coding-plan" "$scratch/out.txt" && grep -q weekly "$scratch/out.txt"'
    printf '{"providers":{"zai-coding-plan":{"quota":{"maxPercent":{"5h":0}}}}}\n' > "$scratch/full.json"
    run --task "$count" --model zai-coding-plan/glm-5.3 --no-project-notes --tools Glob --prices "$scratch/full.json" --max-budget-usd 0.03
    check "a single-model chain over quota still runs, with a note" 'grep -q "over its quota threshold" "$scratch/out.txt"'
fi

if [ $claude_only = 0 ]; then
    echo "== 1g. wrap-up on GLM through z.ai's Anthropic endpoint (claude runtime)"
    run --task "$glob12" "${glm[@]}" --max-budget-usd 0.01 --wrap-up-at 0.3
    glmrun="$last"
    check "wrapped-up, input tokens metered (z.ai reports them in message_delta)" '[ "$(field .status)" = wrapped-up ] && [ "$(field .tokens.input)" -gt 0 ]'

    echo "== 1o. wrap-up in the opencode runtime"
    run --runtime opencode --task "$glob12" "${glm[@]}" --max-budget-usd 0.02 --wrap-up-at 0.3
    check "wrapped-up with a HANDOFF report" '[ $code = 3 ] && grep -q HANDOFF "$last/report.md"'
    check "the provider block is not recorded" '! grep -q "apiKey" "$last/opencode-config.json"'

    echo "== 9. cross-runtime continuation (claude/GLM -> opencode/GLM)"
    run --continue-from "$glmrun" --runtime opencode "${glm[@]}" --max-budget-usd 0.05
    check "success in opencode, continued_from recorded" '[ "$(field .status)" = success ] && [ "$(field .runtime)" = opencode ] && [ "$(field .continued_from)" != null ]'
fi

if [ $claude_only = 0 ] || [ -n "${LW_OPENCODE_MODEL:-}" ]; then
    echo "== 11. opencode: a command outside allowedTools is denied, counted, and the run goes on"
    printf 'Run the bash command `ls | sort` exactly as written. Whatever happens, then run the bash command `ls`. Then reply DONE.\n' > "$rr/inbox/deny.md"
    run --runtime opencode --task "$rr/inbox/deny.md" --model "${LW_OPENCODE_MODEL:-zai-coding-plan/glm-5.3}" --no-project-notes --tools Bash --allow 'Bash(ls:*)' --max-budget-usd 0.05
    check "success with DONE, one denial counted" '[ "$(field .status)" = success ] && grep -q DONE "$last/report.md" && [ "$(field .permission_denials)" -ge 1 ]'

    echo "== 14. model traits add the model's allowed commands"
    ocm="${LW_OPENCODE_MODEL:-zai-coding-plan/glm-5.3}"
    printf '{"modelTraits":{"%s":{"allowedTools":["Bash(head:*)"],"note":"acceptance trait"}}}\n' "${ocm#*/}" > "$scratch/traits.json"
    printf 'Run the bash command `ls | head -1` exactly as written. Then reply DONE.\n' > "$rr/inbox/traits.md"
    run --runtime opencode --task "$rr/inbox/traits.md" --model "$ocm" --prices "$scratch/traits.json" --no-project-notes --tools Bash --allow 'Bash(ls:*)' --max-budget-usd 0.05
    check "no denial, the trait recorded and noted" '[ "$(field .status)" = success ] && [ "$(field .permission_denials)" = 0 ] && [ "$(field .model_traits)" = "${ocm#*/}" ] && grep -q "acceptance trait" "$scratch/out.txt"'

    echo "== 12. a write outside the task's write scope is reported"
    saved="$cwd" cwd="$scratch/repo"
    mkdir -p "$cwd/src" "$cwd/docs" && (cd "$cwd" && git init -q && git add -A && git -c user.email=t@t -c user.name=t commit -qm init --allow-empty)
    printf 'Use the Write tool to create src/a.txt containing "a" and docs/b.txt containing "b". Then reply DONE.\n' > "$rr/inbox/scope.md"
    run --runtime opencode --task "$rr/inbox/scope.md" --model "${LW_OPENCODE_MODEL:-zai-coding-plan/glm-5.3}" --no-project-notes --tools Write --write-scope 'src/**' --max-budget-usd 0.05
    check "two files changed, docs/b.txt outside the scope, warned" '[ "$(field ".changed_files | length")" = 2 ] && [ "$(field ".out_of_scope | join(\",\")")" = docs/b.txt ] && grep -q "outside the write scope: docs/b.txt" "$scratch/out.txt"'
    cwd="$saved"
fi

echo "== 15. gate loop with a fake gate (claude, haiku)"
# mkgate <name> <findings in round 1> <findings in later rounds> <exit code override or "">: a fake gate script in
# the scratch directory (outside the working directory and the runs root) with a round counter and a small SARIF.
mkgate() {
    cat > "$scratch/gate-$1.sh" <<EOF
d="$scratch"
n=0
[ -f "\$d/round-$1" ] && read n < "\$d/round-$1"
n=\$((n + 1))
echo "\$n" > "\$d/round-$1"
if [ "\$n" = 1 ]; then c=$2; else c=$3; fi
r="" sep=""
i=0
while [ "\$i" -lt "\$c" ]; do r="\$r\$sep{\"ruleId\":\"R\$i\",\"message\":{\"text\":\"finding \$i\"}}"; sep=","; i=\$((i + 1)); done
echo "{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"fake\"}},\"results\":[\$r]}]}" > "\$d/sarif-$1-\$n.json"
echo "fake gate round \$n: \$c finding(s)"
echo "sarif: \$d/sarif-$1-\$n.json"
[ -n "$4" ] && exit $4
[ "\$c" -gt 0 ] && exit 1
exit 0
EOF
}
mkgate gated 2 0 ""
mkgate stuck 3 3 ""
mkgate broken 1 1 2
jq -n --arg g1 "$scratch/gate-gated.sh" --arg g2 "$scratch/gate-stuck.sh" --arg g3 "$scratch/gate-broken.sh" '
    def p($g): {model: "claude-haiku-4-5", effort: "low", tools: ["Glob"], maxBudgetUsd: 0.05, gate: {command: ["sh", $g]}};
    {profiles: {gated: p($g1), stuck: p($g2), broken: p($g3)}}' > "$rr/profiles.json"
nruns() { ls "$rr/runs" | wc -l; }

before="$(nruns)"
run --task "$count" --profile gated --no-project-notes
second="$last"; first="$(ls -d "$rr"/runs/* | tail -2 | head -1)"
check "gate clean: exit 0, two run directories, the second named ...-gate2" '[ $code = 0 ] && [ "$(( $(nruns) - before ))" = 2 ] && [[ "$second" == *-gate2 ]]'
check "the second task.md has the gate report heading" 'grep -q "^## Gate report (lean-worker)" "$second/task.md"'
check "result shows the chain clean after 2 rounds, findings 2→0" 'grep -q "gate:     clean after 2 round(s), findings 2→0" "$scratch/out.txt"'
check "gate.json in both run directories, last decision clean" '[ -f "$first/gate.json" ] && [ -f "$second/gate.json" ] && [ "$(field .gate.decision)" = clean ]'

before="$(nruns)"
run --task "$count" --profile stuck --gate-max-rounds 2 --no-project-notes
check "a gate that always finds 3: exit 4 and a STUCK line" '[ $code = 4 ] && grep -q "STUCK" "$scratch/out.txt"'

before="$(nruns)"
run --task "$count" --profile broken --no-project-notes
check "a gate that exits 2: exit 5, one worker run" '[ $code = 5 ] && [ "$(( $(nruns) - before ))" = 1 ]'

echo "== 13. the ledger's schema and stats --json"
cmd stats --json
check "rows carry schema_version 1 and stats --json has groups" '[ "$(tail -1 "$rr/runs.jsonl" | jq .schema_version)" = 1 ] && jq -e ".schema_version == 1 and (.groups | length) > 0" "$scratch/out.txt" >/dev/null'

echo "== 5. cost of a manual session"
cmd cost --claude "$run1/stream.jsonl"
check "cost reads a Claude stream/transcript" 'grep -q "at list price" "$scratch/out.txt"'

echo
echo "passed $pass, failed $fail  (runs: $rr)"
(cd "$cwd" && $L stats --runs-root "$rr")
[ $keep = 1 ] || [ $fail -gt 0 ] || rm -rf "$scratch"
[ $fail = 0 ]
