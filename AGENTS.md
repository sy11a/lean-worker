# lean-worker

The lean-worker skill: a launcher (`skills/lean-worker/launcher`, .NET) that runs one task in a
minimal-context worker (`claude -p` or `opencode run`), meters its cost, wraps it up near its budget and
records the run. A self-contained, lightweight process that works outside the fleet's release cycle;
which models it runs on is its own concern.

Tasks are this repository's GitHub issues (labels `backlog` + `type:feature|bug|idea`), filed on the
operator's word with `gh issue create`. A pull request that finishes one says `Closes #N`.

## Changes reach `master` through a pull request

1. Start from an up-to-date `master` (`git switch master && git pull --ff-only`), then branch before the
   first edit (`fix/<slug>`, `feature/<slug>`, `docs/<slug>`). For parallel work use a worktree
   (`git worktree add ../lean-worker-<slug> -b <branch> origin/master`) instead of switching
   branches under another session.
2. Verify, commit (no AI co-author trailers), `git push -u origin <branch>`, then
   `gh pr create --base master` (what, how verified; no AI attribution footer).
3. Merge it on GitHub yourself: `gh pr merge --merge --delete-branch`. Then
   `git switch master && git pull --ff-only` in the main checkout.
4. After the merge, remove every worktree and local branch the change used:
   `git worktree remove ../lean-worker-<slug> && git branch -d <branch>`, then `git worktree prune`.
   **Never remove `../lean-worker-pinned`**: it is sy11a_ctxops's pinned launcher. It moves only
   deliberately (`git -C ../lean-worker-pinned checkout --detach <commit>`, then a rebuild), when no
   worker runs from it, and the move is recorded in a ctxops `docs/history/` entry.

Never commit to `master` directly and never force-push it.

## Verify

- Unit tests: `dotnet test --project tests/LeanWorker.Tests -v q` (Microsoft Testing Platform, opted in by `global.json`).
- Acceptance (real workers, cents of list price): `tests/acceptance.sh`. It needs GLM; while GLM is not in
  use, run `LW_OPENCODE_MODEL=minimax-coding-plan/MiniMax-M3 tests/acceptance.sh --claude-only`.
