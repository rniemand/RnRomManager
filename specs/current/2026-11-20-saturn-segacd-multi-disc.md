---
title: Saturn and Sega CD multi-disc play
status: in-progress
created: 2026-11-20
---

# Saturn and Sega CD multi-disc play

## Summary
Extends PlayStation multi-disc play (`specs/completed/2026-11-20-psx-multi-disc-play.md`, M1–M8) to Saturn (Yabause) and Sega CD (Genesis Plus GX): Disc 1 of a disc set gets **All discs**, `play-disc-set` streams the set with a generated `.m3u`, and the cores swap discs in-session through EmulatorJS's **Disks** menu. EmulatorJS 4.2.3's loader picks a cue or image over an `.m3u` for these cores, so a guarded vendor edit makes it boot the playlist.

## Out of scope
- 3DO (Opera), 32X CD and any other system.
- `.nrg` Saturn discs (they never play), `.pbp`, and `.img` (neither handler accepts it).
- Hiding **All discs** when no Saturn BIOS is installed (S6: the launch stops with a message instead).
- Starting a set on a disc other than Disc 1.
- A higher size cap, and an e2e seed (no seeded Saturn or Sega CD multi-disc game).

## Behaviour
- **S1 Sets per system.** M1's grouping rule holds unchanged for Saturn (`saturn`) and Sega CD (`segacd`) games too; only the per-system limits differ, from each pinned core's playlist reader: PlayStation 2–8 discs with main files `.bin`/`.iso`/`.chd`/`.img` (as today); Saturn 2–**5** (Yabause's `M3U_MAX_FILE`) and Sega CD 2–**4** (Genesis Plus GX's `MAX_DISKS`), both with main files `.bin`, `.iso` or `.chd`. M2's naming is unchanged. Any other system has no sets.
- **S2 Game detail.** M3's `discSet` is filled for PlayStation, Saturn and Sega CD games; `null` for every other system.
- **S3 Endpoint.** `play-disc-set` (M4) serves Saturn and Sega CD sets as it serves PlayStation's, with these differences:
  - A **standalone `.bin`** disc of a Saturn or Sega CD set gets a generated cue in the archive, because today's single-disc launch relies on EmulatorJS generating one (`GameManager.createCueFile`) and a playlist entry is opened as named: entry `<bin stem>.cue`, content `FILE "<bin name>" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n` (UTF-8 without BOM, LF), placed right before the `.bin`; the playlist line names that cue. Standalone `.iso`/`.chd` discs and PlayStation `.bin` discs are unchanged.
  - The firmware-name check (M4) uses the system's firmware names, compared ignoring case: PlayStation `scph5501.bin`; Saturn `saturn_bios.bin`, `sega_101.bin`, `mpr-17933.bin`; Sega CD `bios_CD_U.bin`, `bios_CD_E.bin`, `bios_CD_J.bin`. A standalone member matching one is `409 InvalidPlayDisc` with detail `Disc <n>: a disc file can't be named <the matched name as listed here>` (so PlayStation's text stays `… can't be named scph5501.bin`).
  - A generated cue counts in the case-insensitive name-clash check and in `X-Content-Bytes`, but not in the 2 GiB cap (opened files only, as today).
  - The playlist has no blank lines (Genesis Plus GX adds a blank line as an empty disc entry): one line per disc, each ending in LF, nothing after the last.
  - A `romId` that isn't the Disc 1 of a set of the game's system is `404 NotFound`; a game of any other system is `404 NotFound`.
