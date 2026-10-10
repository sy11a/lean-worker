# Installing lean-worker: instructions for an AI agent

These instructions are for a Claude Code or opencode session that a user has asked to install this skill,
for example: *"Install the lean-worker skill for this project, following
https://github.com/sy11a/lean-worker/blob/master/INSTALL.md"*.

Follow the steps in order. Each step says what to run, what counts as success, and when to stop
and ask the user. Do not skip the questions: two steps change the user's configuration.

Throughout, **project root** means the root of the repository the user is working in, which is
normally your current working directory. **Repo copy** means the downloaded copy of this
repository.

---

## Step 1: Get the repository

Clone it into a temporary folder, not into the project:

```
git clone --depth 1 https://github.com/sy11a/lean-worker.git "<temp-dir>/lean-worker"
```

- Use `${TMPDIR:-/tmp}` for `<temp-dir>`.
- **If the clone fails** (network policy, proxy, GitHub blocked), stop. Ask the user to download
  the repository ZIP and tell you where they extracted it. Do not look for workarounds around
  the network policy.

Success: `<repo copy>/install.sh` and `<repo copy>/skills/lean-worker/SKILL.md` exist.

## Step 2: Check prerequisites

Run each command and report the result to the user in one short list:

| Check | Command | Required |
|---|---|---|
| Claude Code supports `--bare` | `claude --help` (look for `--bare`) | yes when Claude Code orchestrates or runs workers: stop and tell the user to update Claude Code |
| .NET SDK 8 or newer | `dotnet --list-sdks` | yes: stop and tell the user to install it |
| How Claude Code is authenticated | check whether `ANTHROPIC_API_KEY` is set (**never print its value**); if not, the user is on a subscription login (Pro/Max/Team/Enterprise) | no: either works. Workers run in **lean** mode either way (bare mode only with a key and wrap-up off). Do not ask for an API key on a subscription |
| A shell for the installer | `bash --version` (and `jq --version` for the permission step) | yes |
| opencode | `opencode --version` | yes when opencode orchestrates; otherwise needed only for workers in the opencode runtime |

## Step 3: Ask the user four questions

Ask them together, in one message, and wait for the answer:

1. **Which orchestrators?** Claude Code, opencode, or both. The default is both when opencode is installed.
2. **Where should the skill be installed?**
   - (a) For the user: `~/.claude/skills` and/or
     `~/.config/opencode/skills`, so it is available in every project. Its description then loads into
     every session (about 310 characters).
   - (b) For this project only: `<project root>/.claude/skills` and/or `<project root>/.opencode/skills`,
     which can be committed and shared with the team.
3. **May the installer add a permission rule** so the orchestrator starts workers without a prompt?
   The rule is `Bash(dotnet run --project:*)` in `<project root>/.claude/settings.json` and
   `"dotnet run --project*": "allow"` under `permission.bash` in `<project root>/opencode.json`.
   - The installer backs each file up first (`.bak-<stamp>`) and rewrites it as normalised JSON, so
     existing formatting is lost.
   - If the user says no, the installer skips this step and the user approves each launch.
4. **Should the delegation rule go into the project's instructions?** A short section ("this session
   orchestrates; implementation, search and review go to lean-worker workers") appended once to
   `CLAUDE.md` (Claude Code) and/or `AGENTS.md` (opencode). Recommend no if another tool generates or
   delivers those files.

## Step 4: Run the installer

Run it from anywhere, pointing at the project root.

```
bash "<repo copy>/install.sh" --project "<project root>" [--orchestrator claude|opencode|both] [--scope project] [--skip-permission] [--add-rule]
```

- Add `--orchestrator` with the answer to question 1.
- Add `--scope project` if the user chose (b) in question 2.
- Add `--skip-permission` if the user said no to question 3.
- Add `--add-rule` if the user said yes to question 4.

Read the output. Success means:

- every line is `ok` or `WARN`, and the last section is `== Done`;
- `.lean-worker/project.md` and `.lean-worker/profiles.json` exist in the project root;
- `.gitignore` covers `.lean-worker/runs/`, `.lean-worker/inbox/` and `.lean-worker/runs.jsonl`.

If a line says `FAIL`, stop. Report that line to the user and follow what it says.

## Step 5: Fill in `.lean-worker/project.md`

This file is the context every worker receives, and it is paid for on every worker API call.
**Target: under ~2,000 tokens.** Delete the quoted template block at the top, then replace every `{...}` placeholder:

1. **Stack and layout:** read the build files (`*.sln`, `*.csproj`, `Directory.Build.props`,
   `package.json`, `pom.xml`, … whatever exists), the top-level README and CLAUDE.md, and the CI
   config. Write down the languages and frameworks, and where the main code, tests and tools live.
2. **Commands:** find the real build, test-all, test-one and lint/format commands. Prefer the ones
   CI uses. Do not run long builds or test suites just to check a command; say which ones you
   have not verified.
