---
title: Amiga multi-disk play
status: in-progress
created: 2026-11-20
---

# Amiga multi-disk play

## Summary
A multi-disk Commodore Amiga game (each TOSEC disk its own release, named `… (Disk <n> of <m>) …`) gets an **All disks** action on Disk 1. It boots the whole set in one PUAE session, and EmulatorJS's **Disks** menu swaps disks. This extends PlayStation/Saturn/Sega CD multi-disc play (`specs/completed/2026-11-20-psx-multi-disc-play.md` M1–M8, `specs/completed/2026-11-20-saturn-segacd-multi-disc.md` S1–S7) to floppies stored inside the game's ZIP. Today a multi-disk Amiga game can only reach Disk 1 in the browser, and about half the libretro TOSEC Amiga DAT (20,434 of 44,057 ROMs) is multi-disk.

## Out of scope
- Commodore 64 (and PET, VIC-20): No-Intro C64 names have no `(Side 1)` flag, side 2 often adds `(2 sides)`, and many games lack a side 1 entry. That's a follow-up spec.
- Sets of more than 20 disks (PUAE's disk table, F1).
- Any change to ingest, naming or storage: disks stay separate releases, and nothing about sets is stored.
- `.ipf`, `.dms`, `.hdf`, WHDLoad and `.m3u` ingest.
- Starting a set on a disk other than Disk 1, library quick play, global search, `?play=` (B27) and the Saves tab (they launch one release, as for disc sets).
- Library save states for a set launch (M8 applies unchanged).
- PUAE options such as a second floppy drive (`puae_floppy_multidrive`): the core's defaults stand.
- An e2e seed (no seeded multi-disk game; adding one shifts every seeded count assertion).

## Behaviour
- **F1 Disk sets.** A release's disk token is the one match of `\(Disk ([0-9]+) of ([0-9]+)\)` in its `FileName` (case ignored, ASCII digits, both parsed as ints, so `Disk 01 of 02` is 1 of 2). A release with no match or more than one, or with `n` outside `1..m`, is never in a set. Among an Amiga (`amiga`) game's releases with a token, each one's **set key** is its total `m` plus its `FileName` with the token removed and every `[…]` group (`\[[^\]]*\]`) removed, compared ordinally. Example: `'Allo 'Allo! Cartoon Fun! (1993)(Alternative)(Disk 1 of 2)[cr CSL].adf` and `'Allo 'Allo! Cartoon Fun! (1993)(Alternative)(Disk 2 of 2).adf` share the key `2` + `'Allo 'Allo! Cartoon Fun! (1993)(Alternative).adf`. A group is a **disk set** when it has exactly `m` releases with `2 ≤ m ≤ 20`, numbered exactly `1..m` with no repeats, and every `FileName` ends in `.adf` (case ignored). That means the game holds exactly one release for each disk. Any other group gets no All disks: a missing disk, two candidates for one disk (for example `[cr CSL]` and `[cr QTX]` dumps of Disk 1), one disk, or over 20 disks. The 20-disk cap is PUAE's `DC_MAX_SIZE` in `libretro/libretro-dc.h` (EmulatorJS/libretro-uae). The rules use only `FileName`, never `Flags`, `Regions` or `BaseTitle`, because `NoIntroName.Parse` takes no tags from TOSEC names (their groups have no leading space). Sets are found per game at request time.
- **F2 Set name.** Disk 1's `FileName` stem with its token removed, plus `.m3u`. The `[…]` groups are kept. For the example: `'Allo 'Allo! Cartoon Fun! (1993)(Alternative)[cr CSL].m3u`.
- **F3 Game detail.** M3's `discSet` (`{ name, romIds }` on a set's Disk 1 release, `null` elsewhere) is filled for Amiga games too, from F1/F2. Every other system is unchanged.
- **F4 Ingest (pinned, unchanged).** Two disks of one TOSEC game ingest as two releases of **one** game, each under its DAT ROM name. The libretro DAT's game names have the form `Title (Disk n)[tags]` (for example `'Allo 'Allo! Cartoon Fun! (Disk 1)[cr CSL]`), and the ROM names carry the full TOSEC name. The test fixture is DAT games `Twin Orbit (Disk 1)` and `Twin Orbit (Disk 2)`, with ROMs `Twin Orbit (1990)(Synth)(Disk 1 of 2).adf` and `Twin Orbit (1990)(Synth)(Disk 2 of 2).adf`, each with its own bytes, and they land in one game with `BaseTitle` `Twin Orbit`. A new test pins this. No ingest code changes.
- **F5 Endpoint.** `play-disc-set` (M4) also serves Amiga sets: `GET /api/games/{id}/roms/{romId}/play-disc-set` with the same access (any signed-in user), `MaintenanceExempt`, `Cache-Control: no-store`, no audit, and the same response shape: one uncompressed `application/zip`, UTF-8 entry names at the root, `X-Content-Bytes`, no `Content-Length`.
  - The playlist `<F2 name>` comes first: one line per disk in disk order, `Disk <n>.adf` (`Disk 1.adf`, `Disk 2.adf`…), each ending in LF and nothing after the last line, UTF-8 without BOM. Then each disk, in order, as an entry `Disk <n>.adf` holding the release's stored ROM bytes unchanged (`LibraryZip.RomEntry(FileName)` in the game's ZIP). EmulatorJS labels each disk in its **Disks** menu by entry name, so the menu reads `Disk 1`, `Disk 2`…
  - Each disk is read in full before the response starts, one `LibraryFiles.ReadEntry` call per disk, bounded by the release's `SizeBytes`, as GP3 does. So a failure is never a `200` cut off mid-body. Each call reopens the game's ZIP; that's accepted, at 20 reopens at most.
  - **Guard:** every disk of the set must have `SystemId == "amiga"` and `ExternalFileName` null, otherwise `404 NotFound`.
  - **Errors:**
    - `404 NotFound`: an unknown game or release, a `romId` that isn't the Disk 1 release of an F1 set, a failed guard, or a missing ZIP or missing disk entry. Both come from `ReadEntry`'s `FileNotFoundException`/`DirectoryNotFoundException` mapping; `LibraryZip.OpenEntry` throws `FileNotFoundException` for a missing entry.
    - `409 InvalidPlayDisc`: an empty disk, built with `PlayDisc.Invalid`, so the title is `The disc can't be played: Disk <n>: '<FileName>' is empty.`
    - `500 IoError` (`The game's ZIP can't be read`, from `ReadEntry`): an entry larger than its `SizeBytes` (`LimitExceededException`), or any other read failure.
    - `401` when signed out; `403 PasswordChangeRequired` for a session that must change its password.
  - The 2 GiB cap (`PlayDisc.MaxBytes`) and the firmware-name check don't apply: 20 × 4 MiB is far below the cap, and the entries are aliased, so no disk can take a firmware name.
