---
title: CHD disc images
status: in-progress
created: 2026-11-14
---

# CHD disc images

## Summary
Sega CD, PlayStation and Saturn accept MAME `.chd` disc images. A `.chd` is a single-file release stored next to the game's ZIP, as a standalone `.iso` is. It never matches the Redump DAT (Redump hashes the `.bin` tracks, and a CHD's own SHA-1 covers its compressed container), so every `.chd` goes to Review, where an Admin resolves it with **Manual**, a **DAT pick** or **Attach** (B3 gives the stored name for each). A stored `.chd` plays in the browser through play-disc on the system's existing core.

## Out of scope
- Matching a `.chd` against the DAT: no CHD decoding in .NET and no `chdman`. A `.chd`'s `identification.method` is never `Dat` (a Review DAT pick records `Manual` with the DAT name and the `Unverified` flag).
- Converting between `.chd` and bin/cue, or checking that a `.chd` and a bin/cue release are the same disc.
- Attaching a `.chd` to a game as "the same disc in another format": Attach keeps its flags (Hack, Translated, `Disc <n>`; `None` only for `AmbiguousGroup`).
- `.chd` for PSP, GameCube, Wii, Xbox 360 or any other system; `.chd` inside a `.zip`/`.7z` (disc images are dropped loose, as today); a `.chd` named in a `.cue`.
- Any player change beyond what B5 states, and real-game boot checks before the PR: the maintainer does them after merge, as for the previous browser-play specs.

