---
title: <Title>
status: draft # draft | ready | in-progress | completed
created: <YYYY-MM-DD>
---

# <Title>

## Summary
<2–4 lines: what changes, for whom, why.>

## Out of scope
- <What this spec deliberately doesn't do.>

## Behaviour
<What the user/API/job does after the change. Concrete: inputs, outputs, error codes (ProblemDetails `code`), permissions (Admin/User), edge cases. Number the rules (B1, B2…) so steps and tests can cite them.>

## Changes
| Area | File(s) | Change |
|---|---|---|
| <Data / Core / Api / Web / Docs> | `<path>` | <what and why> |

Migrations, config keys and API contract changes are listed here explicitly, including what happens to existing data.

## Risks
- <Breaking change, data risk, performance, security — and how the spec handles it.>

## Steps
- [ ] **1. <title>**: <deliverables>. Files: `<paths>`. Tests: <what proves it>. Done when: <checkable list>.
- [ ] **2. <title>**: …

## Decisions
- <YYYY-MM-DD> <question> → <answer> (<user | reviewer | code>)

## Kickoff prompt
```text
/spec implement <YYYY-MM-DD-slug>.md
```
Starts the spec, or resumes it at the first unticked step.