- **F6 Play URL.** `parsePlayParams` (`params.js`) also accepts `set=1` with `puae`, only when `disc=1` is also given, as it does for `genesis_plus_gx`. Any other `set` gives `null` (*Invalid emulator link.*). `playUrl` is unchanged: the modal already passes `disc` true for a set (`disc || discSet !== null`), so a set URL carries `&disc=1&set=1`.
- **F7 Player.** With `disc` and `set`, `play.js` takes its existing disc path for `puae` (`loadDisc` on `play-disc-set`). The status while fetching is `Downloading disks…` for `puae` (`Downloading disc…` for every other core, unchanged). `discError(status, code, core, set)` gains a PUAE branch. It comes right after the `401` check and before the existing `PlayDiscTooLarge` branches, so `puae` messages read:
  - `401`: the existing *session expired* text;
  - `InvalidPlayDisc`: *These disks can't be played in the browser: one of them is empty.*
  - any other code, or none: *Couldn't load the disks.* with no status, else *Couldn't load the disks (HTTP <status>).*

  Other cores' texts are unchanged. EmulatorJS 4.2.3 then unpacks the ZIP and boots the `.m3u`: PUAE's `core.json` lists `cue`/`m3u`, the ZIP has no `.cue`/`.ccd`/`.iso`, so `downloadRom()` picks the playlist. No vendored-loader edit is needed. The content `File` is named `contentFileName('puae', game, name)`. No BIOS changes: PUAE keeps booting its built-in AROS Kickstart.