## Behaviour
- **B1 Handlers.** `.chd` is appended to `RomExtensions` of `PlayStationHandler` (`.bin`, `.cue`, `.iso`, `.chd`), `SaturnHandler` (`.bin`, `.cue`, `.nrg`, `.chd`) and `SegaCdHandler` (`.bin`, `.cue`, `.iso`, `.chd`). `[0]` stays `.bin`. Size limits, families, `FileSets`, DATs and cores are unchanged. The README supported-systems rows list the new extension.
- **B2 Ingest.** Nothing new in Core. `IngestSetPlanner` already makes any file that isn't a `.cue` or a cue-named `.bin` a lone unit, so a `.chd` is its own release, unless a `.cue` names it: then the cue and the `.chd` wait together as `Skipped` `UnsupportedSetMember`, as today. It has no header (`ReadHeader` is null for all three systems) and no DAT entry, so it goes to Review as `NoDatMatch` with the folder's system, suggestions from the file name. A `.chd` in another family's folder (e.g. `ingest/md/`) is `UnsupportedType`, as any extension the family doesn't list. A `.chd` byte-identical to one already pending in Review is a `Duplicate` (the lone-file "same as pending review source" path). Per-file size limits apply (16 bytes–1 GiB for all three).
- **B3 Review.** A `.chd` item is not a set member, so the existing lone-file rules apply unchanged (no Core change):
  - **Dismiss** is allowed.
  - **Manual** creates a game; the file is stored next to the ZIP under the name built from the request (`Sanitize(title + regions + revision + flags)`) plus a lower-case `.chd`, method `Manual`.
  - **DAT pick** stays available: stored as `<DAT game name> (Unverified).chd`, method `Manual` with the DAT name recorded, flags gaining `Unverified`.
  - **Attach** to a game of that system with flag Hack, Translated or `Disc <n>`: stored as `<sanitized source stem> (<flag>).chd` (a stem that already holds `(Disc <n>)` isn't suffixed again).

  `NameTaken`/`NameTooLong` apply as for other `FileSets` files.
- **B4 play-disc.** `PlayDisc.ShapeProblem` accepts a single `.chd` (any case) for `psx`, `saturn` and `segacd`, streamed unchanged as `application/octet-stream` like a standalone `.iso`. The single-file message becomes "a single-file disc must be a .bin, .iso or .chd". A `.chd` in a multi-file release is `409 InvalidPlayDisc` (the set rule still demands one `.cue` and `.bin` tracks). PSP is unchanged (a single `.iso` only). The 1 GiB cap is unchanged.
- **B5 Play eligibility and player.** No rule changes: `canPlay` (`emulator.ts`) and the games list's `playRomId` (`LibraryEndpoints`) already allow any external release on `pcsx_rearmed`, `yabause` and `genesis_plus_gx` except `.nrg`, so a `.chd` release shows **Play** and quick play, with `playDisc` true. `play.js` loads it from play-disc and hands it to EmulatorJS as a `File` named after the release (`contentFileName`, which keeps the extension). EmulatorJS 4.2.3 then picks it:
  - Genesis Plus GX and Yabause list `chd` in `core.json`, so EmulatorJS's ISO branch selects the `.chd`.
  - PCSX ReARMed's `core.json` doesn't list `chd`, but its binary is built with libchdr (`chd_open`, valid extensions `…|chd|iso|exe`); EmulatorJS falls back to the only file written, the `.chd`, as it does for a standalone `.iso` today.

  Sega CD's BIOS flow and `EJS_disableCue = false` are unchanged (cue generation only runs when no ISO-type file is found).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Sony/PlayStationHandler.cs`, `src/RnRomManager.Systems.Sega/SaturnHandler.cs`, `src/RnRomManager.Systems.Sega/SegaCdHandler.cs` | `.chd` in `RomExtensions`; doc comments (B1). |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | Single `.chd` shape and message; doc comment (B4). |
| Web | `src/RnRomManager.Web/src/lib/emulator.ts` | `canPlay` doc comment names `.chd` (B5); no logic change. |
| Docs | `README.md`, `docs/features.md`, `docs/deployment.md`, `docs/zip-format.md`, `docs/adding-a-system.md` | See Step 5. |

No migration, no config key. API contract: unchanged shapes; play-disc returns 200 for a `.chd` release where it would have returned 409 (none can exist today, since ingest refused `.chd`).

## Risks
- **Unverified boots.** CHD loading is inferred from `core.json`, the binaries' strings and EmulatorJS's loader; the maintainer boots a real `.chd` per system after merge. If PCSX ReARMed fails to boot one, the fix is a follow-up (`canPlay`/`playRomId` refusing `.chd` on `psx`, as for `.nrg`).
- **Duplicate discs.** The same disc as bin/cue and as `.chd` are two releases, never a `Duplicate`; that's by design and stated in `docs/features.md`.
- **Browser memory.** A `.chd` is smaller than its bin/cue, so the existing 1 GiB cap covers it.

## Steps
Steps 2–3 run integration tests (category `Integration`), which need Docker. Playwright runs only in Finish; this spec adds no e2e case.

- [x] **1. Handlers** (B1): `.chd` appended in the three handlers; README table rows' format cells gain `` `.chd` ``.
  - Files: the three handlers, `README.md` (three rows only), `tests/RnRomManager.Systems.Sony.Tests/PlayStationHandlerTests.cs`, `tests/RnRomManager.Systems.Sega.Tests/SaturnHandlerTests.cs`, `tests/RnRomManager.Systems.Sega.Tests/SegaCdHandlerTests.cs`.
  - Tests: the three handler test classes (exact `RomExtensions`, `[0]` still `.bin`), `SupportedSystemsTableTests`.
  - Done when: those pass and the solution builds with warnings as errors.
- [x] **2. Verify `.chd` ingest and Review** (B2, B3): Testcontainers cases with synthetic files (never real images), no Core change expected (if one is needed, stop and ask):
  - Files: `tests/RnRomManager.Api.Tests/Integration/IngestPipelineTests.cs`, `tests/RnRomManager.Api.Tests/Integration/ReviewEndpointsTests.cs`.
  - `IngestPipelineTests`:
    - for each of `psx`, `saturn`, `segacd` (`[TestCase]`), a lone `.chd` → Review `NoDatMatch` with that system;
    - a `.chd` beside a DAT-matched cue + bin in `ingest/segacd/` → the set is one release and the `.chd` its own Review item;
    - a `.chd` in `ingest/md/` → `UnsupportedType`;
    - a `.cue` naming a `.chd` in `ingest/psx/` → both `Skipped` `UnsupportedSetMember`, left in ingest;
    - two byte-identical `.chd` files in `ingest/psx/` → one Review item and one `Duplicate`.
  - `ReviewEndpointsTests`:
    - a `segacd` `.chd` resolved with **Manual** (title `Sonic CD`, regions `USA`) is stored next to the ZIP as `Sonic CD (USA).chd`, method `Manual`;
    - a `saturn` `.chd` resolved with a **DAT pick** is stored as `<DAT game name> (Unverified).chd`;
    - a `psx` `.chd` named `Game.chd` **Attach**ed with flag `Disc` 2 to a game holding a bin/cue disc 1 becomes a second release `Game (Disc 2).chd` with flag `Disc 2`;
    - a `.chd` item can be dismissed.
  - Done when: those cases and the existing `IngestPipelineTests`/`ReviewEndpointsTests` pass.
- [x] **3. play-disc** (B4, B5 API side): the shape rule and message.
  - Files: `src/RnRomManager.Api/Endpoints/PlayDisc.cs`, `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.PlayDisc.cs`, `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.cs`.
  - Tests: `LibraryEndpointsTests.PlayDisc`: a single `.chd` (and `.CHD`) streams unchanged as `application/octet-stream` with its length for `psx`, `saturn` and `segacd`; a `.chd` exactly at 1 GiB → 200, one byte over → `PlayDiscTooLarge`; a `.chd` in a cue set → `InvalidPlayDisc`; a PSP `.chd` → `InvalidPlayDisc`; a new case: a lone Saturn `.nrg` release → `InvalidPlayDisc` whose detail contains "a single-file disc must be a .bin, .iso or .chd". `LibraryEndpointsTests`: a `segacd` game whose external primary is a `.chd` has `playRomId` set and `playDisc` true.
  - Done when: those pass.
- [x] **4. Player checks** (B5 SPA side): no player logic change expected (if one is needed, stop and ask); `canPlay`'s comment.
  - Files: `src/RnRomManager.Web/src/lib/emulator.ts` (comment), `src/RnRomManager.Web/src/lib/emulator.test.ts`, `src/RnRomManager.Web/src/lib/emulatorPlay.test.ts`.
  - Tests: `emulator.test.ts`: `canPlay` is true for `pcsx_rearmed`, `yabause` and `genesis_plus_gx` with an external `.chd`. `emulatorPlay.test.ts`: for each of the three cores, a `disc=1` launch named `Game.chd` requests play-disc and sets `EJS_gameUrl` to a `File` named `Game.chd`; the Sega CD case still fetches the BIOS first and sets `EJS_disableCue === false`.
  - Done when: `npm run lint`, `npm run check` and those vitest files pass.
- [x] **5. Docs**:
  - `README.md`: the Game detail Play bullet (line 15) adds `.chd` to the PlayStation, Saturn and Sega CD single-file shapes; a sentence that a `.chd` always goes to Review (never DAT-matched).
  - `docs/features.md`:
    - the ingest paragraph (line ~124): PlayStation "(a `.cue` with its `.bin` tracks, or a `.bin`/`.iso`/`.chd`)", Saturn "(… or a `.nrg`/`.chd`)";
    - § Disc sets, Sets bullet: Sega CD in the folder list is unchanged; "No match" (line ~162) ends "A lone `.iso`, `.wbfs`, `.nrg` or `.chd` can be resolved", plus a new bullet: a `.chd` never matches the DAT and always goes to Review; a `.cue` naming a `.chd` waits as `UnsupportedSetMember`; a `.chd` and a bin/cue of the same disc are separate releases and can't be attached as the same disc;
    - "Single files" (line ~163): "an `.iso` or `.chd` (…)";
    - the PlayStation, Saturn and Sega CD Play bullets: a standalone `.chd` plays;
    - `play-disc` (line ~310): "A standalone `.bin`, `.iso` or `.chd` (PSP: only a single `.iso`; Saturn has no stored `.iso`) streams unchanged", and the 409 list's single-file wording.
  - `docs/deployment.md`: the `/app/ingest` row: `ingest/psx/` "…, or `.bin`/`.iso`/`.chd`", `ingest/saturn/` "…, or `.nrg`/`.chd`", `ingest/segacd/` "…, or `.bin`/`.iso`/`.chd`".
  - `docs/zip-format.md`: the `psx`, `saturn`, `segacd` layout lines name `.chd`.
  - `docs/adding-a-system.md` line 66: `.chd` leaves the unsupported list; a sentence: PlayStation, Saturn and Sega CD take `.chd` as a single-file format, never part of a set.
  - Tests: `SupportedSystemsTableTests`.
  - Done when: each sentence listed above is in its file, and `grep -n "chd" docs/adding-a-system.md` no longer shows `.chd` among the unsupported formats.

## Decisions
- 2026-11-14 How is a `.chd` identified? → Review only (`NoDatMatch`); no CHD decoding, no `chdman` (user)
- 2026-11-14 Does a `.chd` release play in the browser? → yes, streamed as-is through play-disc (user)
- 2026-11-14 Which systems? → Sega CD, PlayStation and Saturn (user)
- 2026-11-14 Core support → Genesis Plus GX 4.2.3 `core.json` lists `chd` (npm package checked); vendored Yabause lists `chd`; vendored PCSX ReARMed omits it from `core.json` but its wasm has libchdr and `chd` in its valid extensions (code)
- 2026-11-14 Attach a `.chd` as the same disc as a bin/cue release → out of scope; Attach keeps its flags (user)
- 2026-11-14 Manual name → built from the request like any lone disc file, not the source name (reviewer, code)
- 2026-11-14 DAT pick for a `.chd` → stays available, stored as `<DAT game> (Unverified).chd` (reviewer, code)
- 2026-11-14 Extension order → `.chd` appended, `[0]` stays `.bin` (code: `[0]` is the Review fallback for embedded systems only; external releases keep their source extension)

## Kickoff prompt
```text
/spec implement 2026-11-14-disc-chd-support.md
```
Starts the spec, or resumes it at the first unticked step.
