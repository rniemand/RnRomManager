# Contributing

The MVP's design is in [specs/completed/initial-application.md](specs/completed/initial-application.md) and its build plan in [specs/completed/initial-application-impl.md](specs/completed/initial-application-impl.md). New work goes through the `spec` skill in [.claude/skills/spec](.claude/skills/spec/SKILL.md) (Claude Code).

## Specs, branches and PRs
Specs live in `specs/pending/` (reviewed, not started), `specs/current/` (in progress) and `specs/completed/`.

1. **Write a spec:** `/spec <what you want built>`. Claude maps the code, asks you questions until nothing is open, has a second agent review the draft, splits it into steps, and opens a `Spec: <title>` PR from `spec/<slug>` that adds the spec to `specs/pending/`. The maintainer merges it.
2. **Implement it:** run the kickoff prompt at the bottom of the spec (`/spec implement <file>.md`). Claude creates `dev/<slug>` from `main`, moves the spec to `specs/current/`, and implements one step per commit with its tests, checking in with you after each step. Run the same prompt to resume.
3. **Finish:** once `scripts/test-all.ps1` and `scripts/test-e2e.ps1` pass, the spec moves to `specs/completed/` and Claude opens a PR to `main`. The maintainer merges it.

If a spec turns out unclear or wrong while implementing, don't guess: ask, then fix the spec and its Decisions section in the same commit as the code. Never change a spec silently to match the code.

## Test rule
Every non-trivial class gets unit tests (NUnit `Assert.That`, NSubstitute). File tests use real temp dirs; database tests use Testcontainers (Docker must be running). `pwsh scripts/test-all.ps1` (the .NET build with warnings as errors, every test project, and the web app's lint, type check, tests and build) and `pwsh scripts/test-e2e.ps1` must pass before a PR is opened; CI runs the same jobs. See [docs/development.md](docs/development.md) for the setup.

## Doc rule
The PR that implements or changes a behaviour updates the doc(s) it belongs to (spec §19). Spec changes go into the spec and its decisions log (§21).
