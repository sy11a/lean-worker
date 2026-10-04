# Agent install prompt

Paste the block below into a Claude Code session opened in your project's folder. The agent
installs and configures the skill by following [INSTALL.md](INSTALL.md), and stops to ask you
at the points where your decision is needed.

If GitHub is not reachable from that machine, download the repository ZIP and replace step 1
with: "The repository is extracted at `<path>`."

```
Install the lean-worker skill for this project.

Source: https://github.com/sy11a/lean-worker
Follow INSTALL.md from that repository step by step:
https://github.com/sy11a/lean-worker/blob/master/INSTALL.md

Summary of what I expect:
1. Clone the repo into a temp folder (not into this project). If GitHub is blocked, stop and ask me for a ZIP.
2. Check prerequisites: `claude --help` lists --bare, .NET SDK 8+, bash, optionally opencode, and how Claude Code is authenticated. I am on an Enterprise subscription login (no API key needed; workers run in lean mode). Never print any key.
3. Ask me, in one message: install for my user or for this project only, and may you add the permission rule to .claude/settings.json.
4. Run install.sh for this project root with the flags matching my answers.
5. Fill .lean-worker/project.md and .lean-worker/profiles.json from this repository (build files, README, CLAUDE.md, CI). Delete the template block, replace every {...} placeholder, keep project.md under ~2k tokens, and ask me about anything you cannot determine (max 5 questions).
6. Ask before the smoke test (one Haiku call, $0.10 budget), then run it and report the mode, status, cost (list-price equivalent; on my subscription it counts against plan usage) and first-call context.
7. Delete the temp clone, commit nothing, and tell me what was installed, what I must review by hand, and that I need to restart Claude Code before /lean-worker works.
```
