# lean-worker

Existing code wins over this file: if they disagree, follow the code and report the mismatch.

Tasks are this repository's GitHub issues (labels `backlog` + `type:feature|bug|idea`), filed on the
operator's word with `gh issue create`. A pull request that finishes one says `Closes #N`.

## Changes reach `master` through a pull request

1. Start from an up-to-date `master` (`git switch master && git pull --ff-only`), then branch before
   the first edit (`fix/<slug>`, `feature/<slug>`, `docs/<slug>`). For parallel work use a worktree under
   `~/Repository_v2/.worktrees/lean-worker/` (never next to the projects):
   `wt-add ~/Repository_v2/lean-worker <slug> -b <branch> origin/master`.
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
- before a head goes to the full CI run (operator, 2026-11-09): the touched test classes and the ones that reference the changed
  production types (build the test project and run them in the same step, report the counts), and the affected fast tier inside
  the test sandbox, `dotnet tool run sy11a-test check --affected --base origin/master --tiers small,medium --no-build tests/LeanWorker.Tests/LeanWorker.Tests.csproj`,
  exit code in the report, run from a detached unit, not a session shell
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
- Never remove `~/Repository_v2/.worktrees/lean-worker/pinned` (until 2026-11-17 `../lean-worker-pinned`): it is
  sy11a_ctxops's pinned launcher. It moves only deliberately
  (`git -C ~/Repository_v2/.worktrees/lean-worker/pinned checkout --detach <commit>`, then a rebuild), while no
  worker runs from it, and the move is recorded in a ctxops `docs/history/` entry.

## Decision gate

The operator's rule of 2026-11-16 (every repository in ~/Repository_v2), made automatic on 2026-11-20: anything
found along the way that is not part of the approved task (a review finding outside the PR's own change, a residual
route, a flaky test, an idea, a new rule) is filed as a GitHub issue in this repository with the label
`needs-decision`: what was found, where (file:line), the evidence, the proposed fix and the class the filer proposes
(below). It is not worked on, and not put on an epic or backlog checklist, until it is triaged. A finding on the
PR's own change that blocks it (a MAJOR, a red test) is part of the approved task and is fixed in that PR. This
overrides any rule above that puts findings on an epic checklist.

Triage (automatic, the operator's word of 2026-11-20): at the end of every train of tasks (the last PR of a batch
merged, or the session about to wait on rulings) and at session start, the orchestrating session reviews every open
`needs-decision` issue of this repository and decides it alone. The main goals are the open `decision:do` tasks,
the gates' integrity (fail-closed, trust, security, no false green) and a green build, test and CI run. Each issue
gets exactly one class, with a one-line reason as an issue comment:

1. `triage:drop`: small, changes nothing that matters, costs more time than it saves (cosmetic, a workaround that
   holds and will not recur, an idea with no user). Closed as not planned.
2. `triage:batch`: real but not urgent. Stays open with its area named in the comment (one file area or tool).
   When 5-8 batch issues share an area, or no `decision:do` task is open, the oldest area pack becomes one task
   and one PR: its issues get `decision:do`.
3. `triage:now` (+ `decision:do`): urgent and tied to the main goals: it blocks a `decision:do` task, makes a gate
   fail open or report a false green, is a security defect, or breaks the build, tests or CI. Worked on at once,
   in the blocked task's PR when it is its own change, otherwise its own PR.

Reserved for the operator, never auto-decided (`triage:operator`, stays `needs-decision`): a new analyzer or gate
rule, loosening a gate (a ban, allowance, suppression, threshold or widened trust), dependency-bot merges the
operator keeps, and anything the operator has claimed. When such an issue blocks a main goal, the handoff and the
session's report name it first. The operator overrides any class by relabelling; that wins. The full rule:
sy11a_analyzers AGENTS.md (sy11a/sy11a_analyzers#1331).