- **F8 Game page.** M7's button shows on an Amiga set's Disk 1 with floppy wording: text `All disks`, `aria-label="Play all disks of <F2 name>"`, tooltip `Play all disks in one session`. The same `btn btn-sm btn-outline-success` class and `<Icon name="play" />` icon apply, as does the rule that it shows only when Play shows. The wording comes from a new `emulator.ts` helper `setMedia(core: string | null): 'disks' | 'discs'` (`'disks'` for `puae`, else `'discs'`). Disc sets keep `All discs` and their texts unchanged.
- **F9 Modal.** M8 applies unchanged: the title is `Play <F2 name>`, the iframe title `Emulator: <F2 name>`, battery saves use Disk 1's release, and **Save state to library** is hidden.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` | F1/F2: `Find` delegates `amiga` to a new private floppy finder (token regex, set key, `.adf` only); `MaxDiscs("amiga")` is 20. A new `public static int? DiskNumber(string fileName)` holds the token rule. The PlayStation/Saturn/Sega CD paths are unchanged. The class and `MaxDiscs` summaries name Amiga and PUAE's `DC_MAX_SIZE`. |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | F3: comment only. `DetailAsync` already calls `DiscSets.Find(game.SystemId, …)` for every game; the comment above it (line 487, "PlayStation, Saturn and Sega CD; other systems have none") adds Amiga. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | F5: `HandleSetAsync` sends `amiga` games to a new private `FloppySetAsync` before the `FirmwareNames` lookup. That method runs the F5 guard, reads disks with `LibraryFiles.ReadEntry`, and writes them through the existing `DiscResult` (playlist head, then `Disk <n>.adf` entries as `MemoryStream`s). A new `public const string AmigaSystemId = "amiga"`. The `HandleSetAsync` and `FirmwareNames` summaries are updated: `FirmwareNames` lists the disc systems, and Amiga floppies are aliased. |
| Web | `src/RnRomManager.Web/static/emulator/params.js` | F6/F7: `set` with `puae` + `disc`; PUAE wording in `discError`. |
| Web | `src/RnRomManager.Web/static/emulator/play.js` | F7: `Downloading disks…` for `puae`. |
| Web | `src/RnRomManager.Web/src/lib/emulator.ts` | F8: `setMedia`. |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | F8: the button's text, `aria-label` and tooltip from `setMedia(game.browserCore)`. |
| Docs | `README.md`, `docs/features.md`, `docs/development.md` | README line 15: replace "one disk per launch" with All disks for Amiga (up to 20 disks; one release per disk). features: the Amiga play text, the Amiga ingest sentence "each disk of a multi-disk game is its own release" (add that All disks plays them together), the `GET /api/games/{id}` `discSet` systems, and the `play-disc-set` entry (Amiga shape, errors). development: the Multi-disc sets bullet (Amiga floppies, aliasing, no loader edit). |

No migration, config key, stored data or API contract change beyond `discSet` now appearing on Amiga games, an additive value on an existing nullable field.

