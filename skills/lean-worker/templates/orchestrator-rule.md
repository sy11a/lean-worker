<!-- lean-worker:orchestrator-rule -->
## Delegation (lean-worker)

This session orchestrates. Implementation, code search and review go to lean-worker workers (the
`lean-worker` skill; profiles in `.lean-worker/profiles.json`). This session writes the task, reads the
worker's result block, and verifies with one command. Design decisions and small edits to docs or config
stay here. At session start, before launching new work, run the skill's `status --here` to see the runs
still live, or ended, from earlier sessions.
