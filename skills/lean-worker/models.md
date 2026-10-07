# Worker models: what each one needs

A profile names a **role** (`read`, `code`, `review`, ...) and a chain of models for it. What a worker
needs because of **the model** is below. What depends on **the provider** (billing, quota, endpoint, key)
is in `launcher/prices.json`, under `providers`. One model can come from several providers: for example
GLM from `zai-coding-plan` or from metered `zai`. Its traits stay the same either way.

Status: **measured** = seen in real runs (source given); **not verified** = no worker run yet.

**Enforced as data:** a row's commands and note that are also in the price book's `modelTraits`
(`launcher/prices.json`, keyed by model id without the provider) are applied at launch. The launcher adds the
commands to a Bash profile's `allowedTools` and prints the note in the result. `<repo>` in an allowedTools or
deniedTools entry is replaced by the working tree's root (`git rev-parse --show-toplevel`); an allowedTools entry
with `<repo>` is dropped, and a deniedTools entry with `<repo>` stops the launch, when there is no root or the
root has a shell or glob character. A `<repo>` deny blocks only that exact spelling of the path (`-C <root>/` or
`-C .` are other strings). Add a project's own traits in `.lean-worker/prices.json`. The runtime follows the
provider: claude where the provider has an Anthropic-compatible endpoint, opencode otherwise, unless the profile
or `--runtime` sets one.

| Model | Providers in the price book | Runtimes | What it needs | Status |
|-------|-----------------------------|----------|---------------|--------|
| MiniMax-M3 | `minimax-coding-plan` (subscription), `minimax` | claude, opencode | Chains shell commands (`\|`, `;`, `,`), and every segment must be allowed: give Bash profiles the read-only helpers (`head`, `tail`, `od`, `wc`, `grep`, `jq`, `echo`, git reads) and read line ranges with Read (offset/limit). Edited files outside its task's scope: always launch code tasks with `--write-scope`. Many small calls: a 270-test code task took 241 calls, $1.27 list-price equivalent, +4% of the 5h window. | measured (sy11a_ctxops P6, 2026-09-29); **enforced** (`modelTraits`) |
| MiniMax-M2.7 | `minimax-coding-plan`, `minimax` | claude, opencode | Cheaper sibling for `read`. | not verified |
| GLM-5.3, GLM-5.3-flash | `zai-coding-plan` (subscription), `zai` | claude, opencode | z.ai reports all usage in `message_delta` (the meter reads it). The plan has a peak-hour multiplier. | measured (acceptance tests, 2026-09-29) |
| DeepSeek v4 | `deepseek` | claude, opencode | Prices and endpoint from the provider's documentation. | not verified |
| Claude Haiku / Sonnet / Opus | `anthropic` | claude | The chain's last step and the orchestrator. Cross-provider review: review a change with a model of another family than the one that wrote it. | measured |

When a run teaches something about a model, add it here (with its source), not to every task.
