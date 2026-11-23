---
title: File type gaps
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-23
---

# File type gaps

## Summary
This spec closes the gaps where a system's browser core opens a file type that ingest refuses. There are three kinds:
- **Plain aliases** that hash-match the DAT once accepted: SNES `.swc`/`.fig`, Virtual Boy `.vboy`, ColecoVision `.cv`, Game Boy `.dmg`.
- **More VICE file types** for the Commodore PET, VIC-20 and C64. These come in through Review.
- **Disc image formats** for the five cue-taking disc systems: Alcohol 120% `.mds`/`.mdf` and cdrdao `.toc` sets, plus PlayStation's single-file `.cbn`.

The disc sets are stored as they are and play the way CloneCD discs do: `play-disc` builds a cue from the `.mds` or `.toc`, so every disc core runs them. The audit that led to this spec showed that every type a system already ingests is opened by its core (`BrowserCoreExtensionsTests`).

## Out of scope
- Library-only types that never play (Jaguar `.abs`/`.cof`/`.jag`, Amiga `.ipf`, Saturn `.nrg`, a lone TurboGrafx-CD `.bin`): unchanged.
- Review-only types that no DAT lists: NES `.unf`/`.unif`, PlayStation `.exe`, Amiga `.hdf`/`.lha`/`.fdi`/`.raw`. Amiga `.dms` would need a DMS decoder.
- C64 `.vsf` (a VICE snapshot, not a game image) and PET `.crt` (the PET has no cartridge port), although VICE's shared list names both.
- SuperGrafx (`.sgx`): it would be a new system.
- TurboGrafx-CD multi-disc sets: Redump lists one two-disc title.
- Split copier files (`.1`/`.2` parts of a `.fig`/`.swc`).
- Track-by-track DAT matching for `.mdf`: an `.mdf` is the whole disc, so it matches the Redump DAT only when the disc has one track (as for CloneCD `.img`).
- Playing an `.mdf` with interleaved subchannel data (2448-byte sectors), a multi-session `.mds`, or cooked (non-raw) `.toc` tracks. These ingest but don't play (D10, D14).
- `.cbn` in multi-disc sets (`.pbp` isn't in them either).
- SPA behaviour: `canPlay` and `playRomId` already allow an external release with set files. Only `types.ts` comments change.

## Behaviour

### Aliases and Commodore types
- **A1** These `RomExtensions` gain entries, appended so `RomExtensions[0]` doesn't change:
  - `snes`: `.sfc`, `.smc`, `.swc`, `.fig`. `SnesCopierHeader.Strip` drops a 512-byte header by size alone, so a headered `.swc`/`.fig` matches the DAT's `.sfc` entries and is stored as `.sfc`. `MaxRomSizeBytes` already allows the header.
  - `vb`: `.vb`, `.vboy`
  - `coleco`: `.col`, `.rom`, `.bin`, `.cv`
  - `gb`: `.gb`, `.dmg`. `GameBoyColorHandler` is unchanged. The header still decides whether a `.dmg` is stored as Game Boy or Game Boy Color, as for `.gb`.

  A hash-matched file takes the DAT ROM's name, and a Review resolve takes `RomExtensions[0]`, as today. Size limits are unchanged. Each class comment that lists formats names the new ones (`SnesHandler`'s says `.swc`/`.fig` are stripped like `.smc`).
- **A2** These Commodore `RomExtensions` also gain appended entries. All three handlers keep `KeepsSourceExtension` true and unchanged `ReadHeader`s, so the new types never DAT-match and always go to Review, which keeps the extension. Size limits are unchanged; every new type fits 8 MiB.
  - `pet`: `.prg`, `.d64`, `.tap`, `.t64`, `.g64`, `.d80`, `.d82`, `.d71`, `.d81`, `.x64`, `.p00`
  - `vic20`: `.a0`, `.20`, `.40`, `.60`, `.bin`, `.prg`, `.d64`, `.tap`, `.t64`, `.crt`, `.b0`, `.rom`, `.d71`, `.d81`, `.g64`, `.x64`, `.p00`
  - `c64`: `.crt`, `.tap`, `.nib`, `.d64`, `.g64`, `.t64`, `.prg`, `.bin`, `.d71`, `.d81`, `.x64`, `.p00`, `.d80`, `.d82`

  Each class comment's list of the types VICE opens outside the DAT names the new ones.
