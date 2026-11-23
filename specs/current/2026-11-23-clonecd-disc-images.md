---
title: CloneCD disc images
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-23
---

# CloneCD disc images

## Summary
The five cue-taking disc systems (PlayStation, Saturn, Sega CD, 3DO, TurboGrafx-CD) accept **CloneCD** rips: a `.ccd` (text TOC), its `.img` (raw 2352-byte sectors) and an optional `.sub` (subchannel). Ingest stores the three files as they are, as one release, the way it stores a bin/cue set. `play-disc` builds a cue from the `.ccd` when Play is pressed, as it already builds one for a lone `.bin` in a disc set. So every disc core plays them, including Genesis Plus GX and Opera, which can't open `.ccd` themselves. Today these files are refused (`UnsupportedType`) on four of the systems, and PlayStation takes only a lone `.img`.

## Out of scope
- Alcohol 120% (`.mds`/`.mdf`), cdrdao `.toc` and Nero `.nrg` (Saturn's `.nrg` stays library-only: no change).
- Multi-session CloneCD images and scrambled data tracks (`Sessions` > 1, `DataTracksScrambled=1`): they ingest but don't play (B9).
- Converting CloneCD to bin/cue at ingest, or matching the Redump DAT track by track: a CloneCD `.img` is the whole disc, so it matches the DAT only when the disc has one track (B6).
- PSP: it doesn't take `.cue`, so the set planner never runs for it.
- SPA behaviour: `canPlay`/`playRomId` already allow an external release with set files (only `types.ts` comments change).

## Behaviour

### Accepted files
- **B1** `RomExtensions` gains `.ccd` and `.sub` on all five systems, and `.img` on the four that lack it (PlayStation already lists it). Appended in this order after the existing entries, so `RomExtensions[0]` doesn't change:
  - `psx`: `.bin`, `.cue`, `.iso`, `.chd`, `.img`, `.pbp`, `.ccd`, `.sub`
  - `saturn`: `.bin`, `.cue`, `.nrg`, `.chd`, `.iso`, `.ccd`, `.img`, `.sub`
  - `segacd` and `3do`: `.bin`, `.cue`, `.iso`, `.chd`, `.ccd`, `.img`, `.sub`
  - `tgcd`: `.bin`, `.cue`, `.chd`, `.ccd`, `.img`, `.sub`

  Size limits don't change (16 bytes up to each system's maximum, per file).
- **B2** `BrowserCoreExtensionsTests.Exceptions` gains one entry per system and extension its core doesn't list, all with the reason `CloneCD set member: play-disc sends a generated cue (B8), never this file as the content`. The entries are `psx` `.ccd`/`.sub`, `saturn` `.img`/`.sub`, `segacd` `.ccd`/`.img`/`.sub`, `3do` `.ccd`/`.img`/`.sub` and `tgcd` `.img`/`.sub`.

