---
name: spec
description: Spec out a feature or change for RnRomManager (question loop with the user, independent review, small steps, kickoff prompt), deep-review a pending spec against the code, or implement a written spec on a dev/<slug> branch step by step and open a PR. Use for "/spec <what to build>", "/spec review <spec file>" or "/spec implement <spec file>".
argument-hint: "<feature request> | review <file>.md | implement <file>.md"
---

# spec

Three modes, chosen from the arguments:

- `implement <file>` (or the user pastes a spec's kickoff prompt) → **Implement**.
- `review <file>` (or the user asks for a deep review of a pending spec) → **Review**.
- Anything else → **Create**, with the arguments as the request. No arguments: ask what to spec.

Specs:

- **Folders:** `specs/pending/` (reviewed, not started), `specs/current/` (in progress), `specs/completed/`. The frontmatter `status` tracks the folder: `draft` or `ready` in `pending/`, `in-progress` in `current/`, `completed` in `completed/`. Template: [template.md](template.md).
- **Name:** one file per spec, `YYYY-MM-DD-<slug>.md` (today's date, a 2–5 word kebab-case slug). The slug is the file name without the date and `.md`. The argument may be a path or just the file name.
- **Unique slug:** `git fetch --prune`, then add `-2`, `-3`… (part of the slug) while the slug is used by a file in any `specs/` folder on the current branch or `origin/main`, or by a `dev/<slug>` branch locally or on `origin`.
- **Branch:** `dev/<slug>`. The spec lives there from creation to its single PR, opened only at the end of Implement: it reaches `main` with its finished implementation, so an abandoned branch leaves `main` untouched. Never open a PR for a spec on its own, and never commit spec work to `main`.

Repo rules for every mode are in `CONTRIBUTING.md` (test rule, doc rule). Find code by grepping `docs/codemap.md` (routes, pages, jobs, files, tests, doc section line numbers; never read it whole) and follow `docs/recipes.md` (where each common change must reach). Read those once per session; read other docs only by section. Never commit secrets or dev passwords; GitGuardian scans every PR.

## Writing rules (Create and Review)

Most review rounds are spent on these, so get them right while drafting:

- **Verify every name against the code before writing it:** files, classes, methods, routes, settings keys, CSS classes, components, props, test classes and `*.test.ts` files. Grep it; say `(new)` for anything new. Where a house convention exists (folder layout, a component, an error helper, how a sibling feature does it), follow it and name the example ("as `StorageMigrationJob` does").
- **Pin anything two developers could build differently:** exact user-visible text (titles, labels, toasts, empty states, errors), ProblemDetails `code` and message, icons (name and class), placement relative to an existing element, defaults, ordering, response shapes, Admin/User access.
- **Check the usual gaps:** errors and validation, permissions, the job lock, backup/restore and startup, cancellation, empty states, mobile and accessibility, every caller of changed code, docs (doc rule) and the README systems table, e2e. Name every existing test or e2e assertion the change breaks and how it changes.
- **Rules:** number them by area (`B1`, or letters per area such as `Z1`, `M1`); append new ones, never renumber.
- **Decisions:** `- <YYYY-MM-DD> <question> → <answer> (user | code | reviewer)`, one line each, without restating the body. Fact corrections found in one review pass share a single line.

## Create

Keep the user's time and the token count low: read only what the request touches, ask questions in batches, and never ask something the code already answers.

1. **Understand.** Restate the request in 2–4 lines: goal, who it's for, what's out of scope. If the restatement needs a guess, that guess becomes a question in step 3.
2. **Map the code.** Find every place the change must reach (start from the matching `docs/recipes.md` recipe). Use Grep/Read directly; use one `explore-haiku` subagent only when the sweep spans many areas. Note what can be reused, what will break (callers, data, API contracts), and any small optimization *inside code this spec already touches*. Don't propose refactors outside that footprint.
3. **Question loop.** Ask with `AskUserQuestion`, up to 4 questions per round, recommended option first, each question standalone. Each round covers, in this order: gaps or ambiguities, problems in the request (conflicts with existing behaviour, data loss, security, migration risk), and optimizations as opt-in choices. Repeat until nothing is open. Don't ask about things with an obvious default; state the default in the spec instead.
4. **Draft.** Write the spec from [template.md](template.md) into `specs/pending/`, `status: draft`, following the Writing rules. Include the steps now so the reviewer checks them too:
   - Each step is one commit that builds and passes its tests on its own, about ≤ 300 changed lines.
   - Order: data/migrations → core → API → UI → docs. Each step names its files, its tests and a checkable "Done when" list.
   - Every step that adds or changes behaviour lists the tests that prove it by test class or `*.test.ts` path (NUnit + NSubstitute, Testcontainers for DB, vitest for the SPA), so Implement can run just those. Playwright flows go in the last step and run only in Finish.
5. **Review.** Start one read-only reviewer: `Agent` with `subagent_type: "Plan"` and this prompt (fill in the path):

   > Review the spec at `<path>` for RnRomManager. Read it, then check it against the code it names (read those files; read `CONTRIBUTING.md` for the repo's rules and the "Writing rules" in `.claude/skills/spec/SKILL.md`). Look for: wrong facts (names that don't exist or don't work as described), gaps (cases, errors, permissions, migrations, docs, tests not covered), ambiguity (anything two developers could build differently), conflicts with existing code or behaviour, steps that are too big, out of order, or not independently testable, and missing or vague "Done when" items. Report only real problems as a numbered list: `R<n> [blocker|major|minor] <section>: <problem> — <suggested fix>`. If there are none, reply exactly `NO FINDINGS`. Don't rewrite the spec.

   For each finding: fix it in the spec if the code or the user's earlier answers settle it; reject it with a one-line reason if it's wrong; otherwise collect it for the user and ask in one `AskUserQuestion` round. Then send the reviewer a short list of what changed (`SendMessage` to the same agent so it keeps its context; it's deferred, so load it with `ToolSearch` `select:SendMessage` first) and ask it to re-check. Stop at `NO FINDINGS` or after **3 rounds**, then put every open finding to the user and apply their answers.
6. **Finish.** Set `status: ready`, fill the kickoff prompt with the spec's file name (not a path: the file moves between folders), and record the decisions in § Decisions.
7. **Publish.** If the working tree has changes other than the new spec, tell the user and ask before going on. `git switch main && git pull`, `git switch -c dev/<slug>` (if it already exists locally or on `origin`, ask first), commit only the spec (`Spec: <title>`), and `git push -u origin dev/<slug>`. Tell the user the branch name and the kickoff prompt.

## Review

A deep review of a `pending` spec against the real code, so a fresh developer can implement it without asking anything. You are the reviewer: no Plan agent.

1. **Setup.** `git fetch --prune`, switch to `dev/<slug>`, `git pull`. If the working tree isn't clean, or the spec is in `specs/current/` or `specs/completed/` (implementation has started), tell the user and stop.
2. **Read** the whole spec. § Decisions is settled: don't reopen a decision unless the code proves it wrong.
3. **Check** every rule, Changes row and step against the files it names, by the Writing rules. Also check conflicts with specs in `specs/completed/`, the step rules (Create step 4), and risks the spec doesn't mention (data loss, disk space, performance, security). Read the critical code yourself; use `explore-haiku` only for wide sweeps.
4. **Resolve.** Fix in the spec what the code, the spec or an earlier decision settles. Collect product/UX choices and anything you're unsure of, and ask them in `AskUserQuestion` rounds (Create step 3). Offer optimizations only as opt-in questions; add no features or refactors. Record changes in § Decisions (Writing rules).
5. **Finish.** Keep `status: ready` and the kickoff prompt unchanged. If anything changed, commit only the spec (`Spec review: <one-line summary>`) and push to `dev/<slug>`. Report: (a) what you fixed, one line each; (b) what the user decided; (c) anything still open; (d) a verdict, **CLEAN** if you changed nothing and nothing is open, else **NEEDS ANOTHER PASS**.

## Implement

1. **Find the spec and the state.** `git fetch --prune` first (so branches deleted on GitHub don't count).
   - Already in `specs/completed/` on `origin/main`: say so and stop.
   - No `dev/<slug>` locally or on `origin`: say so and stop (Create makes the branch).
   - Otherwise: the working tree must be clean; if it isn't, tell the user what's uncommitted and stop. Switch to `dev/<slug>` (if it exists both locally and on `origin`, `git pull` it), then check `git log --no-merges origin/main..`.
     - Spec in `specs/pending/` (start): `status` isn't `ready`: say so and stop. Otherwise `git merge origin/main` (merge, not rebase: the branch is already pushed; if it conflicts, tell the user and stop), `git mv` the spec to `specs/current/`, set `status: in-progress`, commit `Start spec: <title>`, and push.
     - Spec in `specs/current/` (resume): work continues at the first unticked step. If the ticks don't match the commits, tell the user what you found and ask before doing anything.
     - Spec not found on the branch: say so and stop.
2. **Read** the whole spec once, then the files the current step names.
3. **Per step**, one at a time:
   1. Implement exactly that step's deliverables, nothing from later steps.
   2. Add or update tests for every behaviour the step adds or changes.
   3. Run **targeted tests only** (the full suite takes over 10 minutes, so it runs once, in Finish): the test classes the step lists plus the ones covering the code it changed (callers included), `dotnet test tests/<Project> --filter "FullyQualifiedName~<ClassTests>|FullyQualifiedName~<OtherTests>"` per affected project, integration classes included when the step touches data access (Docker running; if it isn't, append `&Category!=Integration`, say so, and run them in Finish). When the SPA changed: `npm run lint`, `npm run check` and `npx vitest run <affected *.test.ts files>` in `src/RnRomManager.Web`. Fix failures before going on.
   4. Run `pwsh scripts/codemap.ps1`, so the next step (and session) finds the new code.
   5. Tick the step in the spec, and commit code + spec + `docs/codemap.md` together (`<title>: step <n> — <step title>`), and push, so the work can be resumed from another machine.
   6. Report in 2–4 lines (what changed, test result, anything surprising) and ask the user to continue, change something, or stop. If the user said to run all steps without stopping, continue unless a step fails or the spec turns out wrong.
4. **Spec turns out wrong or unclear:** don't guess and don't silently match the spec to the code. Ask the user, then update the spec section and § Decisions in the same commit as the code.
5. **Finish**, after the last step:
   1. `git merge origin/main` (ask the user when a conflict's resolution isn't clear; resolve a `docs/codemap.md` conflict by running `pwsh scripts/codemap.ps1`), then run `pwsh scripts/test-all.ps1` (also fails on a stale code map) and `pwsh scripts/test-e2e.ps1` with `run_in_background` (Docker running) and wait for the notification. On failures: fix them on the branch (ask the user if a failure also happens on `origin/main`), rerun just the failing tests until they pass, commit, then run the full suite once more. Never open the PR with failing tests.
   2. Check the doc rule: every changed behaviour is in its doc (`docs/`, `README.md`, spec); storage, format, DAT or browser-play changes update the README supported-systems table. Run `pwsh scripts/codemap.ps1` once more and commit `docs/codemap.md` if it changed.
   3. `git mv` the spec to `specs/completed/`, set `status: completed`, commit `Complete spec: <title>`.
   4. Push and open a PR from `dev/<slug>` to `main` titled `<title>`. The body is the spec's summary, the steps done, test results and anything deferred or risky. Stop there: merging is the maintainer's call.