- **A3** The PlayStation `.cbn` (PCSX ReARMed's compressed BIN; `cdriso.c` `handlecbin` picks it by extension) is appended to `psx` (D1). It's a single-file release, like `.pbp`:
  - It is never a set member, so it's dismissible and never DAT-matches; it always goes to Review.
  - `PlayDisc.ShapeProblem`'s generic single-file rule also accepts `.cbn`. Its error becomes `a single-file disc must be a .bin, .iso, .chd, .img, .pbp or .cbn`.
  - `play-disc` streams it unchanged, like `.pbp`. The play page keeps the release name, so PCSX ReARMed sees the `.cbn` extension.

### Accepted disc files
- **D1** On all five disc systems `RomExtensions` gain `.mds`, `.mdf` and `.toc`, and `psx` also gains `.cbn` (A3). All are appended in this order:
  - `psx`: `.bin`, `.cue`, `.iso`, `.chd`, `.img`, `.pbp`, `.ccd`, `.sub`, `.mds`, `.mdf`, `.toc`, `.cbn`
  - `saturn`: `.bin`, `.cue`, `.nrg`, `.chd`, `.iso`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc`
  - `segacd` and `3do`: `.bin`, `.cue`, `.iso`, `.chd`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc`
  - `tgcd`: `.bin`, `.cue`, `.chd`, `.ccd`, `.img`, `.sub`, `.mds`, `.mdf`, `.toc`

  Size limits don't change. Each handler's class comment says it takes MDS/MDF and TOC sets.
- **D2** `BrowserCoreExtensionsTests` gains a constant `MdsTocMember = "MDS or TOC set member: play-disc sends a generated cue (D12), never this file as the content"`. `Exceptions` gains one entry with that reason for each system and extension its core doesn't list (from the recorded `CoreExtensions`):
  - `psx`: `.mds`
  - `saturn`: `.mdf`, `.toc`
  - `segacd` and `3do`: `.mds`, `.mdf`, `.toc`
  - `tgcd`: `.mds`, `.mdf`

  `ProblemsAreNamed`'s expected list gains the matching stale-exception lines:
  - `saturn .mdf` and `saturn .toc`: `(not one of the system's extensions)`;
  - every other new entry: `(no such system)`.

### The `.toc` format (cdrdao)
- **D3** `TocSheet` (new, `src/RnRomManager.Core/Ingest/TocSheet.cs`, next to `CloneCdSheet`) parses a cdrdao `.toc`. `Parse(ReadOnlySpan<byte>)` returns a sheet with:
  - `IsValid` and `Error`;
  - `Files`: the distinct file names, in first-use order, matched ignoring case. This is the shape of `CueSheet.Files`, so the planner can treat it like a cue (D8).
  - `Tracks`: per track, its number, mode token, subchannel token or null, `Pregap` (sectors) or null, `Start` (sectors) or null, a `HasSilence` flag, and its data items. Each data item has a file name, a kind (`File` for `FILE`/`AUDIOFILE`, `Data` for `DATAFILE`), a `Swap` flag, a byte offset or null, a start in sectors or null, and a length in sectors or null.

  It is decoded and split into lines as `CueSheet` does: a leading UTF-8 BOM is dropped, and LF, CRLF or a lone CR ends a line. More than `IngestSetPlanner.MaxCueBytes` bytes is the error `the .toc is larger than 65536 bytes`. Within a line, `//` outside a string starts a comment. Tokens are split on spaces and tabs, and each statement is on one line (cdrdao itself writes them so). A string is `"…"` and can't span lines. Inside it, as in cdrdao's `TocParser.cc`, `\"` is `"`, `\\` is `\`, and `\` plus exactly three octal digits (000–377) is that character code; any other `\` is an error. Keywords match ignoring case (cdrdao is case-sensitive; accepting more costs nothing).
- **D4** Accepted statements:
  - **Before the first `TRACK`:** `CD_DA`, `CD_ROM`, `CD_ROM_XA`, `CD_I`, `CATALOG "<s>"` and a `CD_TEXT { … }` block, all ignored.
  - **Block skipping:** everything from `CD_TEXT` to the `}` that closes its first `{` is skipped, across lines, counting braces outside strings (so nested `LANGUAGE_MAP { … }`, `LANGUAGE n { … }` and binary `{ 0, 10 }` blocks are skipped too).
  - **`TRACK <mode> [<sub>]`.** `<mode>` is one of `AUDIO`, `MODE1`, `MODE1_RAW`, `MODE2`, `MODE2_FORM1`, `MODE2_FORM2`, `MODE2_FORM_MIX`, `MODE2_RAW` (cdrdao's `trackMode`; `MODE0` is only a `ZERO` mode). `<sub>` is `RW` or `RW_RAW`.
  - **Inside a track, ignored:** `COPY`, `NO COPY`, `PRE_EMPHASIS`, `NO PRE_EMPHASIS`, `TWO_CHANNEL_AUDIO`, `FOUR_CHANNEL_AUDIO`, `ISRC "<s>"`, `INDEX <time>` and a `CD_TEXT { … }` block.
  - **Inside a track, recorded:**
    - `PREGAP <time>`;
    - `START [<time>]`. A bare `START` (cdrdao: "the current track length") sets `Start` to the summed lengths of the track's earlier `SILENCE`, `ZERO`, `FILE` and `DATAFILE` items;
    - `SILENCE <time>`, and `ZERO [<mode>] [<sub>] <time>` (both set `HasSilence`). A `ZERO` `<mode>` is any `TRACK` mode or `MODE0`; a token is read as the mode, then the subchannel, when it is one;
    - `FILE "<name>" [SWAP] [#<bytes>] <start> [<length>]`, with `AUDIOFILE` as a synonym. `SWAP` sets the item's `Swap` flag. A `<length>` of 0 means "to the end of the file", so it's recorded as null;
    - `DATAFILE "<name>" [#<bytes>] [<length>]`.
  - **A `<time>`** is `mm:ss:ff` (`ss` < 60, `ff` < 75; read as `(mm*60 + ss)*75 + ff` sectors) or `0`. cdrdao also takes a plain sample or byte count; any number but `0` is the error below (see § Risks). A FILE `<start>` is a `<time>`. `#<bytes>` is `#` followed by decimal digits.
  - Not accepted, so an unknown statement: `FIRST_TRACK_NO`, `END`, `FIFO` (see § Risks).
- **D5** Parse error texts, in the order they're met while reading, left to right within a line (`n` is the 1-based line number, `t` the track number, `<WORD>` the statement's keyword upper-cased, `<token>`/`<word>` as written):
  - `the .toc is larger than 65536 bytes`;
  - `line <n>: a string isn't closed`;
  - `line <n>: a string has an unknown escape`, for a `\` not followed by `"`, `\` or three octal digits of at most 377;
  - `line <n>: unknown statement '<word>'`;
  - `line <n>: <WORD> must come after a TRACK`, for a D4 inside-track statement other than `CD_TEXT` before the first `TRACK`;
  - `line <n>: <WORD> must come before the first TRACK`, for `CD_DA`, `CD_ROM`, `CD_ROM_XA`, `CD_I` or `CATALOG` after it;
  - `line <n>: unknown track mode '<token>'`;
  - `line <n>: unknown subchannel mode '<token>'`;
  - `line <n>: '<token>' isn't a time`;
  - `line <n>: '<token>' isn't a byte offset`;
  - `line <n>: <WORD> needs a string`, when the token where `FILE`, `AUDIOFILE`, `DATAFILE`, `ISRC` or `CATALOG` needs its string isn't one;
  - `line <n>: <WORD> is incomplete`, when the line ends before a required token (`TRACK` with no mode, `PREGAP`/`SILENCE`/`ZERO`/`INDEX` with no time, `FILE` with no start, `NO` alone, a string-taking statement with no string);
  - `line <n>: START without a time needs data of known length before it`, for a bare `START` after an item with no length;
  - `line <n>: unexpected '<token>'`, for an extra token after a complete statement, or `NO` followed by anything but `COPY`/`PRE_EMPHASIS`;
  - `the .toc ends inside a CD_TEXT block`;
  - `the .toc has no TRACK`;
  - `the .toc has more than 99 tracks`;
  - `track <t> has no data`, when a track has no data item and no `SILENCE`/`ZERO`;
  - `the .toc names no file`, when `Files` is empty;
  - `the .toc names more than 99 files`.

  A name that fails `CueSheet.NameProblem` isn't a parse error. The planner makes such a set `UnsupportedSetMember` (D8), and `play-disc`'s existing member-name check refuses it.
- **D6** `TocSheet.ForPlayback(ReadOnlySpan<byte> toc, string tocFileName, IReadOnlyCollection<string> storedTracks, string trackPrefix)` returns a `PlaybackCue`, or `PlaybackCue.Fail(error)` on a D5 error. `storedTracks` are the set's other members, matched to `Files` ignoring case, as `CueSheet.ForPlayback` matches. A missing one is `'<name>' isn't part of the release`. A stored track the `.toc` doesn't name is `the .toc doesn't reference stored track(s): <names>` (ordinal-sorted, joined with `, `, as `CueSheet.ForPlayback`'s text). It checks no file names: `ShapeProblem` already has. Then the tracks are checked one at a time, in order, each running these checks in this order before the next track:
  - `track <t>: <MODE> can't be played; only AUDIO, MODE1_RAW and MODE2_RAW can` (`<MODE>` upper-cased);
  - `track <t> stores subchannel data`, when `<sub>` is set;
  - `track <t> uses SILENCE or ZERO`;
  - `track <t> has more than one FILE or DATAFILE`, counting items, not distinct names;
  - `track <t> byte-swaps its audio (SWAP)`;
  - `'<name>' is used again after another file`, when a file's tracks aren't consecutive;
  - `track <t> has no length but isn't the last track of '<name>'`, checked on the track before the current one in the same file, so every later check knows the previous length;
  - `track <t>: #<bytes> isn't a whole number of 2352-byte sectors`;
  - `track <t> starts before track <t-1> ends in '<name>'`.

  A track's position in its file, in sectors:
  - for `FILE`, `#<bytes>`/2352 plus `<start>`;
  - for `DATAFILE`, `#<bytes>`/2352 when given; otherwise the end (position + length) of the previous track in the same file, or 0 for the file's first track.

  On success:
  - `CueName` is `<toc base name>.cue`.
  - `Tracks` maps each file, in first-use order, to `<trackPrefix>NN.bin` (`NN` from 01).
  - `Content` is UTF-8 with no BOM and LF line ends. Per file it has `FILE "<trackPrefix>NN.bin" BINARY`, then for each of that file's tracks:
    - `  TRACK TT <type>` (`AUDIO`, `MODE1/2352` for `MODE1_RAW`, `MODE2/2352` for `MODE2_RAW`);
    - `    PREGAP mm:ss:ff` when `PREGAP` is set;
    - `    INDEX 00 <position>` and `    INDEX 01 <position + START>` when `START` is set, else `    INDEX 01 <position>`.

  Times are formatted as `CloneCdSheet` formats them. Every line fits `PlaybackCue.MaxLineBytes`.

