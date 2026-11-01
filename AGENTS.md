# lean-worker

Existing code wins over this file: if they disagree, follow the code and report the mismatch.

Tasks are this repository's GitHub issues (labels `backlog` + `type:feature|bug|idea`), filed on the
operator's word with `gh issue create`. A pull request that finishes one says `Closes #N`.

## Changes reach `master` through a pull request

1. Start from an up-to-date `master` (`git switch master && git pull --ff-only`), then branch before
   the first edit (`fix/<slug>`, `feature/<slug>`, `docs/<slug>`). For parallel work use a worktree:
   `git worktree add ../lean-worker-<slug> -b <branch> origin/master`.
2. Verify, commit (no AI co-author trailers), `git push -u origin <branch>`, then
   `gh pr create --base master` (what, how verified; no AI attribution footer).
3. Merge it on GitHub yourself: `gh pr merge --merge --delete-branch`, then
   `git switch master && git pull --ff-only` in the main checkout.

Never commit to `master` directly and never force-push it.

## Verify (per change: these only; no mutation run per change)

- `dotnet test --project tests/LeanWorker.Tests -v q`
- `dotnet tool run sy11a-lint check --full tests/LeanWorker.Tests/LeanWorker.Tests.csproj`
  (`--full`: no solution here, so no `--affected`; the test project as target covers the launcher)
- `dotnet tool run sy11a-arch check --affected skills/lean-worker/launcher/LeanWorker.csproj`
- `dotnet tool run sy11a-test check --affected --base origin/master tests/LeanWorker.Tests/LeanWorker.Tests.csproj`
- The build restores the private `Sy11a.Analyzers` policy package from
  `https://nuget.pkg.github.com/sy11a/index.json`: a classic PAT with `read:packages`, stored in the
  user NuGet config as `NuGetPackageSourceCredentials_github="Username=<you>;Password=<token>"`,
  never in the repository. A policy finding fails the build: fix it, never suppress it.
- Acceptance (while GLM is not in use): `LW_OPENCODE_MODEL=minimax-coding-plan/MiniMax-M3 tests/acceptance.sh --claude-only`.

## Evidence, handoffs, worktrees

- A report or handoff states each claim with its evidence: the command, its exit code and the log
  path or run link. A claim without evidence is not passed on.
- The test gate's report lists test methods the diff deleted or that lost `[Fact]`/`[Theory]`; the
  list goes to the review. The reviewer records SYA1201 misses and false hits under
  "Duplicate-detector evidence".
- A handoff holds the position and the next step only; the history lives in git.
- Remove your worktree when its branch is merged (`git worktree remove`, then `git worktree prune`);
  never remove one with uncommitted work.
- Never remove `../lean-worker-pinned`: it is sy11a_ctxops's pinned launcher. It moves only
  deliberately (`git -C ../lean-worker-pinned checkout --detach <commit>`, then a rebuild), while no
  worker runs from it, and the move is recorded in a ctxops `docs/history/` entry.