### The `.ccd` format
- **B3** `CloneCdSheet` (new, `src/RnRomManager.Core/Ingest/CloneCdSheet.cs`, next to `CueSheet`) parses a `.ccd`'s bytes. `Parse(ReadOnlySpan<byte>)` returns a sheet with `IsValid`, `Error` and `Tracks` (number, mode, `Index0` or null, `Index1`), as `CueSheet.Parse` returns its result.
  - **Size and text.** More than `IngestSetPlanner.MaxCueBytes` bytes is the error `the .ccd is larger than 65536 bytes`. The bytes are decoded the way `CueSheet` decodes a cue: a leading UTF-8 BOM is dropped, and LF, CRLF or a lone CR ends a line.
  - **What's read.** `[Section]` headers and `key=value` lines are matched ignoring case and the spaces around names, keys and values. Blank lines are ignored, and so is every section other than `[Disc]` and `[TRACK n]` (`[CloneCD]`, `[Session n]`, `[Entry n]`, `[CDText]`). Only five keys are read: `Sessions` and `DataTracksScrambled` in `[Disc]`, and `MODE`, `INDEX 0` and `INDEX 1` in `[TRACK n]`. Every other key in those sections is ignored (`TocEntries`, `CDTextLength`, `CATALOG`, `FLAGS`, `ISRC`…).
  - **Error texts**, in the order they're checked (`n` and `m` are decimal track numbers). `<Key>` and `<KEY>` are always the canonical spelling (`Sessions`, `DataTracksScrambled`, `MODE`, `INDEX 0`, `INDEX 1`), whatever case the file uses:
    - a read key given twice: `[Disc] <Key> appears twice` or `track <n>: <KEY> appears twice`;
    - a read key's value isn't a decimal number: `[Disc] <Key> isn't a number` or `track <n>: <KEY> isn't a number`;
    - `Sessions` other than `1`: `multi-session CloneCD images aren't supported`;
    - `DataTracksScrambled` other than `0`: `scrambled CloneCD images aren't supported`;
    - no `[TRACK n]` section: `the .ccd has no [TRACK 1]`;
    - more than 99 tracks: `the .ccd has more than 99 tracks`;
    - track numbers that aren't 1, 2, … in file order: `track <m> follows track <n>` (`track <m> follows the start` when the first isn't 1);
    - no `MODE`: `track <n> has no MODE`;
    - `MODE` not 0, 1 or 2: `track <n> has an unknown MODE <value>`;
    - no `INDEX 1`: `track <n> has no INDEX 1`;
    - an index past sector 449,999 (99:59:74): `track <n>: <KEY> is past 99:59:74`;
    - `INDEX 0` greater than `INDEX 1`: `track <n>: INDEX 0 is after INDEX 1`;
    - an `INDEX 1` not greater than the previous track's: `track <n> doesn't start after track <n-1>`.
- **B4** `CloneCdSheet.ForPlayback(ReadOnlySpan<byte> ccd, string ccdFileName, string imageName, long imageLength, string? subName, string trackPrefix)` returns a `PlaybackCue` (the existing record), or `PlaybackCue.Fail(error)` on a B3 error. It also fails with these texts:
  - `the .img isn't a whole number of 2352-byte sectors` when `imageLength % 2352 != 0`;
  - `track <n> starts past the end of the .img` when the last track's `INDEX 1` isn't below `imageLength / 2352`.

  On success:
  - `CueName` is `<ccd base name>.cue`.
  - `Tracks` holds `(imageName, "<trackPrefix>01.img")`, then `(subName, "<ccd base name>.sub")` when `subName` isn't null. PCSX ReARMed (`f29871d`, `cdriso.c` `opensubfile`) opens `<content path minus extension>.sub`, so the `.sub` shares the cue's base name.
  - `Content` is UTF-8, no BOM, LF: `FILE "<trackPrefix>01.img" BINARY`, then for each track `  TRACK NN <type>` (`AUDIO`, `MODE1/2352` or `MODE2/2352` for MODE 0/1/2), `    INDEX 00 mm:ss:ff` when it has `INDEX 0`, and `    INDEX 01 mm:ss:ff`. The time is the LBA as minutes, seconds and frames, 75 frames a second, two digits each. Every line fits `PlaybackCue.MaxLineBytes`.

