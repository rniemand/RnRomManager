---
title: Collections discovery by publisher and genre
status: in-progress
created: 2026-11-15
---

# Collections discovery by publisher and genre

## Summary

Signed-in users can browse the whole library by publisher or genre across platforms from new tabs on `/collections`. Publishers show a logo: a shared override that any signed-in user can upload or remove, else bundled starter art, else text. Split out of the earlier combined "Library curation and ROM bundles" spec.

## Out of scope

- Editing game metadata (publishers or genres) or merging metadata aliases.
- A mirror or index table for metadata values.
- Logos for genres.
- Fetching publisher art from third parties at runtime.

## Behaviour

- **B1.** At `/collections`, add link-based tabs **User collections**, **Publishers**, and **Genres**. A bare URL defaults to User collections. Preserve the existing `q`, paging and owned collection screens. Discovery tabs get shareable URLs through `tab=publishers|genres`, `key` for the selection, and separate `groupPage` and `gamePage` paging parameters (one-based, default 1). API lists use the existing `page` and `pageSize` query parameters. Changing tab clears the selection and paging. Back/forward restores the selection. Indicate the current tab accessibly and keep keyboard navigation working.
- **B2.** Build both indexes from live `games.publishers` and `games.genres` arrays across systems. Treat each array entry as a whole value. MariaDB is the sole normalization authority, with one canonical SQL expression used everywhere (reads, existence checks, logo writes):
  - Trim leading and trailing whitespace with a Unicode-aware `REGEXP_REPLACE`, and discard blanks.
  - Lowercase with `LOWER(CONVERT(value USING utf8mb4) COLLATE utf8mb4_uca1400_as_cs)`, naming that collation explicitly because the JSON columns are `utf8mb4_bin` and case mapping depends on the collation.
  - Compare the resulting bytes exactly (`CAST(... AS BINARY)`). Punctuation and accents are preserved.
  - The public key is the lowercase hexadecimal SHA-256 digest of the normalized UTF-8 bytes, derived from the full JSON string before any display-length limit.
  - The group label is the binary-smallest trimmed spelling.
  - A game contributes at most once to each group and can appear in several groups. Sort groups by normalized name bytes, with deterministic ties, and games by display title, system and GUID. Counts are distinct games.
  - Provide paged group lists and paged selected-group results with system badges and game links. Show meaningful empty states for no groups and for a selection that disappeared.
- **B3.** Signed-in `GET /api/discovery/{kind}` lists paginated groups with `key`, `name`, `gameCount` and, for publishers, `logoUrl` (see B6). `GET /api/discovery/{kind}/games?key=...` returns the existing `GameListItem` page contract. `kind` is exactly `publishers` or `genres`; an unknown kind or group returns 404. Use the existing paging validation and registered system labels. Parameterize exact values and bound the response. Use MariaDB `JSON_TABLE` with `FOR ORDINALITY` and extract complete values from the original JSON by ordinal, so `JSON_TABLE` column widths never truncate a value. Validate keys as exactly 64 lowercase hexadecimal characters (400 otherwise). Limit the returned display name to 255 characters after grouping, with an ellipsis if truncated.
- **B4.** Ship a small local starter catalog in the SPA, keyed by normalized publisher name, initially covering Nintendo, Sega, Capcom, Konami, Bandai Namco, Electronic Arts, Square Enix, Ubisoft, Activision and Atari. Its keys are the SHA-256 of the declared ASCII lowercase aliases, which equals B2's key for ASCII input. Check in redistributable logo assets with source and license records where available, and original typographic name tiles where redistribution terms can't be verified. Other publishers get a text fallback. Historical metadata aliases are distinct groups and share a starter image only when the catalog maps that alias explicitly. Genres use the same interaction with text labels.
- **B5.** Any signed-in user can add, replace or remove the shared logo override for a publisher currently in the index. One override per normalized key, kept when the publisher temporarily disappears from the index.
  - Signed-in `GET /api/discovery/publishers/logo?key=...` serves the override with its detected type, `Cache-Control: private, no-cache` and `X-Content-Type-Options: nosniff`. 404 when there is no override.
  - Multipart `PUT` on the same route saves exactly one PNG or JPEG in the `file` field, up to 512 KiB and 2048 pixels per axis, using the existing `ImageFormats` format and dimension checks. 400 for invalid input, 413 when the request exceeds 576 KiB, 404 for a publisher not in the index, 204 on success. Validate before atomically replacing the row. Last successful writer wins.
  - `DELETE` on the same route removes the override (204), so the starter art or text fallback shows again. 404 when there is no override.
  - Every successful PUT and DELETE writes an audit event (`PublisherLogoReplaced`, `PublisherLogoRemoved`) with the actor and the publisher's key and label in `Details`.
  - Bytes and MIME type live in the database so backups cover them. Preserve existing game image rules and the app's CSP.
