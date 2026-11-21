---
title: PlayStation multi-disc play
status: in-progress
created: 2026-11-20
---

# PlayStation multi-disc play

## Summary
A multi-disc PlayStation game (each disc its own release with a `Disc <n>` flag) gets a **Play all discs** action that launches every disc in one PCSX ReARMed session. The API streams one uncompressed ZIP holding a generated `.m3u` playlist and each disc's files, and EmulatorJS 4.2.3's own **Disks** menu swaps discs in-session, with one memory card for the whole game. Each disc's own Play button stays as it is.

## Out of scope
- Ingesting `.m3u` files (a playlist has nothing to hash; discs stay separate releases).
- Saturn, Sega CD and 3DO: EmulatorJS 4.2.3's `downloadRom()` prefers a `.cue`/`.ccd` (and an `.iso`/`.cso`/`.chd` when the core lists one) over an `.m3u` for every core group but `psx`, so they'd need a vendored-loader edit.
- Starting a set on a disc other than Disc 1 (EmulatorJS boots the playlist's first entry).
- A multi-disc `.pbp` (PCSX ReARMed already swaps its discs itself) and any `.pbp` as a set member.
- Library quick play, global search, `?play=` (B27) and the Saves tab: they keep launching one release.
- Save states in the library for a set launch (M8).
- A higher size cap, and a new e2e seed (a multi-disc seed game would shift every seeded count assertion).

## Behaviour
- **M1 Disc set.** A release's disc number comes from its first flag matching `^Disc (\d+)$` (case ignored, ASCII digits, parsed as an int, so `Disc 01` is 1), the rule `releases.ts`'s `discNumber`, `ReviewResolver` and `LibraryEditor` use. Among a PlayStation game's releases, those with exactly one such flag group by every other identifying field: `BaseTitle`, `Regions` (ordered), `Languages` (ordered), `Revision`, `Version` and the remaining `Flags` (ordered, the disc flag removed), compared ordinally. A group is a **disc set** when it has **2–8** releases (PCSX ReARMed's libretro disk table holds 8) numbered exactly `1..n` with no repeats, and every member's main file (`FileName`, case ignored) ends in `.bin`, `.iso`, `.chd` or `.img` (a cue set's main file is its first `.bin`; never `.pbp` or `.nrg`). Any other group (a gap, a duplicate number, one disc, nine or more, a `.pbp` member) is not a set and gets no Play all discs action. Sets are found per game at request time; nothing is stored.
- **M2 Set name.** The playlist name starts from Disc 1's cue file name when the disc is a cue set (its `files` entry with ordinal 0), else from Disc 1's `FileName`. Its first ` (Disc 1)` (case ignored) is removed and its extension replaced by `.m3u`; without that token, the stem plus `.m3u`. Example: cue `Final Fantasy VII (USA) (Disc 1).cue` (main file `Final Fantasy VII (USA) (Disc 1) (Track 1).bin`) → `Final Fantasy VII (USA).m3u`; standalone `Parasite Eve (USA) (Disc 1).chd` → `Parasite Eve (USA).m3u`.
- **M3 Game detail.** Each release in `GET /api/games/{id}` gains `discSet`: on a set's Disc 1 release, `{ "name": "<M2 name>", "romIds": [<disc 1 id>, <disc 2 id>, …] }` in disc order; `null` on every other release and on every non-PlayStation game.
- **M4 Endpoint.** `GET /api/games/{id}/roms/{romId}/play-disc-set` (any signed-in user; `MaintenanceExempt` like `play-disc`; `Cache-Control: no-store`; nothing audited) streams the set whose Disc 1 release is `romId` as one uncompressed `application/zip` (UTF-8 names, every entry at the root) with `X-Content-Bytes` (every entry's length summed, playlist included) and no `Content-Length`, as a `play-disc` cue set does:
  - first the playlist `<M2 name>`: one line per disc, in disc order, naming the entry the core opens for that disc (its playback cue, or its standalone image's stored name), LF line ends, UTF-8 without BOM, ending in LF;
  - then per disc, in order: a cue set as `play-disc` builds it (`CueSheet.ForPlayback`: the playback cue under its stored base name with a lowercase `.cue`, then its tracks), the tracks renamed `disc<n>-track-NN.bin` (`disc1-track-01.bin`…) so two discs never share a name; a standalone `.bin`/`.iso`/`.chd`/`.img` unchanged under its stored name.
  - Every member of every disc passes `play-disc`'s checks (name equals its external name, portable name, sole claim, non-empty, shape and cue validity) before the response starts; the combined opened length of all discs uses the same inclusive 2 GiB cap (`PlayDisc.MaxBytes`).
  - Errors: an unknown game or release, a non-PlayStation game, or a `romId` that isn't the Disc 1 release of a set (M1) is `404 NotFound`; a missing, unsafe or ambiguously claimed file `404 NotFound`; an unreadable file `500 IoError`; `409 InvalidPlayDisc` with the detail `Disc <n>: <play-disc's detail>` for a disc failing a `play-disc` check (so the title reads `The disc can't be played: Disc 2: 'x.bin' is empty.`), `two entries of the disc set have the same name ignoring case` when any two archive entries (playlist included) clash, and `Disc <n>: a disc file can't be named scph5501.bin` for a standalone member named `scph5501.bin` (any case; it would overwrite the firmware the player writes to `/scph5501.bin`); over 2 GiB `409 PlayDiscTooLarge` with title `The disc set is larger than 2 GiB.`; signed out `401 Unauthorized`; a session that must change its password `403 PasswordChangeRequired`.
- **M5 Play URL.** `playUrl(gameId, rom, core, disc, save = null, setName: string | null = null)` (`emulator.ts`): with a `setName` it uses it as `name` and appends `&set=1`; `rom` stays the real Disc 1 release (its id is the URL's `rom`). `parsePlayParams` (`params.js`) accepts `set` absent or exactly `1` once, only with core `pcsx_rearmed` (anything else returns `null`, *Invalid emulator link.*), and returns it as `set: boolean`.
- **M6 Player.** With `set`, `play.js` does the PlayStation launch unchanged (scph5501.bin check, progress bar, saves) except that it fetches `/api/games/{game}/roms/{rom}/play-disc-set` instead of `play-disc`. `discError(status, code, core, set = false)` gains `set`: with it, `PlayDiscTooLarge` reads *This disc set is larger than 2 GiB, too large to play in the browser. CHD images are much smaller.*; every other message is unchanged. The content `File` is named `contentFileName('pcsx_rearmed', game, name)`. EmulatorJS 4.2.3 unpacks the ZIP and, for the `psx` group, always picks the `.m3u`; its **Disks** button appears because the disk count is above 1.
- **M7 Game page.** On a set's Disc 1 release card (Releases tab), right after its Play button, a button `btn btn-sm btn-outline-success` with `<Icon name="play" />` and the text `All discs`, `aria-label="Play all discs of <M2 name>"` and tooltip `Play all discs in one session`. It shows only when the release's Play button shows (`canPlay`). A new page state `playSet = $state<string | null>(null)` holds the set name: All discs sets `playRom` = Disc 1 and `playSet` = `discSet.name` and opens the modal; the single-release Play button and the B27 `?play=` path set `playSet = null`. The page's `EmulatorModal` gets `discSet={playSet}`.
- **M8 Modal.** `EmulatorModal` gains `discSet?: string | null` (default `null`). With it: the title is `Play <M2 name>`, the iframe title `Emulator: <M2 name>`, the URL `playUrl(gameId, rom, core, true, playSave, discSet)`, and **Save state to library** is hidden. The page never passes an explicit `save` prop with `discSet`, so the only save a set URL can carry is B22's battery `autoSave` (`&save=…&saveKind=battery`). Battery saves use Disc 1's release (`rom`) as today: the B22 lookup finds the user's newest Disc 1 battery save and loads it into the set's memory card, and **Save battery to library** and the B23 save on close store the card against Disc 1 (replacing that save as B23 does). So a set launch and a single-disc Disc 1 launch share a card through the library, while single-disc Disc 2+ launches keep their own. In the browser the set's card is the playlist's (`<M2 stem>.srm`).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` (new) | M1/M2: `DiscSets.Find(IReadOnlyList<RomRow> roms, ILookup<long, RomFileRow> files)` → the sets (`DiscSet(string Name, IReadOnlyList<RomRow> Discs)`, new record) and `DiscSets.DiscNumber(IReadOnlyList<string> flags)`. |
| Core | `src/RnRomManager.Core/Ingest/CueSheet.cs` | `ForPlayback` gains `string trackPrefix = "track-"`; the alias is `{trackPrefix}{n:00}.bin`. Existing callers unchanged. |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | M3: `ToDetail` adds `discSet` per release (PlayStation only). M4: maps `/{id:guid}/roms/{romId:long}/play-disc-set` beside `play-disc`, with `MaintenanceExempt`. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | M4: `HandleSetAsync` (new); the per-disc member checks and opening move into one private method that both handlers call, so `play-disc` behaves exactly as today. `DiscResult` writes the leading entry (cue or playlist) unchanged. |
| Web | `src/RnRomManager.Web/src/lib/api/types.ts` | `RomDetail.discSet: { name: string; romIds: number[] } \| null`. |
| Web | `src/RnRomManager.Web/src/lib/emulator.ts` | M5: `playUrl`'s `setName` argument. |
| Web | `src/RnRomManager.Web/static/emulator/params.js`, `play.js` | M5/M6: `set` parameter, `play-disc-set` URL, `discError`'s `set`. |
| Web | `src/RnRomManager.Web/src/lib/components/EmulatorModal.svelte` | M8: `discSet` prop, titles, hidden state button. |
| Web | `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | M7: the All discs button, `playSet`. |
| Docs | `docs/features.md`, `docs/development.md`, `README.md` | features: the PlayStation play paragraph (replace "discs can't be swapped inside a session" with multi-disc play, the Disks menu, the 8-disc limit and M8's memory card and state rules), the `GET /api/games/{id}` entry (`discSet`), and a `play-disc-set` entry beside `play-disc`; development: the Browser emulators section (`set` parameter, `discError`'s `set`); README: the Play line. |

No migration, config key or stored data change.

## Risks
- **Memory:** a set uses the same 2 GiB cap as one disc, so most bin/cue sets of three or more discs are refused (M4/M6 message points to CHD). No new memory risk.
- **Cooked `.iso` in a playlist:** PCSX ReARMed reads a 2048-byte-sector `.iso` only through EmulatorJS's single-file fallback today; inside an `.m3u` it may not boot. Documented, not blocked (the app can't tell a cooked image from a raw one without reading it).
- **Memory cards:** M8's library round trip ties the set's card to Disc 1's battery save; single-disc Disc 2+ progress isn't merged. Documented.
- **States:** a state from a set could be taken on any disc, so set launches don't store states in the library (M8).
- **Eight-disc limit:** PCSX ReARMed's libretro disk table (`disks[8]` in `frontend/libretro.c` at the pinned `EmulatorJS/pcsx_rearmed` f29871d); `read_m3u` silently stops after 8 lines, so M1 never offers a larger set.

## Steps
- [x] **1. Disc sets and track prefix (Core)**: `DiscSets` (M1, M2) and `CueSheet.ForPlayback`'s `trackPrefix`. Files: `src/RnRomManager.Core/Library/DiscSets.cs` (new), `src/RnRomManager.Core/Ingest/CueSheet.cs`. Tests: `tests/RnRomManager.Core.Tests/Library/DiscSetsTests.cs` (new: a 3-disc set of a multi-track cue Disc 1 plus `.chd` discs, named from the cue; gap, duplicate, single disc, `.pbp`/`.nrg` member, 9 discs → none; 8 discs → a set; two regions → two sets; differing languages, revision, version or other flags split groups; `disc 01` counts as 1; `(DISC 1)` removed ignoring case; a name without the token), `CueSheetTests` (prefix in aliases and cue content; default unchanged). Done when: both test classes pass and existing `CueSheetTests` are unchanged.
- [x] **2. Detail `discSet` (API)**: M3. Files: `LibraryEndpoints.cs`. Tests: `LibraryEndpointsTests` (`discSet` on Disc 1 with name and ordered ids, null on Disc 2+, on a lone disc and on another system). Done when: `LibraryEndpointsTests` passes.
- [x] **3. `play-disc-set` (API)**: M4. Files: `PlayDisc.cs`, `LibraryEndpoints.cs` (route). Tests: `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.PlayDiscSet.cs` (new: a cue-set + `.chd` set streams the playlist first with exact bytes, then each disc with `disc<n>-track-NN.bin` names and byte-exact tracks; `X-Content-Bytes`; each M4 error with its detail; exactly 2 GiB streams and one byte more is 409; 401/403), `LibraryEndpointsTests.PlayDisc` unchanged and passing, and the new route in `EndpointRoleMatrixTests` (User). Done when: those pass.
- [x] **4. Player (`set`)**: M5, M6. Files: `emulator.ts`, `params.js`, `play.js`. Tests: `src/lib/emulator.test.ts` (`setName` → `name` and `&set=1`; without it unchanged), `src/lib/emulatorParams.test.ts` (`set` accepted once with `pcsx_rearmed`; refused with another core, repeated or not `1`; `discError` with `set`), `src/lib/emulatorPlay.test.ts` (fetches `play-disc-set` after the BIOS check, `File` name, 409 messages). Done when: those files, `npm run lint` and `npm run check` pass.
- [ ] **5. Game page and modal**: M7, M8. Files: `types.ts`, `EmulatorModal.svelte`, `routes/games/[id]/+page.svelte`. Tests: `src/routes/games/game-page.test.ts` (button on Disc 1 only, its label and tooltip; hidden without `discSet` or when Play is hidden; opens the modal with the set URL; All discs, close, then a single-disc Play gives a URL without `set`), `src/lib/components/EmulatorModal.test.ts` (titles; iframe URL with `set=1`, and with the user's newest Disc 1 battery save as `save`/`saveKind=battery` when B22 finds one; no **Save state to library**; **Save battery to library** and the B23 close store against Disc 1's file name). Done when: those files, lint and check pass.
- [ ] **6. Docs**: as in the Changes Docs row; regenerate the codemap. Done when: each listed doc section is updated, "discs can't be swapped inside a session" is gone, and `docs/codemap.md` is regenerated and committed.

## Decisions
- 2026-11-20 Where does multi-disc play start? → a separate Play all discs action; per-disc Play stays single-disc (user)
- 2026-11-20 Size cap for a set launch? → the same inclusive 2 GiB (user)
- 2026-11-20 Which release do a set's library saves use? → Disc 1's (user)
- 2026-11-20 Saturn, Sega CD, 3DO? → out of scope; EmulatorJS only prefers an `.m3u` for the `psx` group (user)
- 2026-11-20 `.m3u` ingest? → no; playlists are generated at play time (user)
- 2026-11-20 Which file does EmulatorJS boot from the ZIP? → the `.m3u` (4.2.3 `downloadRom()` always prefers it for `psx`; `pcsx_rearmed` lists no `iso`/`chd`, so its `isoFile` branch is skipped) (code)
- 2026-11-20 e2e? → none; no seeded multi-disc game, and adding one shifts every seeded count (code)
- 2026-11-20 Review round 1 fact fixes: a cue set's main file is its first `.bin`, so M2 names from the cue; disc-number rule is `releases.ts`'s, not `IngestPipeline.IsDiscNumber` (case-sensitive) (reviewer)
- 2026-11-20 Largest set? → 8 discs, PCSX ReARMed's disk table (reviewer)
- 2026-11-20 Is the 8-disc table real in the pinned core? → yes, `disks[8]`; extra `.m3u` lines are ignored, entries resolve next to the playlist, the first line boots (code)
- 2026-11-20 How does the set name reach the play URL? → `playUrl`'s `setName`; `rom` stays Disc 1 for saves (reviewer)
- 2026-11-20 A standalone member named `scph5501.bin`? → 409 `InvalidPlayDisc` (reviewer)
- 2026-11-20 Library save states from a set launch? → not offered; battery saves only (user)
- 2026-11-20 Does B22's Disc 1 battery save reach a set launch? → yes, through the set URL's `save`; no other save is ever passed (reviewer)
- 2026-11-20 Step 2 too big? → split into detail (2) and endpoint (3) (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-20-psx-multi-disc-play.md
```
Starts the spec, or resumes it at the first unticked step.