3. **Conventions:** only rules that matter when changing code, such as naming, error handling, test
   style or the architecture layering the codebase enforces. Two to six bullets.
4. **Never:** keep the defaults (no commit or push; no edits to CI, dependency versions, secrets or
   config) and add what the codebase shows is sensitive, such as generated code folders, migrations
   or public API contracts.
5. **Where to look:** link long documents by path. Do not paste them.

Anything you cannot determine from the repository, **ask the user** instead of guessing. Keep
it to one message with at most five questions.

## Step 6: Fill in `.lean-worker/profiles.json`

- In the `code` profile, replace `Bash({build command}:*)` and `Bash({test command}:*)` with
  the project's real command prefixes, e.g. `Bash(dotnet test:*)` or `Bash(npm run test:*)`.
  Add any other commands a coding worker needs routinely, such as a formatter or a code
  generator. Keep the list short.
- Keep the other profiles (`read`, `edit`, `research`, `review`) unless the user wants changes.
  Delete `research` if the user's policy bars web access for agents.
- Ask whether the user runs other providers (GLM, DeepSeek, MiniMax) or opencode. If so, offer
  model chains (`"model": ["zai-coding-plan/glm-5.3", "sonnet"]`) and `"runtime"`; see the
  README section "Models, prices and subscriptions". Do not change prices unless the user asks.
- If the project has an obvious extra task class (a slow integration-test suite, a separate
  frontend), propose a profile for it. Add it only if the user agrees.
- The file must stay valid JSON.

Then show the user both files, or a summary of what you filled in, and say plainly:
**"These files are yours: please read them and edit anything that is wrong."**

## Step 7: Smoke test

Ask the user before running this. It makes one real API call to Haiku with a budget of $0.10.

From the project root:

```
dotnet run --project "<skill-dir>/launcher" -c Release -- --task ".lean-worker/inbox/smoke-test/task.md" --model haiku --effort low --tools Read --max-budget-usd 0.1
```

Before running it, create `.lean-worker/inbox/smoke-test/task.md` containing:
`Do not read or change any file. Reply with exactly one line: lean-worker smoke test OK`.

`<skill-dir>` is where the skill was installed (the installer prints it): `~/.claude/skills/lean-worker`,
`~/.config/opencode/skills/lean-worker`, or the same under
`<project root>/.claude/skills` / `<project root>/.opencode/skills`.

- The launcher picks the mode itself; the result block shows which one ran.
- **If it fails with "Not logged in":** in lean mode, ask the user to run `claude` and `/login`.
  In bare mode, the key is missing or invalid.
- **Success:** the block starts with `LEAN-WORKER RESULT`, shows `status: success`, and the worker
  report says `lean-worker smoke test OK`. Tell the user the cost and first-call context from
  the block.

## Step 8: Finish

1. Delete the temporary clone from step 1.
2. Tell the user, in a few lines:
   - where the skill is installed, and whether the permission rule was added;
   - what went into `project.md` and `profiles.json`, and what still needs their review;
   - the smoke test result: its mode, and its cost. On a subscription, say that the cost is the
  list-price equivalent and the tokens count against the plan's usage;
   - **that they must start a new Claude Code or opencode session before the skill is available;**
   - how to use it: `/lean-worker <what to do>; done when <command> passes`.
3. Do not commit anything. Whether `.lean-worker/project.md`, `profiles.json` and
   `.claude/settings.json` get committed is the user's decision. Recommend committing the first two.

---

## Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| `LEAN-WORKER LAUNCH FAILED: --mode bare needs ANTHROPIC_API_KEY` | `--mode bare` was forced without a key | Use `--mode auto` (the default) or `--mode lean` on a subscription |
| `status: error`, `reason: api_error`, "Not logged in" | Lean mode: `claude` is not logged in. Bare mode: the key is missing or invalid | Lean: run `claude`, then `/login`. Bare: check the key in the environment Claude Code runs in |
| The worker sees project rules it should not see (lean) | A rules file outside the excluded patterns | Check the worker's `settings.json` in the run directory; pass `--claude-settings` with extra `claudeMdExcludes` |
| `'claude' is not on PATH` from the launcher | Claude Code is installed for another shell | Add its folder to PATH, or reinstall with the native installer |
| Permission prompts on every launch | Rule not added, or a different command shape | Add `Bash(dotnet run --project:*)` to `.claude/settings.json` `permissions.allow` |
| `WARNING: n permission denial(s)` in a result | The worker needed a command not pre-approved | Add that command prefix to the profile's `allowedTools` |
| Build error on the first run | SDK older than 8, or a corrupted copy | Check `dotnet --list-sdks`, then re-run the installer |
| `note: no price for <model>; priced as dearest` | The model is not in the price book | Add it to `.lean-worker/prices.json` (see the README) |
| `no API key for provider '<name>'` | A non-Anthropic model with no key | Set the provider's `keyEnv` variable, or log in with `opencode auth login` |