## Risks
- **Variant ambiguity:** a game holding several dumps of one disk gets no All disks (F1). This is deliberate, because a guess could pair a cracked Disk 1 with a disk 2 it doesn't load. Each disk's Play still works.
- **Floppy writes:** PUAE may write to a disk in the browser's memory filesystem. Like single-disk play today, the changes are lost on close; nothing is written back to the library.
- **Memory:** each disk is buffered (F5), and `ReadEntry`'s `MemoryStream` plus `ToArray()` briefly doubles each one. TOSEC ADFs are 462 KiB–2.2 MiB (HD disks at the top), so 20 disks is at most about 44 MiB held, and 4 MiB per disk at the handler's limit (80 MiB) in theory.
- **Disk labels:** the aliases hide the TOSEC names in the Disks menu (`Disk 1`…). The order matches the playlist, so the label is right.
- **Unverified with real dumps:** that PUAE's playlist boots Disk 1 and swaps disks through EmulatorJS's menu is read from the code (`libretro-dc.c`'s `dc_parse_m3u`, EmulatorJS `downloadRom()`), not run. Finish includes one manual check with a real two-disk set.

## Steps
- [x] **1. Amiga disk sets (Core)**: F1, F2. Files: `DiscSets.cs`. Tests: `tests/RnRomManager.Core.Tests/Library/DiscSetsTests.cs`, with new cases:
  - a 2-disk set whose Disk 1 has a `[cr CSL]` tag and Disk 2 none forms one set named from Disk 1 (F2 example);
  - a 3-disk set listed out of order comes back in disk order;
  - each of these gives no set: a missing disk; two candidates for Disk 1; a single disk; `n` above `m`; two tokens in one name; a non-`.adf` member;
  - 20 disks form a set and 21 don't;
  - `Disk 01 of 02` counts as 1 of 2, and the token is matched ignoring case;
  - two keys in one game (two publishers) give two sets;
  - `DiskNumber` returns the number or null;
  - `MaxDiscs_IsPerSystem` gains `[TestCase("amiga", 20)]`;
  - the existing PlayStation/Saturn/Sega CD cases pass unchanged.
  Done when: `DiscSetsTests` passes.
- [x] **2. Ingest pin and detail (API)**: F3, F4. Files: `LibraryEndpoints.cs` (comment only; fix code only if a test shows a gap). Tests:
  - `tests/RnRomManager.Api.Tests/Integration/IngestPipelineTests.cs`: the F4 two-disk game lands as two releases of one game under their DAT ROM names. Add the two `Twin Orbit` `FixtureGame`s to the `[IngestSeeder.AmigaDat]` list in the local fixture dictionary at line 176, each with its own bytes (a new `Atari(901_120, <seed>)` array per disk, as `AmigaDisk` is built). If that shifts an existing count assertion, use a test-local DAT instead and say so in the commit.
  - `LibraryEndpointsTests.PlayDiscSet.cs`: an Amiga game written with `TestLibrary.WriteZipAsync` and mirrored through `ILibraryMirror.ReplaceAsync`, as `LibraryEditorSaveTests.cs` lines 55–57 do (a new `AmigaGame` helper in that file). It has `discSet` on Disk 1 with the F2 name and ordered ids, and `null` on Disk 2 and on an ambiguous game.

  Done when: those tests pass and the existing `LibraryEndpointsTests` cases pass unchanged.
- [x] **3. Floppy `play-disc-set` (API)**: F5. Files: `PlayDisc.cs`. Tests in `LibraryEndpointsTests.PlayDiscSet.cs` (new cases):
  - a 2-disk Amiga set streams the exact playlist bytes, then `Disk 1.adf` and `Disk 2.adf` byte-exact, with `X-Content-Bytes`;
  - a `romId` that is Disk 2, or a release of an ambiguous game, is 404;
  - a disk with a non-null `ExternalFileName` is 404 (the F5 guard);
  - an empty disk gives 409 with the exact title `The disc can't be played: Disk 1: '<FileName>' is empty.`;
  - a missing disk entry gives 404, and an entry larger than its `SizeBytes` gives `500 IoError`;
  - the empty, missing and over-size fixtures change the ZIP after `TestLibrary.WriteZipAsync` writes it, opening it with `ZipFile.Open(…, ZipArchiveMode.Update)` as `AuditLibraryEditTests.cs` line 202 does: the entry is replaced with an empty one, deleted, or rewritten larger than its `SizeBytes`;
  - 401 when signed out, 403 for a forced password change.

  The existing PlayStation/Saturn/Sega CD set tests pass unchanged, and `EndpointRoleMatrixTests` is unchanged (same route). Done when: those classes pass.
- [ ] **4. Player and game page (Web)**: F6–F9. Files: `params.js`, `play.js`, `emulator.ts`, `routes/games/[id]/+page.svelte`. Tests:
  - `src/lib/emulatorParams.test.ts`: `set=1` accepted with `puae` + `disc=1`, refused without `disc`; PUAE `discError` texts; other cores' texts unchanged;
  - `src/lib/emulatorPlay.test.ts`: a `puae` set URL fetches `play-disc-set`, shows `Downloading disks…`, and a 409 shows the PUAE text;
  - `src/lib/emulator.test.ts`: `setMedia`;
  - `src/routes/games/game-page.test.ts`: an Amiga Disk 1 with `discSet` shows `All disks` with its `aria-label` and tooltip and opens the modal with a `&disc=1&set=1` URL; the PlayStation `All discs` case is unchanged.

  Done when: those files, `npm run lint` and `npm run check` pass.
- [ ] **5. Docs**: as in the Changes Docs row; regenerate the codemap. Done when: each listed doc section is updated, "one disk per launch" is gone from README, and `docs/codemap.md` is regenerated and committed. Finish also plays a real two-disk Amiga set once, swapping to Disk 2 from the Disks menu, and notes the result in the PR.

## Decisions
- 2026-11-20 Which systems? → Amiga only; C64 sides are a follow-up (user)
- 2026-11-20 How are disks grouped? → the disks the game holds, keyed by name minus the disk token and `[…]` tags, exactly one per disk (user)
- 2026-11-20 Sets over 20 disks? → no All disks, as PlayStation over 8 (user)
- 2026-11-20 Saves for a set launch? → M8's rules: Disk 1's battery save, no library states (user)
- 2026-11-20 Largest set? → 20, PUAE's `DC_MAX_SIZE` (code)
- 2026-11-20 Does EmulatorJS boot the `.m3u` for PUAE without a loader edit? → yes: `core.json` lists `cue`/`m3u`, and with no `.cue`/`.ccd`/`.iso` in the ZIP `downloadRom()` picks the `.m3u` (code)
- 2026-11-20 Do a game's disks share one library game today? → yes by `BaseTitle` from the DAT game name (`… (Disk n)` is cut at the first ` (`); pinned by a test in step 2 (code)
- 2026-11-20 Entry names in the archive? → aliased `Disk <n>.adf`: Disks menu labels, and no TOSEC punctuation in playlist lines (code)
- 2026-11-20 New endpoint or `play-disc-set`? → `play-disc-set`, so the player's set path is shared (code)
- 2026-11-20 Review round 1 fact fixes: the F4 fixture uses a new title (`Twin Orbit`) so it doesn't join the `Disk Orbit` game; the libretro game names are `Title (Disk n)[tags]`, as the DAT shows, not the ROM stem; the 409 uses `PlayDisc.Invalid`'s title; a missing entry is 404 and an over-size entry is `500 IoError` (round 2); the mirror example is `LibraryEditorSaveTests`; HD ADFs reach 2.2 MiB (reviewer)
- 2026-11-20 Floppy guard? → every disk is `amiga` and embedded, otherwise 404 (reviewer)
- 2026-11-20 Open the game ZIP once for all disks? → no, one `ReadEntry` per disk (at most 20 reopens) (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-20-amiga-multi-disk-play.md
```
Starts the spec, or resumes it at the first unticked step.
