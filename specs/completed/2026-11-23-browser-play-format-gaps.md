---
title: Browser play format gaps
status: completed # draft | ready | in-progress | completed
created: 2026-11-23
---

# Browser play format gaps

## Summary
An audit of every system's stored formats against its browser core's compiled-in `valid_extensions` (`BrowserCoreExtensionsTests`) found no unexplained mismatch; what's left are formats the library stores but Play refuses. This spec closes the one that is fixable without rebuilding a core: a Saturn `.nrg` disc plays (play-disc converts it, as MDS/TOC discs already are).

## Out of scope
Audit gaps left as they are (each already refused by `canPlay`/`playRomId` or the system has no `BrowserCore`):
- Amiga `.ipf`: PUAE's WebAssembly build has no capsimg.
- Jaguar `.abs`, `.cof`, `.jag` and `.rom`/`.j64` of a non-cart size: Virtual Jaguar's reset wipes RAM-loaded programs.
- TurboGrafx-CD lone `.bin`: a data track without its cue; Beetle PCE would boot it as a HuCard.
- 3DO disc sets: Opera has no `.m3u` support.
- TurboGrafx-CD disc sets: the pinned Beetle PCE (`EmulatorJS/beetle-pce-libretro` `e1ac90d`) reads every `.m3u` entry but `PCE_LoadCD` (`mednafen/pce/pce.cpp`) only ever inserts the first disc, and `libretro.cpp` registers no disk-control interface, so **All discs** couldn't change discs.
- GameCube, Wii, Xbox 360, Switch, 3DS: no EmulatorJS core exists.
- TAO and multi-session `.nrg` images, and `.nrg` tracks that aren't 2352-byte sectors (N3).
- A `.nrg` inside a cue set (still `InvalidPlayDisc`, as today).

## Behaviour

