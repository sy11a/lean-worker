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

- The build references the `Sy11a.Analyzers` policy package (`Directory.Build.props`, pinned exactly in `Directory.Packages.props`), restored by
  `nuget.config` from the private GitHub Packages feed `https://nuget.pkg.github.com/sy11a/index.json`. Restoring
  it needs a classic personal access token with `read:packages`, stored as credentials for the source key `github`
  in the user NuGet config (not in the repository), e.g.
  `NuGetPackageSourceCredentials_github="Username=<you>;Password=<token>"`. A policy finding fails the build: fix
  it, never suppress it. Installed copies (`install.sh` copies `skills/lean-worker/` alone) build without it.
- Unit tests: `dotnet test --project tests/LeanWorker.Tests -v q` (Microsoft Testing Platform, opted in by `global.json`).
- Lint gate: `dotnet tool run sy11a-lint check --full tests/LeanWorker.Tests/LeanWorker.Tests.csproj` (the test project as target covers the launcher; `--affected` needs a solution, which lean-worker does not have; `sy11a.lint` moves in lockstep with `Sy11a.Analyzers`, SY-POLICYVERSION).
- Architecture gate: `dotnet tool run sy11a-arch check --affected skills/lean-worker/launcher/LeanWorker.csproj` (`--full` for the whole picture; `sy11a.arch` moves in lockstep with `Sy11a.Analyzers` and `sy11a.lint`, SY-POLICYVERSION).
- Test gate: `dotnet tool run sy11a-test check --affected --base origin/master tests/LeanWorker.Tests/LeanWorker.Tests.csproj` (the test project as target, so a launcher-only change selects it; `--full` for the whole picture; `sy11a.test` moves in lockstep with `Sy11a.Analyzers`, `sy11a.lint` and `sy11a.arch`).
- Gate evidence for review (dev-flow BL-522; tool sides bound by sy11a/sy11a_analyzers#327):
  - **Deleted tests.** The test gate's report lists the test methods the diff deleted or that lost
    `[Fact]`/`[Theory]` (0002 R-073). That list goes into the review's gate tail. A deletion fails no gate;
    review judges it.
  - **Mutation round.** After a green test gate, run
    `dotnet tool run sy11a-test mutate --affected --base origin/master tests/LeanWorker.Tests/LeanWorker.Tests.csproj`
    (0002 R-092).
    - The survivors (file, line, original and replacement, mutator, covering tests) go to the Claude test
      worker for one round that writes or strengthens tests only.
    - The test gate and `mutate --affected` then run again over the same lines.
    - What still survives goes into the test-plan note's survivor record.
    - Exit `2`: the round could not run. The test-plan note says so in one line, and the task is not blocked.
    - Exit `143` (`sy11a.test` 0.18.0 and later): the run was interrupted. It is treated like `2`, but the
      line says "interrupted", and the survivors of the pairs that finished still count.
  - **Duplicate-detector evidence.** The duplicate gate is the architecture gate's SYA1201 (jscpd, 0004 R-045).
    The reviewer records any clone it missed and any SYA1201 hit judged false, with file and lines, under
    "Duplicate-detector evidence" in the review report.
- Acceptance (real workers, cents of list price): `tests/acceptance.sh`. It needs GLM; while GLM is not in
  use, run `LW_OPENCODE_MODEL=minimax-coding-plan/MiniMax-M3 tests/acceptance.sh --claude-only`.
