# Contributing

The MVP's design is in [specs/completed/initial-application.md](specs/completed/initial-application.md) and its build plan in [specs/completed/initial-application-impl.md](specs/completed/initial-application-impl.md). New work goes through the `spec` skill in [.claude/skills/spec](.claude/skills/spec/SKILL.md) (Claude Code).

## Specs, branches and PRs
Specs live in `specs/pending/` (reviewed, not started), `specs/current/` (in progress) and `specs/completed/`.

1. **Write a spec:** `/spec <what you want built>`. Claude maps the code, asks you questions until nothing is open, has a second agent review the draft, splits it into steps, and commits the spec to `specs/pending/` on a new `dev/<slug>` branch from `main`. No PR is opened: if the branch is abandoned, the spec never reaches `main`.
2. **Implement it:** run the kickoff prompt at the bottom of the spec (`/spec implement <file>.md`). On `dev/<slug>`, Claude moves the spec to `specs/current/` and implements one step per commit with its tests, checking in with you after each step. Run the same prompt to resume.
3. **Finish:** once `scripts/test-all.ps1` and `scripts/test-e2e.ps1` pass, the spec moves to `specs/completed/` and Claude opens the branch's only PR to `main`, with the spec and its implementation together. The maintainer merges it.

If a spec turns out unclear or wrong while implementing, don't guess: ask, then fix the spec and its Decisions section in the same commit as the code. Never change a spec silently to match the code.

## Test rule
Every non-trivial class gets unit tests (NUnit `Assert.That`, NSubstitute). File tests use real temp dirs; database tests use Testcontainers (Docker must be running). `pwsh scripts/test-all.ps1` (the .NET build with warnings as errors, every test project, and the web app's lint, type check and tests) and `pwsh scripts/test-e2e.ps1` must pass before a PR is opened; CI runs the same jobs. See [docs/development.md](docs/development.md) for the setup.

## Doc rule
The PR that implements or changes a behaviour updates the doc(s) it belongs to (the MVP spec's §19 maps behaviours to docs). Spec changes go into the spec and its Decisions section.
