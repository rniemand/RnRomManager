---
name: spec
description: Spec out a feature or change for RnRomManager (question loop with the user, independent review, small steps, kickoff prompt), or implement a written spec on a dev/<slug> branch step by step and open a PR. Use for "/spec <what to build>" or "/spec implement <spec file>".
argument-hint: "<feature request> | implement <specs/.../file.md>"
---

# spec

Two modes, chosen from the arguments:

- `implement <path>` (or the user pastes a spec's kickoff prompt) → **Implement**.
- Anything else → **Create**, with the arguments as the request. No arguments: ask what to spec.

Specs live in `specs/pending/` (reviewed, not started), `specs/current/` (in progress) and `specs/completed/`. One file per spec, named `YYYY-MM-DD-<slug>.md` (today's date, a 2–5 word kebab-case slug; add `-2` if it exists). The slug is the file name without the date and `.md` (so it includes any `-2`), and it is the same everywhere: `branch: dev/<slug>`, `spec/<slug>`. The frontmatter `status` tracks the folder: `draft` or `ready` in `pending/`, `in-progress` in `current/`, `completed` in `completed/`. Spec template: [template.md](template.md).

Repo rules that apply to both modes are in `CONTRIBUTING.md` (test rule, doc rule) and `docs/development.md` (architecture, conventions). Read them once per session, not per step. Never commit secrets or dev passwords; GitGuardian scans every PR.

## Create

Keep the user's time and the token count low: read only what the request touches, ask questions in batches, and never ask something the code already answers.

1. **Understand.** Restate the request in 2–4 lines: goal, who it's for, what's out of scope. If the restatement needs a guess, that guess becomes a question in step 3.
2. **Map the code.** Find every place the change must reach: endpoints, services, repositories, migrations, the system handler, SPA pages/stores, tests, docs, config. Use Grep/Read directly; use one `Explore` subagent only when the sweep spans many areas. Note what can be reused, what will break (callers, data, API contracts), and any small optimization *inside code this spec already touches* (dead code, duplication, a missing test). Don't propose refactors outside that footprint.
3. **Question loop.** Ask with `AskUserQuestion`, up to 4 questions per round, recommended option first. Each round covers, in this order: gaps or ambiguities, problems you found in the request (conflicts with existing behaviour, data loss, security, migration risk), and optimizations as opt-in choices. Repeat until nothing is open. Don't ask about things with an obvious default; state the default in the spec instead.
4. **Draft.** Write the spec from [template.md](template.md) into `specs/pending/`, `status: draft`. Include the steps (§ Steps) now so the reviewer checks them too. Steps rules:
   - Each step is one commit that builds and passes its tests on its own, small enough for one sitting (about ≤ 300 changed lines).
   - Order: data/migrations → core → API → UI → docs. Each step names its files, its tests and a "Done when" list.
   - Every step that adds or changes behaviour lists the tests that prove it (NUnit + NSubstitute, Testcontainers for DB, vitest for the SPA, Playwright for user flows).
5. **Review.** Start one read-only reviewer: `Agent` with `subagent_type: "Plan"` and this prompt (fill in the path):

   > Review the spec at `<path>` for RnRomManager. Read it, then check it against the code it names (read those files; read `CONTRIBUTING.md` for the repo's rules). Look for: gaps (cases, errors, permissions, migrations, docs, tests not covered), ambiguity (anything two developers could build differently), conflicts with existing code or behaviour, steps that are too big, out of order, or not independently testable, and missing or vague "Done when" items. Report only real problems as a numbered list: `R<n> [blocker|major|minor] <section>: <problem> — <suggested fix>`. If there are none, reply exactly `NO FINDINGS`. Don't rewrite the spec.

   For each finding: fix it in the spec if the code or the user's earlier answers settle it; reject it with a one-line reason if it's wrong; otherwise collect it for the user. Ask the user about all collected findings in one `AskUserQuestion` round. Then send the reviewer a short list of what changed (`SendMessage` to the same agent, so it keeps its context; it's a deferred tool, so load it with `ToolSearch` `select:SendMessage` first, and don't start a new reviewer instead) and ask it to re-check. Stop when it says `NO FINDINGS`, or after **3 rounds**: then put every open finding to the user and apply their answers.
6. **Finish.** Set `status: ready`, fill the kickoff prompt at the bottom with the spec's file name (not a path: the file moves between folders), and record the decisions you and the user made in § Decisions.
7. **Publish.** If the working tree has changes other than the new spec, tell the user and ask before going on. `git switch main && git pull`, `git switch -c spec/<slug>` (if `spec/<slug>` already exists locally or on `origin`, tell the user and ask before going on), commit only the spec (`Spec: <title>`), push, and open a PR to `main` titled `Spec: <title>` whose body is the spec's summary and the kickoff prompt. Tell the user the PR link and the kickoff prompt. Merging is the maintainer's call.

## Implement

1. **Find the spec and the state.** The argument may be a path or just the file name. The branch is the spec's frontmatter `branch` (`dev/<slug>`); if you can't read the spec on the current branch, use `dev/<slug>` (the slug from the file name). Run `git fetch` first, then decide by the branch, not by the folder the file is in on the current branch (on `main` an in-progress spec is still in `pending/`):
   - `dev/<slug>` exists locally or on `origin` (resume): switch to it (if both exist, `git pull` it), check `git status` and `git log main..`. Work continues at the first unticked step. If the spec isn't in `specs/current/` there, the tree has uncommitted changes, or the ticks don't match the commits, tell the user what you found and ask before doing anything.
   - No such branch (start): needs a clean working tree; if it isn't, tell the user what's uncommitted and stop. `git switch main && git pull`, then find the file in `specs/` on `main`. Not in `specs/pending/` on `main` (e.g. the Spec PR isn't merged yet): say so and stop. `status` isn't `ready`: say so and stop. Otherwise `git switch -c dev/<slug>`, `git mv` the spec to `specs/current/`, set `status: in-progress`, commit `Start spec: <title>`, and `git push -u origin dev/<slug>`.
   - Already in `specs/completed/` on `main`, or not found anywhere: say so and stop.
2. **Read** the whole spec once, then the files the current step names.
3. **Per step**, one at a time:
   1. Implement exactly that step's deliverables, nothing from later steps.
   2. Add or update tests for every behaviour the step adds or changes.
   3. Run the targeted tests: `dotnet test tests/<Project>` for each affected test project (add `--filter "Category!=Integration"` only when Docker isn't running, and say so), and `npm run lint`, `npm run check` and `npm test` in `src/RnRomManager.Web` when the SPA changed. Fix failures before going on.
   4. Tick the step in the spec, and commit code + spec together: `<title>: step <n> — <step title>`, and push, so the work is backed up and can be resumed from another machine.
   5. Report in 2–4 lines (what changed, test result, anything surprising) and ask the user to continue, change something, or stop. If the user said to run all steps without stopping, continue unless a step fails or the spec turns out wrong.
4. **Spec turns out wrong or unclear:** don't guess and don't silently match the spec to the code. Ask the user, update the spec section and § Decisions in the same commit as the code.
5. **Finish**, after the last step:
   1. `pwsh scripts/test-all.ps1` and `pwsh scripts/test-e2e.ps1` must both pass (Docker running). If one fails, fix it on the branch and rerun; never open the PR with failing tests.
   2. Check the doc rule: every changed behaviour is in its doc (`docs/`, `README.md`, spec).
   3. `git mv` the spec to `specs/completed/`, set `status: completed`, commit `Complete spec: <title>`.
   4. Push and open a PR to `main` titled `<title>`; the body lists the steps done, test results and anything deferred or risky. Stop there: merging is the maintainer's call.