### Ingest
- **B5** `IngestSetPlanner.Plan(IReadOnlyList<PlannerFile> files, IReadOnlySet<string>? selected = null, bool loneImage = false)`. `IngestJob` (Step 6) and `ReviewSets.FindAsync` pass `loneImage: handler.Id == "psx"` (in `ReviewSets` only for consistency: it returns sets alone), the way `DiscSets.MainExtensions` names PlayStation. The `IngestSetPlannerTests` helpers keep their calls; new cases pass `loneImage` explicitly. After the cue sets and broken cues, it forms CloneCD units for each `.ccd`, in ordinal name order. Names match by stem, ignoring case: `<stem>.ccd`, `<stem>.img`, `<stem>.sub`.
  - A member whose name fails `CueSheet.NameProblem`: every unclaimed member is `Skipped` with `UnsupportedSetMember`.
  - A `.ccd` over `MaxCueBytes`, unreadable, or not `CloneCdSheet.Parse(...).IsValid`, plus its unclaimed `.img`/`.sub`: `Skipped` with `IncompleteSet`.
  - No `.img`: the `.ccd` and its `.sub` are `Skipped` with `IncompleteSet`.
  - Two files folding to the same member name, or an `.img`/`.sub` already claimed by a cue unit: every unclaimed member is `Skipped` with `SetConflict`.
  - Otherwise a `Set` with members `[ccd, img]` or `[ccd, img, sub]`. The `.img` is `members[1]`, the main file `StoreSetReleaseAsync` expects.
  - Then, before the lone files: every remaining `.sub` is `Skipped` with `IncompleteSet`, and so is every remaining `.img` unless `loneImage` is true. With `loneImage`, a PlayStation `.img` with no `.ccd` stays `Lone`, as today.

  `PlannerFile.CueBytes` and `IngestCueReader.Read` also carry a `.ccd`'s bytes, with the same size bound and checked open. Their doc comments, and `IngestReason.UnsupportedSetMember`/`IncompleteSet`/`SetConflict`'s, say "a `.cue` or `.ccd`" / "a cue or CloneCD set".
- **B6** Processing is `IngestPipeline.ProcessSetAsync`, unchanged. Every member is hashed. A CloneCD `.img` matches the Redump DAT only when it's byte-identical to a listed track, in practice a one-track disc. Then the set is ingested automatically: the `.img` is the main file, and the `.ccd` (ordinal 0) and `.sub` go in `files`. Otherwise every member goes to Review as `NoDatMatch`, as an unmatched bin/cue set does.