### The `.mds` format (Alcohol 120%)
- **D7** `MdsSheet` (new, `src/RnRomManager.Core/Ingest/MdsSheet.cs`) parses an `.mds`'s bytes, all little-endian. The offsets follow libmirage's `image-mds.h` and PCSX ReARMed's `parsemds`.
  - **Header:** bytes 0–15 are `MEDIA DESCRIPTOR`, byte 16 is the major version, `num_sessions` is a u16 at 0x14, and the session-blocks offset is a u32 at 0x50.
  - **Session 1's block (24 bytes):** `num_all_blocks` is a u8 at +10 and the track-blocks offset is a u32 at +20.
  - **Track blocks:** `num_all_blocks` blocks of 80 bytes, from the track-blocks offset. `point` is the u8 at +4. Each track block gives:
    - the track number (`point`);
    - `mode` (+0) and `subchannel` (+1);
    - `extra_offset` (u32 +12) and `sector_size` (u16 +16);
    - `start_sector` (u32 +36) and `start_offset` (u64 +40);
    - the extra block at `extra_offset`: `pregap` (u32 +0) and `length` (u32 +4), both in sectors. An `extra_offset` of 0 means no extra block: `pregap` and `length` are 0 (libmirage would dereference null there).

  A block is a track when its `point` is 1–99. Every other block (0, 100–159 and the 0xA0+ lead-in points) is skipped. The version is byte 16 alone (libmirage compares the 16-byte signature plus 0x01; byte 17 is ignored).

  `Parse(ReadOnlySpan<byte>)` returns a sheet with `IsValid`, `Error`, `Sessions` and `Tracks` (those fields), or the first of these errors:
  - `the .mds is larger than 65536 bytes`;
  - `the .mds has no MEDIA DESCRIPTOR signature`, when the file is shorter than 88 bytes or the signature differs;
  - `only version 1 .mds files are supported`;
  - `the .mds has no session`, when `num_sessions` is 0;
  - `the .mds is truncated`, when any block it reads would end past the file;
  - `the .mds has no track 1`, when no block is a track;
  - `track <m> follows track <n>` (or `track <m> follows the start`), when the track numbers aren't 1, 2, … in block order. Track numbers are 1–99, so there's no separate track-count error.

  Parse doesn't check sessions beyond the count, subchannel, sector size or mode. A set with any of them ingests, and fails only at play (D10).
