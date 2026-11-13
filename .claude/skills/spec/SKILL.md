---
name: spec
description: Spec out a feature or change for RnRomManager (question loop with the user, independent review, small steps, kickoff prompt), or implement a written spec on a dev/<slug> branch step by step and open a PR. Use for "/spec <what to build>" or "/spec implement <spec file>".
argument-hint: "<feature request> | implement <specs/.../file.md>"
---

# spec

Two modes, chosen from the arguments:

- `implement <path>` (or the user pastes a spec's kickoff prompt) → **Implement**.
- Anything else → **Create**, with the arguments as the request. No arguments: ask what to spec.

Specs:

- **Folders:** `specs/pending/` (reviewed, not started), `specs/current/` (in progress), `specs/completed/`. The frontmatter `status` tracks the folder: `draft` or `ready` in `pending/`, `in-progress` in `current/`, `completed` in `completed/`. Template: [template.md](template.md).
- **Name:** one file per spec, `YYYY-MM-DD-<slug>.md` (today's date, a 2–5 word kebab-case slug). The slug is the file name without the date and `.md`.
- **Unique slug:** `git fetch --prune`, then add `-2`, `-3`… (part of the slug) while the slug is used by a file in any `specs/` folder on the current branch or `origin/main`, or by a `dev/<slug>` branch locally or on `origin`.
- **Branch:** `dev/<slug>`. The spec lives there from creation to its single PR: it reaches `main` only with its finished implementation, so an abandoned branch leaves `main` untouched.

Repo rules that apply to both modes are in `CONTRIBUTING.md` (test rule, doc rule) and `docs/development.md` (architecture, conventions). Read them once per session, not per step. Never commit secrets or dev passwords; GitGuardian scans every PR.

## Create

Keep the user's time and the token count low: read only what the request touches, ask questions in batches, and never ask something the code already answers.

1. **Understand.** Restate the request in 2–4 lines: goal, who it's for, what's out of scope. If the restatement needs a guess, that guess becomes a question in step 3.
2. **Map the code.** Find every place the change must reach: endpoints, services, repositories, migrations, the system handler, SPA pages/stores, tests, docs, config. Use Grep/Read directly; use one `explore-haiku` subagent only when the sweep spans many areas (a bigger agent only if the sweep needs deeper reasoning). Note what can be reused, what will break (callers, data, API contracts), and any small optimization *inside code this spec already touches* (dead code, duplication, a missing test). Don't propose refactors outside that footprint.
3. **Question loop.** Ask with `AskUserQuestion`, up to 4 questions per round, recommended option first. Each round covers, in this order: gaps or ambiguities, problems you found in the request (conflicts with existing behaviour, data loss, security, migration risk), and optimizations as opt-in choices. Repeat until nothing is open. Don't ask about things with an obvious default; state the default in the spec instead.
4. **Draft.** Write the spec from [template.md](template.md) into `specs/pending/`, `status: draft`. Include the steps (§ Steps) now so the reviewer checks them too. Steps rules:
   - Each step is one commit that builds and passes its tests on its own, small enough for one sitting (about ≤ 300 changed lines).
   - Order: data/migrations → core → API → UI → docs. Each step names its files, its tests and a "Done when" list.
   - Every step that adds or changes behaviour lists the tests that prove it by test class or `*.test.ts` file name (NUnit + NSubstitute, Testcontainers for DB, vitest for the SPA, Playwright for user flows), so Implement can run just those. Playwright flows run only in Finish.
5. **Review.** Start one read-only reviewer: `Agent` with `subagent_type: "Plan"` and this prompt (fill in the path):

   > Review the spec at `<path>` for RnRomManager. Read it, then check it against the code it names (read those files; read `CONTRIBUTING.md` for the repo's rules). Look for: gaps (cases, errors, permissions, migrations, docs, tests not covered), ambiguity (anything two developers could build differently), conflicts with existing code or behaviour, steps that are too big, out of order, or not independently testable, and missing or vague "Done when" items. Report only real problems as a numbered list: `R<n> [blocker|major|minor] <section>: <problem> — <suggested fix>`. If there are none, reply exactly `NO FINDINGS`. Don't rewrite the spec.

   For each finding: fix it in the spec if the code or the user's earlier answers settle it; reject it with a one-line reason if it's wrong; otherwise collect it for the user. Ask the user about all collected findings in one `AskUserQuestion` round. Then send the reviewer a short list of what changed (`SendMessage` to the same agent, so it keeps its context; it's a deferred tool, so load it with `ToolSearch` `select:SendMessage` first, and don't start a new reviewer instead) and ask it to re-check. Stop when it says `NO FINDINGS`, or after **3 rounds**: then put every open finding to the user and apply their answers.
6. **Finish.** Set `status: ready`, fill the kickoff prompt at the bottom with the spec's file name (not a path: the file moves between folders), and record the decisions you and the user made in § Decisions.
7. **Publish.** If the working tree has changes other than the new spec, tell the user and ask before going on. `git switch main && git pull`, `git switch -c dev/<slug>` (if `dev/<slug>` already exists locally or on `origin`, tell the user and ask before going on), commit only the spec (`Spec: <title>`), and `git push -u origin dev/<slug>`. **Never open a PR for the spec:** the branch gets one PR, at the end of Implement, once every step is done and all tests pass. Tell the user the branch name and the kickoff prompt.

## Implement

1. **Find the spec and the state.** The argument may be a path or just the file name. The branch is `dev/<slug>` (the slug from the file name). Run `git fetch --prune` first (so branches deleted on GitHub don't count). All work happens on that branch; never commit spec work to `main`.
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
   3. Run **targeted tests only**: the full suite takes over 10 minutes even sharded, so it runs once, in Finish. Run the test classes the step lists plus the ones covering the code it changed (callers included): `dotnet test tests/<Project> --filter "FullyQualifiedName~<ClassTests>|FullyQualifiedName~<OtherTests>"` per affected project, and integration classes too when the step touches data access (Docker running; if it isn't, append `&Category!=Integration`, say so, and run them in Finish). When the SPA changed: `npm run lint`, `npm run check` and `npx vitest run <affected *.test.ts files>` in `src/RnRomManager.Web`. Don't run whole test projects, `scripts/test-all.ps1` or e2e per step unless the user asks. Fix failures before going on.
   4. Tick the step in the spec, and commit code + spec together: `<title>: step <n> — <step title>`, and push, so the work is backed up and can be resumed from another machine.
   5. Report in 2–4 lines (what changed, test result, anything surprising) and ask the user to continue, change something, or stop. If the user said to run all steps without stopping, continue unless a step fails or the spec turns out wrong.
4. **Spec turns out wrong or unclear:** don't guess and don't silently match the spec to the code. Ask the user, update the spec section and § Decisions in the same commit as the code.
5. **Finish**, after the last step:
   1. `git merge origin/main` (resolve conflicts on the branch, asking the user when the right resolution isn't clear), then run the full suite: `pwsh scripts/test-all.ps1` and `pwsh scripts/test-e2e.ps1` must both pass (Docker running). They are slow, so start them with `run_in_background` and wait for the notification. On failures: fix them on the branch (ask the user if a failure also happens on `origin/main`, i.e. isn't caused by this spec), rerun just the failing tests until they pass, commit, then run the full suite once more. Never open the PR with failing tests.
   2. Check the doc rule: every changed behaviour is in its doc (`docs/`, `README.md`, spec).
   3. `git mv` the spec to `specs/completed/`, set `status: completed`, commit `Complete spec: <title>`.
   4. Push and open a PR from `dev/<slug>` to `main` titled `<title>` — the branch's first and only PR, carrying the spec and its implementation together. The body is the spec's summary, the steps done, test results and anything deferred or risky. Stop there: merging is the maintainer's call.