- **S4 Loader.** A new guarded edit in `vendor-emulatorjs.mjs`, in `emulator.js`'s `downloadRom()` right after the unpacked file names are scanned and before the `isoFile`/cue choice: when `this.getCore()` is `yabause` or `genesis_plus_gx` and an unpacked file's extension is `m3u` (case ignored), `this.fileName` is that file and the `isoFile`/cue choice is skipped. Every other core, and every ZIP without an `.m3u` (all single-disc launches), behaves exactly as upstream. Vendoring fails if the anchor isn't found exactly once.
- **S5 Play URL.** `parsePlayParams` accepts `set=1` with `pcsx_rearmed`, with `yabause`, or with `genesis_plus_gx` only when `disc=1` is also given (Genesis Plus GX also plays cartridges); any other `set` is `null` (*Invalid emulator link.*). `playUrl` is unchanged (the modal already passes `disc` true for a set).
- **S6 Player.** With `set`:
  - Saturn: the optional `saturn_bios.bin` check runs as today, but a 404 (no BIOS) stops the launch before the disc is requested with *Playing all Saturn discs needs a BIOS: ask an Admin to put saturn_bios.bin in the BIOS folder (Paths:Bios).* (`saturnSetBiosMissing`, new in `params.js`), since Yabause aborts a playlist without a real BIOS. The BIOS goes to `/saturn_bios.bin` as today, which is where Yabause looks when EmulatorJS sets no system directory.
  - Sega CD: unchanged from the single-disc Sega CD path (operator-core check, the three BIOS files, `EJS_disableCue = false`), then `play-disc-set`. Genesis Plus GX reads the BIOS next to the content when it gets no system directory, as today.
  - Both load `play-disc-set` with `discError(…, set = true)` wording, as PlayStation does.