- **D8a** `MdsSheet.ForPlayback(ReadOnlySpan<byte> mds, string mdsFileName, string mdfName, long mdfLength, string trackPrefix)` returns a `PlaybackCue`, or fails with a D7 error or one of these. Each check runs over every track, in track order, before the next check starts:
  - `multi-session .mds images aren't supported`, when `Sessions` > 1;
  - `the .mdf stores subchannel data, which can't be played`, when any track's `subchannel` isn't 0;
  - `track <t> has <s>-byte sectors; only 2352 can be played`;
  - `track <t> has an unknown mode 0x<XX>`, when `mode & 0x07` is 6 (libmirage's table: 1 is audio, 2 is MODE1, 0/3/4/5/7 are MODE2);
  - `the .mdf isn't a whole number of 2352-byte sectors`;
  - `track <t> doesn't start on a 2352-byte sector`;
  - `track <t> doesn't start after track <t-1>`, for a `start_offset` that isn't greater than the previous one;
  - `track <t> starts past the end of the .mdf`, when the last track's `start_offset` is at or past `mdfLength`.

  On success:
  - `CueName` is `<mds base name>.cue`.
  - `Tracks` holds `(mdfName, "<trackPrefix>01.bin")`.
  - `Content` has `FILE "<trackPrefix>01.bin" BINARY`, then per track `  TRACK TT <type>` (`AUDIO`, `MODE1/2352` or `MODE2/2352`). For track 2 onward, a `    PREGAP mm:ss:ff` follows when the extra block's `pregap` is above 0. Track 1's `pregap` (normally 150) is the cue's implicit two seconds and is never written. Alcohol doesn't store pregaps in the `.mdf` (libmirage: "MDS format doesn't seem to store pregap data in its data file"), so the player inserts them. Then comes `    INDEX 01 <start_offset / 2352>`. `start_sector` and `length` are parsed but not used: neither libmirage nor PCSX ReARMed lays out tracks from them.

  The text is UTF-8, no BOM, LF, formatted as D6.

### Ingest
- **D8** `IngestSetPlanner.Plan` (signature unchanged):
  - **`.toc` files** are planned exactly as `.cue` files, in the same loop and with the same reasons. A `.toc` over `MaxCueBytes` stays out of the loop and is `Lone`, as an oversized cue is. A valid sheet's members are the `.toc` first, then `TocSheet.Files` in order. The set is `UnsupportedSetMember` when any of them isn't `.bin` or fails `CueSheet.NameProblem`. An unreadable or invalid `.toc` follows the broken-cue rule. A `.bin` named by both a cue and a toc counts in `namedBy` for both, so both are `SetConflict`.
  - **MDS units** come after the CloneCD units, one per `.mds` in ordinal name order: `<stem>.mds` and `<stem>.mdf`, matched ignoring case. The outcomes follow CloneCD B5:
    - a member name that fails `CueSheet.NameProblem`: `UnsupportedSetMember`;
    - an oversized, unreadable or `!MdsSheet.Parse(...).IsValid` `.mds`, with its unclaimed `.mdf`: `IncompleteSet`;
    - no `.mdf`: `IncompleteSet`;
    - a case-folded duplicate, or a member already claimed: `SetConflict`;
    - otherwise a `Set` `[mds, mdf]`, with the `.mdf` at `members[1]`, the main file.

    Every remaining `.mdf` is `Skipped` with `IncompleteSet` before the lone files. Unlike `.img`, there's no lone `.mdf` exception.
  - **Bytes read:** `PlannerFile.CueBytes` and `IngestCueReader.Read` also carry a `.toc`'s and an `.mds`'s bytes, with the same bound and checked open.
  - **Comments:** the doc comments of `PlannerFile`, `PlannedUnit`, `IngestSetPlanner`, `IngestCueReader` and `IngestReason.UnsupportedSetMember`/`IncompleteSet`/`SetConflict` say "a cue, `.toc`, `.ccd` or `.mds`" or "a cue, TOC, CloneCD or MDS set". The log message becomes `{Path}: the set sheet couldn't be read: {Error}`.
- **D9** Processing is `IngestPipeline.ProcessSetAsync`, unchanged:
  - A TOC set matches the Redump DAT track by track, like a cue set, and ingests or goes to Review (`NoDatMatch`/`AmbiguousSet`) the same way. Its `.toc` is ordinal 0 in `files`.
  - An MDS set matches only when its `.mdf` equals a listed track (a one-track disc). Then it ingests with the `.mdf` as the main file and `files` `[.mds]`. Otherwise both members go to Review as `NoDatMatch`.

### Review
- **D10** `ReviewResolver.IsSetMemberCandidate` adds `.toc`, `.mds` and `.mdf` to its extensions, so they are never dismissible (`IsDismissible` itself is unchanged). A set of any kind resolves as one release through `ResolveSetAsync` → `StoreSetReleaseAsync`. The A2 tag source (the Release flag's `named` lookup, `ReviewResolver` lines 545–548) also takes the `.toc` or `.mds` name. A dismiss of one of these members is refused with `SetFileNotResolvable`.

### Play
- **D11** `PlayDisc.ShapeProblem` accepts, on all five disc systems:
  - **A TOC set:** exactly one `.toc` plus 1–99 `.bin` members, the main file among them, the shape a cue set has. The rule becomes "exactly one `.cue` or `.toc`".
  - **An MDS disc:** exactly two members: the main member is an `.mdf`, and the other an `.mds` of the same stem, ignoring case. Checked next to `IsCloneCd`, before the cue rule.

  TurboGrafx-CD's single-file rule and PSP's rules are unchanged. The error for another multi-file shape becomes `a disc set must be one .cue or .toc and its .bin tracks, the main file among them`.
- **D12** `OpenDiscAsync` reads a TOC set's `.toc` or an MDS disc's `.mds` in full. One over `MaxCueBytes` is `the .toc is larger than 65536 bytes` or `the .mds is larger than 65536 bytes`. It then calls `TocSheet.ForPlayback` (with the bins and `trackPrefix`) or `MdsSheet.ForPlayback` (with the `.mdf`'s opened length), as `CloneCdDiscAsync` does.
  - The archive holds the generated cue and the aliased tracks. The stored `.toc`/`.mds` is never sent, since EmulatorJS 4.2.3's `downloadRom()` might pick it.
  - A `ForPlayback` error is `409 InvalidPlayDisc` with that error as the detail, prefixed for a set, as today.
  - `PlayDiscTooLarge` counts every member, the `.toc`/`.mds` included.
- **D13** Multi-disc sets:
  - `DiscSets.MainExtensions` gains `.mdf`.
  - `DiscSets.Name` also takes Disc 1's ordinal-0 `.toc` or `.mds`.
  - `play-disc-set` streams each TOC or MDS disc as D12 does, with the `disc<n>-track-` prefix, and the playlist names its generated cue.
  - The lone-`.bin` cue step and the firmware-name check apply only to a disc without a cue, so they're unchanged.
- **D14** Play-time failures use the existing problem codes and play-page text: `InvalidPlayDisc` for the D6 and D8a errors, `PlayDiscTooLarge` above 2 GiB.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `SnesHandler`, `VirtualBoyHandler`, `ColecoVisionHandler`, `GameBoyHandler` | A1 |
| Systems | `PetHandler`, `Vic20Handler`, `C64Handler` | A2 |
| Systems | `PlayStationHandler`, `SaturnHandler`, `SegaCdHandler`, `ThreeDoHandler`, `TurboGrafxCdHandler` | A3, D1 |
| Core | `src/RnRomManager.Core/Ingest/TocSheet.cs` (new) | D3–D6 |
| Core | `src/RnRomManager.Core/Ingest/MdsSheet.cs` (new) | D7, D8a |
| Core | `src/RnRomManager.Core/Ingest/IngestSetPlanner.cs` (`IngestSetPlanner`, `PlannerFile`, `PlannedUnit`, `IngestCueReader`), `IngestOutcome.cs` (reason comments), `ReviewSets.cs` (summary) | D8 |
| Core | `src/RnRomManager.Core/Ingest/ReviewResolver.cs` (`IsSetMemberCandidate` and its summary, the `named` lookup at lines 545–548) | D10 |
| Core | `src/RnRomManager.Core/Library/DiscSets.cs` (`MainExtensions`, `Name`) | D13 |
| Core (comments only) | `IngestJob.cs` (line 93), `IngestPipeline.cs` (lines 409, 588), `InfoJsonRom.cs` (line 38), `InfoJsonRomFile.cs` (line 4), `LibraryRows.cs` (line 93), `ISystemHandler.FileSets` | Say "a cue, TOC, CloneCD or MDS set" / "the cue, `.toc`, `.ccd` or `.mds`" |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs` (`ShapeProblem`, `OpenDiscAsync`, new TOC and MDS branches next to `CloneCdDiscAsync`, the class and method XML docs) | A3, D11–D13 |
| Web (comments only) | `src/RnRomManager.Web/src/lib/api/types.ts` (lines 386, 392) | Set members list `.toc`/`.mds`/`.mdf` |
| TestData | `tests/RnRomManager.TestData/ExternalSetTestHandler.cs`, `PsxSetTestHandler.cs` | `RomExtensions` gain `.mds`, `.mdf`, `.toc` |
| Tests | `TocSheetTests` (new), `MdsSheetTests` (new) in `tests/RnRomManager.Core.Tests/Ingest/`; `IngestSetPlannerTests`, `ReviewSetsTests`, `ReviewResolverTests`, `ReviewResolverExternalTests`, `DiscSetsTests`; each changed handler's tests; `BrowserCoreExtensionsTests`, `SupportedSystemsTableTests`, `IngestPipelineTests`, `ReviewEndpointsTests`, `LibraryEndpointsTests.PlayDisc.cs`, `LibraryEndpointsTests.PlayDiscSet.cs` | Per step. The assertion at `LibraryEndpointsTests.PlayDisc.cs:677` changes to A3's new single-file error text. No test asserts `a disc set must be one .cue and its .bin tracks…` today; step 8 adds one for D11's text. |
| Docs | `README.md` (table rows), `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md`, `docs/zip-format.md` | Steps 1, 7, 9 |

No migration (`files` rows hold any member), no config key and no API shape change (`setMembers` already lists any set's members).

## Risks
- **Unverified on real dumps:** no MDS or TOC rip is in the repo, so the tests use synthetic files and check the archive, not that a core boots it. The MDS offsets come from libmirage and PCSX ReARMed, not from Alcohol's documentation. Step 8's "Done when" asks the implementer to play one MDS and one TOC disc per core they own a rip for, and to record each result in § Decisions. The PR names the cores that weren't checked.
- **MDS pregaps (D8a)** come from the extra block, as libmirage reads them, on its comment that Alcohol leaves pregaps out of the `.mdf`. If a rip did store them, the cue would add a second gap of silence before that track.
- **cdrdao subset (D4–D6):** a `.toc` with sample- or byte-count times, a statement split over lines, or `FIRST_TRACK_NO`, `END` or `FIFO` doesn't parse, so its set waits in ingest as `IncompleteSet`. `SILENCE`/`ZERO`, `SWAP`, cooked modes and subchannel tracks parse but don't play. Most PlayStation/Saturn TOC rips use `MODE2_RAW`/`AUDIO` with `#` offsets into one `.bin`. The grammar was checked against cdrdao's current `trackdb/TocParser.cc`.
- **Commodore Review volume:** the new PET/VIC-20/C64 types never DAT-match, so each file needs manual resolution. That's the existing behaviour for those systems' non-DAT types.

## Steps
- [x] **1. Alias, Commodore and `.cbn` types**: A1–A3. Files:
  - the seven A1/A2 handlers and their tests (`SnesHandlerTests`, `VirtualBoyHandlerTests`, `ColecoVisionHandlerTests`, `GameBoyHandlerTests`, `PetHandlerTests`, `Vic20HandlerTests`, `C64HandlerTests`);
  - `PlayStationHandler` (`.cbn` only) and `PlayStationHandlerTests`;
  - `PlayDisc.cs` (single-file rule only);
  - `LibraryEndpointsTests.PlayDisc.cs`;
  - `README.md` rows.

  Tests:
  - the handler tests' extension lists;
  - `BrowserCoreExtensionsTests` and `SupportedSystemsTableTests`;
  - a SNES unit test that a 512-byte-headered `.swc` normalises to the headerless bytes (`SnesHandlerTests`);
  - `LibraryEndpointsTests.PlayDisc.cs`: a standalone PlayStation `.cbn` streams unchanged as `application/octet-stream`, and line 677's text is updated.

  Done when they pass.
- [ ] **2. TOC parsing**: D3–D5. Files: `TocSheet.cs` (new, `Parse` and the records), `TocSheetTests.cs` (new). Done when `TocSheetTests` covers:
  - a one-file and a two-file toc giving the expected `Files` and `Tracks` (byte offsets, starts, lengths, `PREGAP`, `START`; positions are step 3's);
  - comments, `CD_TEXT` blocks at disc and track level (one with a nested `LANGUAGE_MAP` and a `{` inside a string), a BOM, LF/CRLF/CR and mixed-case keywords;
  - string escapes (`\"`, `\\`, `\101`), `FILE … SWAP`, a `FILE` length of 0 recorded as null, a `ZERO MODE0` item, and a bare `START` after a sized `FILE`;
  - every D5 error text, as one `[TestCase(toc, error)]` row each (as `CloneCdSheetTests` does for line ends), which keeps the step near 300 lines.
- [ ] **3. TOC playback**: D6. Files: `TocSheet.cs` (`ForPlayback`), `TocSheetTests.cs`. Done when `TocSheetTests` covers:
  - a one-file `MODE2_RAW` + two `AUDIO` toc with `#` offsets, `START` and `PREGAP`, giving the exact cue text;
  - a two-file toc (`DATAFILE` + `FILE`) giving two `FILE` lines;
  - a `DATAFILE` continuing after a sized track;
  - every D6 error text, including the no-length track that isn't last in its file and an unreferenced stored track.
- [ ] **4. MDS sheet**: D7, D8a. Files: `MdsSheet.cs` (new), `MdsSheetTests.cs` (new; builds `.mds` bytes in the test). `MdsSheet` is far smaller than `TocSheet`, so parse and playback are one step. Done when `MdsSheetTests` covers:
  - a one-track MODE2 disc, and a data + two-audio disc with a point-0 block, 0xA0–0xA2 lead-in blocks and track 2's extra-block `pregap` of 150 written as `PREGAP 00:02:00` (track 1's 150 not written), both giving the exact cue text;
  - a track with `extra_offset` 0 (no `PREGAP`);
  - every D7 and D8a error text.
- [ ] **5. Planning**: D8, before any handler accepts the new extensions. Files:
  - `IngestSetPlanner.cs`, `IngestOutcome.cs`, `ReviewSets.cs`;
  - both TestData handlers;
  - the comment-only Core files, `IngestJob.cs` included.

  Tests:
  - `IngestSetPlannerTests`:
    - TOC sets: a valid set; a missing `.bin`; a non-`.bin` member; a member failing `NameProblem` (`UnsupportedSetMember`); an invalid `.toc`; an oversized `.toc` (`Lone`); a `.bin` named by a cue and a toc (both `SetConflict`).
    - MDS sets: a full set; a missing `.mdf`; an invalid `.mds`; a member failing `NameProblem` (`UnsupportedSetMember`); a case-folded duplicate (`SetConflict`); a lone `.mdf` (`IncompleteSet`).
  - `ReviewSetsTests`: finds a TOC set and an MDS set from each member.

  Done when they pass.
- [ ] **6. Review**: D10. Files: `ReviewResolver.cs`, `types.ts` (comments). Tests:
  - `ReviewResolverTests`: `IsDismissible` is false for `.toc`/`.mds`/`.mdf` and true for a `.cbn`;
  - `ReviewResolverExternalTests`:
    - a TOC set resolves as one release, with main the first `.bin` and `files` `[.toc, other bins]`;
    - an MDS set resolves as one release, with main `.mdf` and `files` `[.mds]`;
    - each set's Release flag takes its tags from the `.toc`/`.mds` stem (lines 545–548).

  Done when they pass, and `npm run lint` and `npm run check` pass in `src/RnRomManager.Web`.
- [ ] **7. Accept the disc files**: D1, D2, D9, and the README rows. Files:
  - the five disc handlers and their tests;
  - `BrowserCoreExtensionsTests.cs`, `README.md`;
  - `IngestPipelineTests.cs`, `ReviewEndpointsTests.cs`.

  Tests:
  - the handler tests, `BrowserCoreExtensionsTests` (including `ProblemsAreNamed`) and `SupportedSystemsTableTests`;
  - `IngestPipelineTests`:
    - a synthetic Saturn MDS set goes to Review as two `NoDatMatch` items;
    - a one-track PlayStation `.mdf` listed in the fixture DAT ingests with `files` `[.mds]`;
    - a PlayStation TOC set whose `.bin` is in the fixture DAT ingests with `files` `[.toc]`;
    - a lone Sega CD `.mdf` is `Skipped`/`IncompleteSet`.
  - `ReviewEndpointsTests`:
    - GET on a TOC set's `.toc` shows `resolvable` true, `dismissible` false and its `setMembers`;
    - dismissing an MDS set's `.mdf` is refused with `SetFileNotResolvable`.

  Done when they pass, with the integration classes run on Docker.
- [ ] **8. Play**: D11–D14. Files: `PlayDisc.cs`, `DiscSets.cs`. Tests:
  - `LibraryEndpointsTests.PlayDisc.cs`:
    - an MDS Saturn disc streams a ZIP with exactly the generated cue and `track-01.bin`, and no `.mds`;
    - a TOC PlayStation set streams the generated cue and `track-01.bin`…, and no `.toc`;
    - a TurboGrafx-CD TOC set plays;
    - an `.mdf` with subchannel gives `409 InvalidPlayDisc` with D8a's text;
    - a cooked-mode `.toc` gives `409 InvalidPlayDisc` with D6's text;
    - D11's new multi-file shape error text, for an `.mdf` with an `.mds` of another stem;
    - D12's `the .toc is larger than 65536 bytes` and `the .mds is larger than 65536 bytes`.
  - `LibraryEndpointsTests.PlayDiscSet.cs`: a two-disc Saturn set of MDS discs (playlist lines, `disc1-track-01.bin`, `disc2-track-01.bin`).
  - `DiscSetsTests`: `.mdf` mains group into a set, and the `.toc`/`.mds` set names.

  Done when they pass, and the real-dump check from § Risks is done for the cores available or noted for the PR.
- [ ] **9. Docs**: the doc rule. Tests: `SupportedSystemsTableTests`. No Playwright change. Done when it passes and `docs/codemap.md` is regenerated. Files:
  - `docs/features.md`:
    - Ingestion (:197): the alias and Commodore types;
    - Disc sets (:233): the **Sets**, **Waiting sets** and **No match** bullets (TOC like cue, MDS by stem, a lone `.mdf` waits);
    - Review queue (:260): the unmatched disc sets and track files without a set bullets;
    - Resolve reviews through the API (:367): set members and `dismissible`;
    - Game detail Play (:144–157): MDS/TOC discs play through a generated cue, plus the D14 limits; the PlayStation bullet (:144) names a standalone `.cbn`; the VIC-20 bullet (:154) adds `.b0` to the cartridge extensions ingest keeps (VICE maps it at $B000), and `.rom`.
  - `docs/development.md` Browser emulators: the play-disc and Multi-disc paragraphs, and `.cbn`.
  - `docs/adding-a-system.md`: the `FileSets` row and "Limits of the contract" (`.toc` and `.mds` are now grouped; `.gdi` still isn't).
  - `docs/zip-format.md`: lines 49–62 and the `files` row (:203): `files` may hold a `.toc` or `.mds`.
  - `README.md`:
    - line 3, which names the set kinds kept together: add TOC and MDS sets;
    - line 15 and the disc systems' Play notes: MDS/TOC discs, and a standalone PlayStation `.cbn`.

## Decisions
- 2026-11-23 Which gaps? → Core-supported types that ingest refuses (C); TurboGrafx-CD sets (D) dropped, since Redump has one two-disc title (user)
- 2026-11-23 Which alias/Review types? → Aliases that DAT-match plus VICE types for PET/VIC-20/C64; Review-only `.unf`/`.unif`/`.exe`/Amiga types left out; `.dms` skipped (needs a decoder) (user)
- 2026-11-23 Disc formats? → `.mds`/`.mdf`, `.toc` and `.cbn`, on all five disc systems through a generated cue, as CloneCD (user)
- 2026-11-23 Subchannel `.mdf` (2448-byte sectors) → ingest, don't play (user)
- 2026-11-23 Review round 1:
  - an oversized `.toc` is `Lone`, like a cue;
  - bad `.toc` member names are `UnsupportedSetMember`;
  - new errors: `the .toc names no file`, an unreferenced stored track, a no-length track that isn't last, and disc statements after a TRACK;
  - sample-count tocs don't ingest (Risks);
  - Review resolve tests added;
  - `IngestJob.cs`/`types.ts` placed in steps, and README lines 3/15 in the docs step;
  - the TOC work split into parse and playback steps, with Review its own step (reviewer)
- 2026-11-23 Review round 2 → step 2 asserts D3's fields, not positions (reviewer)
- 2026-11-23 Audit list corrections → C64 `.vsf` (snapshot) and PET `.crt` (no cartridge port) dropped from the proposed list; no libretro DAT lists any added type except where the bytes equal an existing entry (code)

- 2026-11-23 Review round 3, checked against libmirage `image-mds`, PCSX ReARMed `cdriso.c` and cdrdao `TocParser.cc`:
  - MDS offsets, the mode table and the version byte confirmed;
  - a track block is `point` 1–99 (point 0 skipped too), and `extra_offset` 0 means no extra block;
  - MDS pregaps come from the extra block, not `start_sector` (which neither reader uses), so the "positions don't agree" and unreachable "more than 99 tracks" errors are dropped;
  - TOC: `MODE0` isn't a track mode, a bare `START` and `FILE … SWAP` are accepted, a `FILE` length of 0 means to the end, strings take cdrdao's escapes, and `FIRST_TRACK_NO`/`END`/`FIFO` stay unknown (Risks);
  - new pinned errors: unknown string escape, `<WORD> needs a string`, `<WORD> is incomplete`, bare `START` after unsized data, `SWAP` at play; "more than one file" counts items;
  - MDS play shape is exactly two members; tests added for D12's size errors (reviewer)
- 2026-11-23 Review round 4 → D5 `<WORD>`/`<token>` casing and left-to-right order pinned, `CD_TEXT` exempt from "must come after a TRACK"; D6 runs all checks per track, track by track, and checks no names; D8a runs each check over all tracks before the next; step 2's error tests are `[TestCase]` rows (reviewer)
- 2026-11-23 Review round 5 → § Changes names `ReviewResolverExternalTests` and the same `ReviewResolver` lines as D10 (reviewer)
- 2026-11-23 Review round 6 → step 5 tests an MDS member failing `NameProblem`; step 6 names lines 545–548 (reviewer)
- 2026-11-23 Review round 7 → step 9 updates the VIC-20 Play bullet, which lists the cartridge extensions (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-23-file-type-gaps.md
```
Starts the spec, or resumes it at the first unticked step.
