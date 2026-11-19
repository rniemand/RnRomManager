# CLAUDE.md

## Exploration

Start from `docs/codemap.md` (generated index: API routes, SPA pages, jobs, system handlers, files per folder, tests, and doc section line numbers) and `docs/recipes.md` (where each common change must reach). Then grep or read the exact files and line ranges they point to. Read large docs (`docs/features.md`, `docs/development.md`) by section, never whole.

Use the `explore-haiku` subagent (`.claude/agents/explore-haiku.md`) for search sweeps the codemap can't answer (e.g. every caller of a method). Only use the larger `Explore` or `general-purpose` agents when the task needs deeper reasoning than locating code (e.g. a review, audit or design judgement).

After adding or moving routes, pages, jobs, files or doc headings, run `pwsh scripts/codemap.ps1` and commit the updated `docs/codemap.md` with the change.