- **B6.** Logo precedence and URLs: the API's `logoUrl` is `/api/discovery/publishers/logo?key=<key>&v=<updated_at ticks>` when an override exists and null otherwise, following the collection cover URL pattern. The SPA falls back to the starter catalog by key, then to text. After an upload or removal the SPA refetches the group page, so the changed `v` shows the new image at once.

## Changes

| Area | File(s) | Change |
|---|---|---|
| Data | `src/RnRomManager.Data/Migrations/00NN_publisher_logos.sql` (next free number) | New `publisher_logos` table, below. No existing data changes. |
| Data | `src/RnRomManager.Data/Repositories/DiscoveryRepository.cs`, `PublisherLogoRepository.cs` | Bounded live group/game queries; logo upsert, read and delete. |
| Data | `src/RnRomManager.Data/Repositories/LibraryRepositories.cs` | Reuse the existing `GameListItem` projection for group game pages. |
| Core | `src/RnRomManager.Core/Discovery/` (new) | Repository contracts, key validation, `PublisherLogo`. |
| Core | `src/RnRomManager.Core/Audit/AuditActions.cs` | `PublisherLogoReplaced`, `PublisherLogoRemoved` (added to `All`). |
| Api | `src/RnRomManager.Api/Endpoints/DiscoveryEndpoints.cs`, `PublisherLogoEndpoints.cs`, `AppHost.cs` | Signed-in routes in the normal API and antiforgery group. |
| Web | `src/RnRomManager.Web/src/routes/collections/+page.svelte`, `src/lib/discovery.ts`, `src/lib/components/DiscoveryGroups.svelte`, `DiscoveryGames.svelte`, `PublisherLogoModal.svelte`, `src/lib/api/types.ts` | Tabs, paged discovery, logo editing. |
| Web | `src/RnRomManager.Web/static/publishers/` | Starter art plus a source/license manifest. |
| Docs | `docs/features.md`, `docs/development.md` | Behaviour, logo storage, starter-art attribution. |

`publisher_logos`: `key_digest BINARY(32) NOT NULL` primary key (the SHA-256 bytes), `media_type VARCHAR(16) NOT NULL` (`image/png` or `image/jpeg`), `bytes MEDIUMBLOB NOT NULL` (≤ 512 KiB, checked in code), `updated_by INT NULL` with FK to `users` `ON DELETE SET NULL`, `updated_at DATETIME(6) NOT NULL`. No new configuration or storage mount.

## Risks

- Every discovery read scans `games` with `JSON_TABLE`, `REGEXP_REPLACE`, `LOWER` and `SHA2`, with no index. The Full seed is only about 200 games, so step 3 measures against 50,000 synthetic games: group page and group game page each under 500 ms (median of 5 warm runs) on the Testcontainers MariaDB. If that fails, cache each kind's computed index in memory for up to 60 seconds and record the choice in Decisions.
- Metadata lists can be sparse or inconsistent. Exact grouping with deterministic labels, tested against MariaDB.
- Publisher images come from any signed-in account. Restrict type, size and dimensions, serve same-origin with `nosniff`, audit every change, allow removal, and verify that backups round-trip the bytes.

## Steps

Every step adding a route also updates `tests/RnRomManager.Api.Tests/Integration/EndpointRoleMatrixTests.cs` for the signed-in User role and uses the normal signed-in API and antiforgery group.

