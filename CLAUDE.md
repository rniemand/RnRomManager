# CLAUDE.md

## Exploration

Start from `docs/codemap.md` (generated index: API routes, SPA pages, jobs, system handlers, files per folder, tests, and doc section line numbers) and `docs/recipes.md` (where each common change must reach). Grep the codemap for the area you need rather than reading it whole, then grep or read the exact files and line ranges it points to. Read large docs (`docs/features.md`, `docs/development.md`) by section, never whole.

Use the `explore-haiku` subagent (`.claude/agents/explore-haiku.md`) for search sweeps the codemap can't answer (e.g. every caller of a method). Only use the larger `Explore` or `general-purpose` agents when the task needs deeper reasoning than locating code (e.g. a review, audit or design judgement).

At the end of every change (each spec step, and any other commit that touches code or docs), run `pwsh scripts/codemap.ps1` and commit `docs/codemap.md` with it. CI's `codemap` job and `scripts/test-all.ps1` fail when it's stale. Resolve a merge conflict in it by regenerating, never by hand.

## Code style

Follow `docs/style-guide.md` for every code change; the `CodeStyleTests` guard enforces its structural rules.