### Saturn `.nrg` (N)
- **N1** `play-disc` accepts a standalone `.nrg` (any case) on every system it serves except PSP and TurboGrafx-CD (only Saturn stores `.nrg`). It reads the Nero footer and track list from the image and streams, like an MDS disc, an uncompressed ZIP of a generated cue `<nrg stem>.cue` and the image as `track-01.bin` (`disc<n>-track-01.bin` in a set). The entry's length is the last track's end offset, so the footer is never sent (`CopyMeasuredAsync` copies exactly the entry length). `X-Content-Bytes` follows from the entries as today.
- **N2** Parsing follows libmirage's `image-nrg` (`nrg.h`), big-endian:
  - Footer: if the last 12 bytes start `NER5`, a v2 footer (12 bytes) with a u64 chunk start; else if the last 8 bytes start `NERO`, a v1 footer (8 bytes) with a u32 chunk start; else none. `NrgSheet.Footer` returns the chunk start, the region end (file length − footer length) and whether it's v2, or null when there's no footer, the file is shorter than the footer, or the chunk start is ≤ 0 or ≥ the region end.
  - The chunk region is `[chunk start, region end)`. Chunks: 4-byte id, u32 size, `size` data bytes. Walk to the region end, stopping early at `END!` (as libmirage, which walks the whole region and doesn't require `END!`); a chunk running past the region is truncated. The footer version picks the chunk ids, as libmirage's `old_format` flag does: a v2 file reads `DAOX` and `ETN2`, a v1 file `DAOI` and `ETNF`. Every other chunk (`CUEX`, `CUES`, `SINF`, `MTYP`, `CDTX`, the other version's ids, …) is skipped.
  - Each `DAOX`/`DAOI` chunk: a 22-byte header (4 bytes ignored, 13-byte MCN, 1 pad, session type, 1 byte ignored, first track u8, last track u8), then `last − first + 1` blocks of 42 (`DAOX`, u64 offsets) or 30 (`DAOI`, u32 offsets, read unsigned) bytes: 12-byte ISRC, u16 sector size, u8 mode, 3 pad, index 0 offset, index 1 offset, end offset. The chunk size must be exactly 22 + blocks × block size. Track numbers run `first..last`.
  - Checked against libmirage (`cdemu/cdemu` `libmirage/images/image-nrg/image-nrg.h` and `parser.c`) in the spec review; `NrgSheet`'s doc comment cites those files, as `MdsSheet` cites `image-mds.h`. Mode codes 0x0F, 0x10 and 0x11 are 2352 + 96 subchannel bytes (sector size 2448), so N3.8 refuses them.
- **N3** Errors are `409 InvalidPlayDisc` (as every play-disc shape error; title `The disc can't be played: <detail>.`), with these exact details (`<n>` a track number), each check run over every track before the next starts, in this order:
  1. `the .nrg has no Nero footer` (`Footer` returned null) — emitted by `PlayDisc.OpenDiscAsync`
  2. `the .nrg's track list is larger than 65536 bytes` (region longer than `IngestSetPlanner.MaxCueBytes`) — emitted by `OpenDiscAsync` before allocating; `NrgSheet.Parse` checks it again, as `SheetDiscAsync` and `MdsSheet` both bound their input
  3. `the .nrg is truncated` (a chunk past the region, a `DAO*` size that doesn't match its block count, or `last < first`)
  4. `TAO .nrg images aren't supported` (no DAO chunk for the version, but its ETN chunk)
  5. `the .nrg has no track list` (none of those chunks)
  6. `multi-session .nrg images aren't supported` (more than one DAO chunk: libmirage reads one per session)
  7. `the .nrg's first track isn't track 1` (`first ≠ 1`: the cue's first track must be `TRACK 01`)
  8. `track <n> has <size>-byte sectors; only 2352 can be played`
  9. `track <n> has an unknown mode 0x<MM>` (mode not 0x05, 0x06 or 0x07)
  10. `track <n> doesn't start on a 2352-byte sector` (index 0, index 1 or end offset not a multiple of 2352, or index 0 > index 1, or index 1 ≥ end)
  11. `track <n> doesn't start where track <n−1> ends` (index 0 ≠ the previous end: no overlap and no gap, so no bytes silently join a track)
  12. `track <n> ends past the .nrg's footer` (end > chunk start)
- **N4** The cue: `FILE "<prefix>01.bin" BINARY`; per track `TRACK nn MODE1/2352` (0x05), `MODE2/2352` (0x06) or `AUDIO` (0x07); `INDEX 00` at index 0 ÷ 2352 only when index 0 < index 1; `INDEX 01` at index 1 ÷ 2352. Times via `CloneCdSheet.Time`. Track 1's index 0 may be > 0: the cue writes it as given and never addresses the bytes before it (cue readers start a track at its first INDEX); Step 1 tests this case.
- **N5** The `.nrg` refusal goes: `canPlay` (`emulator.ts`) and `LibraryEndpoints`' quick-play `playable` no longer reject `.nrg`. A malformed `.nrg` shows the play page's existing `InvalidPlayDisc` message.
- **N6** `DiscSets` takes `.nrg` as a disc's main file on `saturn` only, so a Saturn set may mix `.nrg` discs; its playlist names the generated cue (`DiscSets.Name` falls back to the file name, unchanged).

## Changes
| Area | File(s) | Change |
|---|---|---|
| Tests | `tests/RnRomManager.TestData/SyntheticNrgBuilder.cs` (new) | Builds v1/v2 DAO `.nrg` images (tracks, modes, sector sizes, offsets, footer) for Core and Api tests, as `SyntheticMdsBuilder` does for MDS |
| Core | `src/RnRomManager.Core/Ingest/NrgSheet.cs` (new) | `NrgTrack(int Number, int Mode, int SectorSize, ulong Index0, ulong Index1, ulong End)`; `NrgSheet(IReadOnlyList<NrgTrack> Tracks, string? Error)` with `static (long ChunkStart, long RegionEnd, bool V2)? Footer(ReadOnlySpan<byte> tail, long length)` (`tail` = the file's last 12 bytes or fewer), `static NrgSheet Parse(ReadOnlySpan<byte> region, long chunkStart, bool v2)` (N3.2–N3.12) and `static (PlaybackCue Cue, long DataLength) ForPlayback(ReadOnlySpan<byte> region, long chunkStart, bool v2, string nrgFileName, string trackPrefix)`, shaped like `MdsSheet` |
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` | N6: `.nrg` main file on `saturn`, and the `MainExtensions` comment |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | N1/N3: `ShapeProblem` accepts a lone `.nrg` (message becomes `a single-file disc must be a .bin, .iso, .chd, .img, .pbp, .cbn or .nrg`); `OpenDiscAsync` reads the tail, calls `Footer` (N3.1), checks the region size (N3.2), reads the region, seeks back to 0 and returns a `Disc` with the cue and one `Entry(alias, stream, DataLength)`; the class and `ShapeProblem` doc comments name the `.nrg` shape |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | N5: drop the `.nrg` condition and its comment in the quick-play `playable` |
| Systems | `src/RnRomManager.Systems.Sega/SaturnHandler.cs` | `BrowserCore` doc comment no longer says an `.nrg` release never plays |
| Web | `src/RnRomManager.Web/src/lib/emulator.ts` | N5: drop the `.nrg` clause from `canPlay` and its doc comment |
| Tests | `tests/RnRomManager.Api.Tests/BrowserCoreExtensionsTests.cs` | `("saturn", ".nrg")` reason becomes `play-disc sends a cue built from the Nero footer and the image as track-01.bin (N1), never the .nrg as the content` |
| Docs | `README.md` line 15; `docs/features.md` (Saturn Play bullet; the `GET /api/games` line's `playRomId`, which says the release "isn't an `.nrg` image"; `play-disc` and `play-disc-set` API lines); `docs/development.md` (TestData row at line 42; § Browser emulators: Saturn and Multi-disc sets bullets); `docs/adding-a-system.md` line 61 (`BrowserCore` row: "never an `.nrg` release") | Drop "never `.nrg`"/"an .nrg never plays", describe N1/N6, add the builder |

No migration, config key or API contract change: `play-disc` keeps its responses, a valid `.nrg` now gets 200 instead of 409, and a Saturn set with `.nrg` discs now gets a `discSet`. The README systems table is unchanged (formats and cores don't change).

## Risks
- **Memory:** the chunk region is bounded by `MaxCueBytes` (64 KiB) before it is read; the image itself streams.
- **Untrusted offsets:** every offset is checked against the region or the footer (N3), so a crafted `.nrg` can't make play-disc read past the file or send the footer.
- **Wrong layout assumptions:** N2 now follows libmirage's parser, but synthetic tests only prove self-consistency. Manual check in Finish: boot one real Saturn `.nrg` (DAO) in the browser, alone and in a set; record the result in the PR.
- **Saturn sets with `.nrg`:** a 5-disc set counts all images against the 2 GiB `MaxBytes` budget as today.

## Steps
- [x] **1. NRG parser**: `SyntheticNrgBuilder`, `NrgSheet` and `NrgTrack` (N2–N4), following N2. Files: `tests/RnRomManager.TestData/SyntheticNrgBuilder.cs` (new), `src/RnRomManager.Core/Ingest/NrgSheet.cs` (new). Tests: `tests/RnRomManager.Core.Tests/Ingest/NrgSheetTests.cs` (new, images from `SyntheticNrgBuilder`). Done when: tests cover `Footer` for v2 (`V2` true) and v1 (`V2` false, region end = length − 8), no footer, a file shorter than 8 bytes and a chunk start ≤ 0 or ≥ the region end; each N3 error 2–12 with its exact text, including skipped unknown and other-version chunks; a region without `END!` parsing to its end; a mode 0x0F block (sector size 2448) failing N3.8; a three-track DAOX image (data with a 150-sector index 0, two audio) gives the exact N4 cue with `INDEX 00`/`INDEX 01` and `DataLength` = last end; a DAOI image gives the same cue; `dotnet test tests/RnRomManager.Core.Tests --filter "FullyQualifiedName~NrgSheetTests"` passes.
- [x] **2. Play Saturn .nrg**: N1, N5, N6 in `PlayDisc`, `LibraryEndpoints`, `DiscSets`, `emulator.ts`; `SaturnHandler` comment; `BrowserCoreExtensionsTests` reason; docs for N. Files: `src/RnRomManager.Api/Endpoints/PlayDisc.cs`, `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs`, `src/RnRomManager.Core/Library/DiscSets.cs`, `src/RnRomManager.Systems.Sega/SaturnHandler.cs`, `src/RnRomManager.Web/src/lib/emulator.ts`, `tests/RnRomManager.Api.Tests/BrowserCoreExtensionsTests.cs`, `README.md`, `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md`. Tests:
  - `LibraryEndpointsTests.PlayDisc`: new `PlayDisc_ASaturnNrg_StreamsACueAndTrack01WithoutTheFooter` (a `SyntheticNrgBuilder` image; the ZIP holds `<stem>.cue` with the N4 text and `track-01.bin` of exactly the data length); `PlayDisc_ASaturnNrg_AloneOrInASet_Is409InvalidPlayDisc` renamed `PlayDisc_ASaturnNrg_WithoutAFooterOrInACueSet_Is409InvalidPlayDisc`, and for the lone case it also asserts the title `The disc can't be played: the .nrg has no Nero footer.`; `PlayDisc_ALoneNrg_NamesTheSingleFileShapes` becomes `PlayDisc_ALoneMdf_NamesTheSingleFileShapes` (a lone `.mdf`, new message text).
  - `LibraryEndpointsTests.PlayDiscSet`: new `PlayDiscSet_ASaturnSetWithAnNrgDisc_NamesItsGeneratedCue` (`disc2-track-01.bin`, the playlist line names its cue).
  - `LibraryEndpointsTests`: `Games_QuickPlay_AnNrgPrimary_IsNotPlayable` becomes `Games_QuickPlay_AnNrgPrimary_IsPlayable`, every case `true`.
  - `DiscSetsTests`: `AnImgOrNrgDisc_SpoilsASaturnOrSegaCdSet` drops `("saturn", ".nrg")`; new `ANrgDisc_JoinsASaturnSet`.
  - `src/RnRomManager.Web/src/lib/emulator.test.ts`: the two `.nrg` tests (lines 72–89) expect `true` and are retitled `is true for an external Saturn bin and its .nrg (N5)` and `is true for an external %s image (N5)`.
  - `BrowserCoreExtensionsTests` passes.
  Done when: those classes and `emulator.test.ts` pass; `npm run lint` and `npm run check` pass; README, features, development and adding-a-system docs no longer say `.nrg` never plays (`git grep -n -i nrg -- ':!specs' ':!tests'` shows no such wording), and the TestData row names `SyntheticNrgBuilder`.

Finish runs the full suite and e2e unchanged (no Playwright change: e2e doesn't boot the Saturn core with real discs), plus the manual check in § Risks.

## Decisions
- 2026-11-23 Which audit gaps to fix? → Saturn `.nrg` playback and TurboGrafx-CD disc sets; the rest stay out of scope (user; TurboGrafx-CD superseded in pass 5)
- 2026-11-23 Which `.nrg` shapes? → DAO, single session, first track 1, contiguous 2352-byte tracks; the sector-size and mode limits match `MdsSheet`'s (code)
- 2026-11-23 How to keep the footer out of the stream? → entry length = last track end; `CopyMeasuredAsync` copies exactly that (code)
- 2026-11-23 Review round 1: README line 15 needs updating; NRG fixtures go in a shared `SyntheticNrgBuilder`; `Footer` returns the region end (v1 footers are 8 bytes); the tgcd firmware-clash test can't be built (dropped); a tgcd set with a lone `.bin` disc isn't a set (T6); doc comments and test names pinned (reviewer)
- 2026-11-23 Review round 2: T6 covers any lone non-`.chd` disc, not only `.bin` (reviewer)
- 2026-11-23 Spec review pass 1: `vendorEmulatorjs.test.ts` pins the `.m3u` replace string, so Step 3 updates and extends it; N4's track-1 index 0 wording made exact (reviewer)
- 2026-11-23 Spec review pass 2: Step 3's detail and params tests named after their siblings; the TurboGrafx-CD detail test uses `.chd` discs because of T6 (reviewer)
- 2026-11-23 Spec review pass 3: N3.7's reason no longer claims `MdsSheet` requires track 1 (it doesn't); decisions put in order (reviewer)
- 2026-11-23 Spec review pass 4: N2 checked against libmirage's NRG parser (footer version picks DAOX/ETN2 or DAOI/ETNF; `END!` not required; the header's first 4 bytes are ignored); T1 checked against Beetle PCE `e1ac90d` (no playlist limit; 4 is the app's cap) (reviewer)
- 2026-11-23 Spec review pass 5: Beetle PCE `e1ac90d` can't swap discs (`PCE_LoadCD` inserts only the first; no disk-control interface) → TurboGrafx-CD sets dropped, T1–T6 and Step 3 removed (user); `Footer` returns the version so `Parse`/`ForPlayback` pick the chunk ids, and DAOI offsets read unsigned (reviewer)
- 2026-11-23 Spec review pass 6: Step 1 tests `Footer`'s `V2` flag (reviewer)
- 2026-11-23 Spec review pass 7: `docs/features.md`'s `GET /api/games` line also excludes `.nrg` from `playRomId`, so Step 2 updates it (reviewer)
- 2026-11-23 Spec review pass 8: `docs/adding-a-system.md`'s `BrowserCore` row also says "never an `.nrg` release"; Step 2 updates it and its Done when greps for leftovers (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-23-browser-play-format-gaps.md
```
Starts the spec, or resumes it at the first unticked step.