### Review
- **B7** `ReviewResolver.IsDismissible(item, systems)` becomes `IsDismissible(item, systems, bool inSet)`. On a `FileSets` system:
  - a `.cue`, `.bin`, `.ccd` or `.sub` item is never dismissible;
  - an `.img` item is dismissible only when `item.SystemId == "psx"` and `inSet` is false (a lone PlayStation `.img`), the same test B5's `loneImage` uses; on every other `FileSets` system it never is.

  Every other item is dismissible, as today. "Set member candidates" are the `FileSets` items with one of these five extensions. Every caller finds the set for such an item first, under the lock where it already holds one, and passes `inSet: set is not null`:
  - `ReviewEndpoints` (GET item: `resolvable`, `dismissible`);
  - `ReviewResolver.ResolveInJobAsync` (line 38);
  - `ReviewResolver.DismissAsync` (line 49), which now looks up the set and passes it to `ActCoreAsync`;
  - `IsResolvableAsync`;
  - `ActCoreAsync`'s guard (line 349).

  A dismiss of a set member is refused with `SetFileNotResolvable`, as for a `.cue` today. A CloneCD set resolves as one release through the existing `ResolveSetAsync` → `StoreSetReleaseAsync` path. The A2 tag source (`ReviewResolver` line 536, the set's `.cue` name) also takes the `.ccd` name.

### Play
- **B8** `PlayDisc.ShapeProblem` also accepts a **CloneCD disc**: the main member is an `.img`, and the other members are exactly one `.ccd` and at most one `.sub`, all with the same stem ignoring case, for any of the five systems. PSP's and TurboGrafx-CD's single-file rules are unchanged; a TurboGrafx-CD CloneCD disc boots as a CD because it gets a cue. The error for any other shape keeps its text. `OpenDiscAsync` opens and counts every member as now, `.ccd` included. For a CloneCD disc it then reads the `.ccd` in full (one over `MaxCueBytes` is `the .ccd is larger than 65536 bytes`, as B3 says) and calls `CloneCdSheet.ForPlayback` with the `.img`'s opened length. It returns the generated cue plus the aliased `.img` and `.sub`. The stored `.ccd` is never in the archive, because EmulatorJS 4.2.3's `downloadRom()` would boot the first `.cue`/`.ccd` it finds. A `ForPlayback` error is `409 InvalidPlayDisc` with that error as the detail (prefixed for a set, as today).
- **B9** Play-time failures are the existing ones, with the existing play-page text: `InvalidPlayDisc` for a bad `.ccd`, multi-session or scrambled, and `PlayDiscTooLarge` when `.ccd` + `.img` + `.sub` exceed 2 GiB.
- **B10** Multi-disc sets: `DiscSets.MainExtensions` returns `.bin`, `.iso`, `.chd`, `.img` for every system, so CloneCD discs group into **All discs** sets. Its comment, which says the Saturn and Sega CD handlers accept no `.img`, is updated. `DiscSets.Name` takes Disc 1's ordinal-0 `.cue` or `.ccd` for the playlist name. `play-disc-set` streams each CloneCD disc as B8 does, with the `disc<n>-track-` prefix, and the playlist names its generated cue. The lone-`.bin` cue step and the firmware-name check only apply to a disc without a cue, so they're unchanged.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Ingest/CloneCdSheet.cs` (new) | B3, B4. |
| Core | `src/RnRomManager.Core/Ingest/IngestSetPlanner.cs` (`IngestSetPlanner`, `PlannerFile`, `IngestCueReader`), `IngestJob.cs` (Step 6), `ReviewSets.cs` (call and summary), `IngestOutcome.cs` (reason comments) | B5. |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolver.cs` (`IsDismissible`, `ResolveInJobAsync`, `DismissAsync`, `IsResolvableAsync`, `ActCoreAsync`, line 536, the `SetFileNotResolvable` comment) | B7. |
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` | B10. |
| Core (comments only) | `ISystemHandler.FileSets` (lines 41–42), `InfoJsonRom.Files` (line 38), `InfoJsonRomFile` (line 4), `LibraryRows` ("0 is the cue", line 93), `IngestPipeline.ProcessSetAsync`/`StoreSetReleaseAsync` ("bin/cue set", "the cue's first FILE") | Say "a cue or CloneCD set" / "the cue or `.ccd`" (B5, B6). |
| Systems | `PlayStationHandler`, `SaturnHandler`, `SegaCdHandler`, `ThreeDoHandler`, `TurboGrafxCdHandler` | B1, and each class comment says it takes CloneCD sets. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` | B8, B10 (`ShapeProblem`, `OpenDiscAsync`, the XML docs). |
| Api | `src/RnRomManager.Api/Endpoints/ReviewEndpoints.cs` | B7. |
| Web (comments only) | `src/RnRomManager.Web/src/lib/api/types.ts` (lines 386 and 392: `.cue`/`.bin` → set members) | B7. |
| TestData | `tests/RnRomManager.TestData/ExternalSetTestHandler.cs` | `RomExtensions` gains `.ccd`, `.img`, `.sub`, so its set fixtures can store a CloneCD release. |
| TestData | `tests/RnRomManager.TestData/PsxSetTestHandler.cs` (new) | A `FileSets` test handler with id `psx` and `ExternalSetTestHandler`'s other members (`.img`/`.ccd`/`.sub` included), for the PlayStation cases of B5 and B7. |
| Tests | `tests/RnRomManager.Core.Tests/Ingest/CloneCdSheetTests.cs` (new), `IngestSetPlannerTests`, `ReviewSetsTests`, `ReviewResolverTests`, `ReviewResolverExternalTests`, `tests/RnRomManager.Core.Tests/Library/DiscSetsTests` (`AnImgOrNrgDisc_SpoilsASaturnOrSegaCdSet` keeps only its `.nrg` cases), each system's handler tests (extension lists), `BrowserCoreExtensionsTests`, `IngestPipelineTests`, `ReviewEndpointsTests`, `LibraryEndpointsTests.PlayDisc.cs`, `LibraryEndpointsTests.PlayDiscSet.cs` | Per step. The existing assertion at `LibraryEndpointsTests.PlayDisc.cs:677` ("a single-file disc must be …") keeps its text. |
| Docs | `README.md` (table rows for the five systems, Step 3), `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md`, `docs/zip-format.md` | Step 5. |

No migration (`files` rows already hold any set member), no config key, no API shape change: `setMembers` already lists any set's members.

## Risks
- **`.sub` use is per core:** only PCSX ReARMed reads it (LibCrypt subchannel). The other cores ignore the extra file. LibCrypt games without a `.sub`, as with bin/cue today, still fail their protection check.
- **Unverified on real dumps:** no CloneCD rip is in the repo (synthetic files only), and the endpoint tests check the archive, not that a core boots it. Each of PCSX ReARMed, Yabause, Genesis Plus GX, Opera and Beetle PCE must accept a generated cue whose `FILE` is `track-01.img` with mixed data and AUDIO tracks. Step 4's "Done when" asks the implementer to play one CloneCD disc per core they own a rip for, and to record each result in § Decisions; the PR names the cores that weren't checked.
- **Review dismiss rule changes (B7):** a PlayStation `.img` that belongs to a CloneCD set stops being dismissible on its own, so a set can't be half dismissed. A lone PlayStation `.img` behaves as before.

## Steps
- [x] **1. CloneCD sheet**: B3, B4. Files: `src/RnRomManager.Core/Ingest/CloneCdSheet.cs` (new), `tests/RnRomManager.Core.Tests/Ingest/CloneCdSheetTests.cs` (new). Tests: `CloneCdSheetTests`. Done when the tests cover:
  - a one-track MODE2 sheet and a data + two-audio-track sheet with `INDEX 0`, both giving the exact cue text;
  - LF, CRLF, lone CR, a leading BOM, mixed-case keys, ignored sections and ignored keys (`TocEntries`, `FLAGS`);
  - every B3 error text;
  - B4's two image-length errors, the `.sub` alias with and without a sub, and the trackPrefix (`disc2-track-01.img`).
- [ ] **2. Planning and Review**: B5 and B7, before any handler accepts the new extensions, so no `.ccd`/`.sub` item exists without the new rules. Files:
  - `IngestSetPlanner.cs`, `IngestJob.cs`, `ReviewSets.cs`, `IngestOutcome.cs`;
  - `ReviewResolver.cs`, `ReviewEndpoints.cs`;
  - `ExternalSetTestHandler.cs` and `PsxSetTestHandler.cs` (new) in TestData;
  - the comment-only files in § Changes.

  Tests:
  - `IngestSetPlannerTests`: a full set, a set without `.sub`, a missing `.img`, an invalid `.ccd`, a non-portable member name, a case-folded duplicate, an `.img` claimed by a cue, a lone `.sub`, and a lone `.img` with `loneImage` true and false;
  - `ReviewSetsTests`: finds a CloneCD set from each member (on `ExternalSetTestHandler`); the lone-`.img` difference is `IngestSetPlannerTests`' (`FindAsync` returns only sets);
  - `ReviewResolverTests`: the B7 `IsDismissible` matrix (`.ccd`/`.sub` always false; an `ExternalSetTestHandler` `.img` false in and out of a set; a `psx` `.img` false in a set and true alone);
  - `ReviewResolverExternalTests`: `DismissAsync` refuses a set's `psx` `.img` with `SetFileNotResolvable`; a CloneCD set resolves as one release with main `.img` and `files` `[.ccd, .sub]`.

  Done when they pass. These are unit tests over synthetic folders, as the existing ones are.
- [ ] **3. Accept the files**: B1, B2, B6, and the README table rows (`SupportedSystemsTableTests`). Files: the five handlers and their tests, `BrowserCoreExtensionsTests.cs`, `README.md`, `IngestPipelineTests.cs`, `ReviewEndpointsTests.cs`. Tests:
  - handler tests, `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`;
  - `IngestPipelineTests`: a synthetic Saturn CloneCD set goes to Review as three `NoDatMatch` items; a one-track PlayStation `.img` listed in the fixture DAT ingests with `files` `[.ccd, .sub]`; a lone Sega CD `.img` is `Skipped`/`IncompleteSet`;
  - `ReviewEndpointsTests`: GET on a set's `.ccd` shows `resolvable` true, `dismissible` false and three `setMembers`; dismissing its `.img` is refused with `SetFileNotResolvable`.

  Done when they pass, with the integration classes run on Docker.
- [ ] **4. Play**: B8–B10. Files: `PlayDisc.cs`, `DiscSets.cs`. Tests:
  - `LibraryEndpointsTests.PlayDisc.cs`:
    - a CloneCD Saturn disc streams a ZIP with exactly the generated cue, `track-01.img` and `<stem>.sub`, and no `.ccd`;
    - a disc without `.sub`;
    - a bad `.ccd` gives `409 InvalidPlayDisc` with B3's text;
    - a multi-session `.ccd`;
    - an `.img` that isn't a whole number of sectors;
    - a TurboGrafx-CD CloneCD disc plays.
  - `LibraryEndpointsTests.PlayDiscSet.cs`: a two-disc PlayStation CloneCD set (playlist lines, `disc1-track-01.img`, `disc2-track-01.img`).
  - `DiscSetsTests`: `.img` mains on Saturn and Sega CD, and the `.ccd` set name; `AnImgOrNrgDisc_SpoilsASaturnOrSegaCdSet` keeps only `.nrg`.

  Done when they pass; the real-dump check from § Risks is done for the cores available, or noted for the PR.
- [ ] **5. Docs**: the doc rule. Tests: `SupportedSystemsTableTests`. Done when it passes and `docs/codemap.md` is regenerated. No Playwright change. Files:
  - `docs/features.md`:
    - the ingest paragraph's disc-system sentences (CloneCD accepted; `.img`/`.sub` wait for their `.ccd` except a PlayStation `.img`; a one-track `.img` can DAT-match);
    - the TurboGrafx-CD sentence "CloneCD `.ccd`/`.img` and `.iso` aren't supported" becomes "`.iso` isn't supported";
    - the Disc sets section's **Sets**, **Waiting sets** and **No match** bullets (CloneCD grouping by stem);
    - the Review queue's "Unmatched disc sets" and "Disc track files without a set" bullets;
    - the Review API's `dismissible` description (B7);
    - the Play section's CloneCD note.
  - `docs/development.md`: the play-disc and Multi-disc paragraphs.
  - `docs/adding-a-system.md`: the `FileSets` row and "Limits of the contract" (`.ccd` is now grouped; `.gdi`, `.mds` and `.toc` still aren't).
  - `docs/zip-format.md`: a set's `files` may hold a `.ccd` and `.sub`.
  - `README.md`: the Play notes for the disc systems.

## Decisions
- 2026-11-23 Which formats? → CloneCD only (`.ccd` + `.img` + optional `.sub`); `.mds`, `.toc` and `.nrg` stay out (user)
- 2026-11-23 How do they play? → Stored as-is; play-disc generates a cue from the `.ccd`, so all five disc systems play them (user)
- 2026-11-23 Saturn `.nrg` → unchanged, library-only (user)
- 2026-11-23 How do the cores take the files? → EmulatorJS 4.2.3 `downloadRom()` boots the first `.cue`/`.ccd` in the ZIP (so only the generated cue is sent); PCSX ReARMed `f29871d` `cdriso.c` opens `<content stem>.sub` after any image or cue, and parses a `.ccd` itself (not relied on) (code)
- 2026-11-23 Lone `.img`/`.sub` → wait as `IncompleteSet` for their `.ccd`, except a PlayStation lone `.img`, which stays a lone release as today; multi-disc sets accept CloneCD discs on every system (defaults, stated)
- 2026-11-23 Review rounds 1–2 → `DismissAsync` among B7's callers; `.img` dismissible only as a lone `psx` file; `NameProblem` check in B5; unknown `.ccd` keys, BOM and CR handled; every error text pinned with canonical key spelling; lone-`.img` case kept in the planner tests only (round 3); changed docs, comments and the `DiscSetsTests` case named; `.ccd` counted in the budget; `Plan`'s signature pinned; Review moved into step 2 ahead of the new extensions, with CloneCD-capable and `psx` test handlers; real-dump check widened to every core (reviewer)

- 2026-11-23 First track not 1 → `track <m> follows the start`, naming the track found (code)

## Kickoff prompt
```text
/spec implement 2026-11-23-clonecd-disc-images.md
```
Starts the spec, or resumes it at the first unticked step.