- **S7 Game page and modal.** No change: M7's **All discs** button and M8's modal rules (Disc 1's release for battery saves, no **Save state to library**) already key on `discSet`, whatever the core.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` | S1: `Find(string systemId, IReadOnlyList<RomRow> roms, ILookup<long, RomFileRow> files)` (system id first); the public `MaxDiscs` const becomes `public static int MaxDiscs(string systemId)` (8 for `psx`, 5 for `saturn`, 4 for `segacd`, 0 otherwise, which gives no sets); main-file extensions per system. Core uses the literal ids, as the handlers do. |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | S2: `DetailAsync` calls `DiscSets.Find(game.SystemId, …)` for every game (other systems get none), replacing the PlayStation-only check. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | S3: `HandleSetAsync` accepts `psx`, `saturn` and `segacd` games; `PsxBiosName` becomes a per-system firmware-name list; generated cues for Saturn/Sega CD standalone `.bin` discs. |
| Web | `src/RnRomManager.Web/scripts/vendor-emulatorjs.mjs` | S4: the new guarded edit, with its paragraph in the edits header comment. |
| Web | `src/RnRomManager.Web/static/emulator/params.js`, `play.js` | S5/S6: `set` for `yabause` and `genesis_plus_gx` (with `disc`), `saturnSetBiosMissing`, the Saturn set BIOS stop. |
| Docs | `docs/features.md`, `docs/development.md`, `README.md` | features: the Saturn and Sega CD play paragraphs (All discs, the disc limits, the Saturn BIOS requirement), the PlayStation paragraph's Multi-disc sets text (no longer PlayStation only), the `play-disc-set` entry (systems, generated cues, firmware names), `discSet`'s systems; development: the Multi-disc sets bullet (loader edit, generated cue, per-core limits; drop "sets are PlayStation only") and the vendoring guarded-edits list; README line 15: All discs for PlayStation, Saturn and Sega CD with the limits 8 / 5 / 4 discs, and that a Saturn set needs `saturn_bios.bin` (the Saturn clause's "when you supply it" holds only for single discs). |

No migration, config key or stored data change.

## Risks
- **Yabause off-by-one:** Yabause's reader writes a sixth entry past its 5-slot array; S1's cap of 5 keeps playlists inside it.
- **Generated cue for a raw `.bin`:** `MODE1/2352` mirrors what EmulatorJS generates for today's single-disc launch, so a disc that boots alone boots in a set.
- **Memory:** the same 2 GiB cap; most Saturn and Sega CD bin/cue sets are large, so CHD is the realistic format.
- **Saves:** set saves follow M8 (Disc 1's release). How each core names its save for an `.m3u` (Yabause's battery file, Genesis Plus GX's per-BIOS backup RAM) isn't verified; a set's in-browser save may differ from the single-disc one, which M8 already accepts for PlayStation.

## Steps
- [ ] **1. Per-system disc sets (Core)**: S1. Files: `DiscSets.cs`. Tests: `tests/RnRomManager.Core.Tests/Library/DiscSetsTests.cs`: every existing `Find` call gains `"psx"` as its first argument (expectations unchanged), `EightDiscs_AreASet_AndNineAreNot` keeps PlayStation; new cases: Saturn 5 discs → a set and 6 → none; Sega CD 4 → a set and 5 → none; `.img` and `.nrg` members spoil a Saturn and a Sega CD set; `3do` and `gb` → none; `MaxDiscs` per id. Done when: `DiscSetsTests` passes.
- [ ] **2. Detail and endpoint (API)**: S2, S3. Files: `LibraryEndpoints.cs`, `PlayDisc.cs`. Tests in `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.PlayDiscSet.cs`: `Detail_DiscSet_IsNullForALoneDisc_AndOnAnotherSystem` and `PlayDiscSet_NotDiscOne_ALoneDisc_AnotherSystem_OrAMissingFile_Is404` switch their other-system game from Saturn to 3DO (`ThreeDoGame` in `LibraryEndpointsTests.PlayDisc.cs`); `PlayDiscSet_AStandaloneDiscNamedLikeTheFirmware_IsInvalid` keeps its expected text; new: Saturn and Sega CD `discSet`; a Sega CD set of a cue disc, a standalone `.bin` disc (its generated cue right before it, exact cue bytes, the playlist naming it) and a `.chd` disc; a Saturn set streams; a Saturn `SEGA_101.BIN` and a Sega CD `bios_cd_e.bin` member refused with the S3 detail (`… can't be named sega_101.bin`, `… bios_CD_E.bin`); `X-Content-Bytes` counts the generated cue; the playlist has no blank line. `LibraryEndpointsTests.PlayDisc` unchanged. Done when: those classes pass.
- [ ] **3. Loader edit and player**: S4–S6. Files: `vendor-emulatorjs.mjs`, `params.js`, `play.js`. Tests: `src/lib/vendorEmulatorjs.test.ts` (the `guardedEdits` map gains the new anchor and edit, so "copies the src scripts unchanged except the guarded edits" still passes; a new case runs the vendored `downloadRom` selection on `[m3u, cue, chd]` names and gets the `.m3u` for `yabause` and `genesis_plus_gx`, upstream's choice for `pcsx_rearmed`, and upstream's choice for a ZIP without an `.m3u`); `src/lib/emulatorParams.test.ts` ("rejects set=1 with %s" keeps `opera` and `mgba`, drops `yabause`, and checks `genesis_plus_gx` without `disc=1`; new: `set=1` accepted with `yabause` and with `genesis_plus_gx` plus `disc=1`; the `saturnSetBiosMissing` text); `src/lib/emulatorPlay.test.ts` (Saturn set: BIOS 404 stops with the message before the disc is requested; with a BIOS, loads `play-disc-set`; Sega CD set: the BIOS files, then `play-disc-set`). Done when: those files, `npm run lint` and `npm run check` pass, and `npm run predev` vendors without error.
- [ ] **4. Docs**: as in the Changes Docs row; regenerate the codemap. Done when: each listed section is updated and `docs/codemap.md` is regenerated and committed.

## Decisions
- 2026-11-20 Saturn All discs without a BIOS? → shown; the launch stops with a message naming saturn_bios.bin (user)
- 2026-11-20 Does Yabause play an `.m3u` and swap discs? → yes in the vendored build (`EmulatorJS/yabause` e24ebfc: `read_m3u`, disk control), only with a real BIOS in the system directory, which EmulatorJS leaves at `/` where the app writes `saturn_bios.bin`; at most 5 entries (`M3U_MAX_FILE`) (code)
- 2026-11-20 Does Genesis Plus GX? → yes in the pinned operator build (`@emulatorjs/core-genesis_plus_gx@4.2.3`, SHA-256 matching `OperatorCoreManifest`, built from `EmulatorJS/Genesis-Plus-GX` 594cdf3, core.json lists `m3u`): `.m3u` reader, disk control, `MAX_DISKS 4`, blank lines become entries, CD BIOS from the system directory else the content directory (code)
- 2026-11-20 Why a loader edit? → EmulatorJS 4.2.3 prefers `.m3u` only for the `psx` group; for others an `.iso`/`.chd` or `.cue` wins (code)
- 2026-11-20 Standalone `.bin` discs in a Saturn/Sega CD set? → a generated `MODE1/2352` cue, as EmulatorJS's own `createCueFile` makes for a single disc (code)
- 2026-11-20 UI changes? → none; All discs and the modal key on `discSet` (code)
- 2026-11-20 Review round 1: firmware detail keeps the listed name (PlayStation text unchanged); broken tests named per step; `Find`/`MaxDiscs` signatures pinned; README limits pinned (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-20-saturn-segacd-multi-disc.md
```
Starts the spec, or resumes it at the first unticked step.
