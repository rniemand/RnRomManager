# Contributing

The design lives in [specs/initial-application.md](specs/initial-application.md) (the spec) and the build order in [specs/initial-application-impl.md](specs/initial-application-impl.md) (the plan).

## Branches and PRs
One step = one branch = one PR.

1. `git switch main && git pull`.
2. Pick the first unticked step in the plan's tracker.
3. `git switch -c step/NN-slug`.
4. Read the step's spec sections, build exactly its deliverables (nothing from later steps), and write its tests.
5. Check the step's "Done when" list and the plan's Definition of Done.
6. Tick the step in the tracker in the same branch.
7. Commit, push, open a PR titled `SNN: <step title>`, wait for CI, then squash-merge.

If the spec is unclear or wrong, don't guess: add the question to spec §20, ask the owner, and record the answer in the spec section and §21 in the same PR. Never change the spec silently to match the code.

## Test rule
Every non-trivial class gets unit tests (NUnit `Assert.That`, NSubstitute). File tests use real temp dirs; database tests use Testcontainers (Docker must be running). `pwsh scripts/test-all.ps1` (the .NET build with warnings as errors, every test project, and the web app's lint, type check, tests and build) and `pwsh scripts/test-e2e.ps1` must pass before a PR is opened; CI runs the same jobs. See [docs/development.md](docs/development.md) for the setup.

## Doc rule
The PR that implements or changes a behaviour updates the doc(s) it belongs to (spec §19). Spec changes go into the spec and its decisions log (§21).