- [x] **1. Store publisher logo overrides.** Files: `Data/Migrations/00NN_publisher_logos.sql`, `Data/Repositories/PublisherLogoRepository.cs`, `Core/Discovery/PublisherLogo.cs`. Tests: `PublisherLogoRepositoryTests` (Data Testcontainers) atomic replace, delete, key isolation; `MySqlBackupStoreTests` binary round-trip. Done when: one publisher's replacement is isolated and survives backup and restore.
- [x] **2. Query discovery group pages.** Files: `Core/Discovery/`, `Data/Repositories/DiscoveryRepository.cs`. Tests: `DiscoveryRepositoryTests` (Data Testcontainers) normalization, null and empty arrays, Unicode whitespace, case and accents, long values, multiple values, duplicate counting and stable paging for both kinds. Done when: individual metadata values are listed as groups across systems.
- [x] **3. Query discovery game pages and measure.** Files: `Core/Discovery/`, `Data/Repositories/DiscoveryRepository.cs`, `Data/Repositories/LibraryRepositories.cs`. Tests: `DiscoveryRepositoryTests` exact selection, cross-platform results, rename and removal visibility, case and accents; an `Explicit` 50,000-game timing test. Done when: every matched game is projected once and the 500 ms budget is met or the cache fallback is in place.
- [x] **4. Expose discovery reads.** Files: `Api/Endpoints/DiscoveryEndpoints.cs`, `Api/AppHost.cs`. Tests: `DiscoveryEndpointsTests` (API Testcontainers) auth, response shapes, invalid key and kind, paging validation. Done when: both kinds and both page shapes are usable.
- [ ] **5. Expose publisher overrides.** Files: `Api/Endpoints/PublisherLogoEndpoints.cs`, `Api/Endpoints/DiscoveryEndpoints.cs`, `Api/AppHost.cs`, `Core/Audit/AuditActions.cs`. Tests: `PublisherLogoEndpointsTests` (API Testcontainers) signed-in write and delete, anonymous denial, upload validation (multiple files, oversized fixed-length and chunked bodies), unknown publisher, response headers, audit events. Done when: B5's routes behave as specified and `logoUrl` appears in the index.
- [ ] **6. Mount Collections tabs.** Files: `Web/src/routes/collections/+page.svelte`, `Web/src/lib/discovery.ts` (tab URL helper). Tests: `collections-page.test.ts` (vitest) default tab, preserved legacy query, back/forward. Done when: existing collection actions still work under User collections.
- [ ] **7. Render the discovery group index.** Files: `Web/src/lib/discovery.ts`, `Web/src/lib/components/DiscoveryGroups.svelte`, `Web/src/routes/collections/+page.svelte`, `Web/src/lib/api/types.ts`. Tests: `DiscoveryGroups.test.ts` group paging, counts, empty and error states, stale results for both kinds. Done when: each tab shows its own live groups.
- [ ] **8. Render selected-group games.** Files: `Web/src/lib/components/DiscoveryGames.svelte`, `Web/src/routes/collections/+page.svelte`. Tests: `DiscoveryGames.test.ts` group selection, stale key, cross-platform paging and game links; Playwright browse flow (run in Finish). Done when: group links can be shared and opened directly.
- [ ] **9. Add starter publisher images.** Files: `Web/static/publishers/` (art and manifest), `Web/src/lib/discovery.ts`, `Web/src/lib/components/DiscoveryGroups.svelte`. Tests: `discovery.test.ts` catalog key coverage (keys match B2 for each alias) and fallback order. Done when: starter art renders locally with no third-party request and every asset has a manifest entry.
- [ ] **10. Add publisher logo editing.** Files: `Web/src/lib/components/PublisherLogoModal.svelte`, `Web/src/lib/components/DiscoveryGroups.svelte`, `Web/src/lib/discovery.ts`. Tests: `PublisherLogoModal.test.ts` validation, immediate refresh after upload and removal; Playwright shared replacement with two signed-in accounts (run in Finish). Done when: any signed-in user can replace or remove an override.
- [ ] **11. Document.** Files: `docs/features.md`, `docs/development.md`. Done when: discovery, logo storage and audit, and starter-art attribution are documented and the full repository and Playwright suites pass.

## Decisions

- 2026-11-15 Who can change publisher logos? → any signed-in user, shared (user).
- 2026-11-15 How is misuse handled? → every change is audited and an override can be removed to restore the default (reviewer).
- 2026-11-15 Starter art? → redistributable logos where available, original name tiles otherwise (design).
- 2026-11-15 Where do starter art and overrides merge? → API returns only the override URL; the SPA falls back to starter art, then text (reviewer).
- 2026-11-15 Split from the combined curation/bundles spec to keep each branch reviewable (reviewer).
- 2026-11-15 Backup restore of many 512 KiB logos? → restore's multi-row INSERT also ends a batch at 4 MiB of dumped JSON, since 500 logo rows would exceed MariaDB's 16 MiB `max_allowed_packet` (step 1, code).
- 2026-11-15 Which array entries count? → only JSON strings; numbers, booleans, `null`, nested arrays/objects and non-array documents are skipped like blanks. "Unicode whitespace" is PCRE `\s` plus `\p{Z}` (step 2, code).
- 2026-11-15 Did the live scan meet the 500 ms budget? → no: at 50,000 games the group page took 1.5 s (genres 0.7 s) and a group's game page 0.9 s; grouping 150,000 entries alone took about 0.5 s in MariaDB. Per the Risks fallback, `DiscoveryRepository` (a singleton) keeps each kind's index in memory for 60 s, so metadata renames and removals show within a minute; a deleted game drops out of its page at once. One MariaDB pass returns every entry's key, normalized bytes and trimmed bytes, plus the game order (display title, system, id) in the same snapshot; the app only compares those bytes (label = binary-smallest, groups sorted by normalized bytes) and cuts the label to 255 code points. Group game pages reuse the library list's `GameListItem` projection through a new `GameQuery.GameIds` filter. Warm: group pages under 1 ms, game pages about 7 ms; one cold build about 1.2 s (step 3, measured).

## Kickoff prompt

```text
/spec implement 2026-11-15-collections-discovery.md
```
