---
title: Nintendo DS support
status: ready
created: 2026-11-14
---

# Nintendo DS support

## Summary

Admins can import Nintendo DS cartridge dumps into the library. The app stores each release inside its game's ZIP and offers browser play through the existing release player.

## Out of scope

- DSiWare support, DSi-mode execution, DS Download Play, multiplayer, and BIOS or firmware configuration. The DS unit code is informational for archival import. A DSi-enhanced cartridge may run in DS mode. Import eligibility does not establish that a cartridge can boot.
- Changes to the shared input archive limits, emulator version, or storage format.
- Touch-screen devices. Play accepts pointer (mouse) input as the stylus. Finger input on a touch device is not verified (see Open decisions and verification).

## Behaviour

- **B1. Import and storage.** Add `NintendoDsHandler` with `Id` `nds`, `DisplayName` `Nintendo DS`, `ShortName` `DS`, `Family` `nds`, `RomExtensions` `[".nds"]`, `MinRomSizeBytes` 512, `MaxRomSizeBytes` 536,870,912 (512 MiB, inclusive), `DatName` `Nintendo - Nintendo DS`, `EmbedRomsInZip` true, the default `DatSource` (Libretro), `DatFolder` (`no-intro`) and `MetadatKinds` (all four), `FileSets` false and the identity `NormalizeForHashing`. Accept case-insensitive `.nds` from `ingest/nds/`, loose or inside ZIP or 7z archives. The existing input archive caps still apply. They allow at most 100 non-directory entries, including skipped entries, and at most 256 MiB of combined extracted supported ROM bytes. Exceeding either cap fails the archive with `ArchiveUnsafe`. Supported dumps larger than 256 MiB must be imported loose. This includes the 30 entries of exactly 512 MiB in the 2026.10.07 DAT. Skip other archive member extensions. Reject unsupported loose extensions as `UnsupportedType`. Reject out-of-range loose sizes as `TooSmall` or `TooLarge`. Hash and store the original bytes in the game's ZIP, using the standard embedded release naming and `info.json` fields. Import and Review require Admin. Any signed-in user, with or without a role, can download the ROM through the standard attachment route. That route streams the ZIP entry as `application/octet-stream` with the stored file name. It ignores `Range` and sends the full entry with 200. Use the existing download errors and password-change gate.
- **B2. Identification.** Use libretro's No-Intro `Nintendo - Nintendo DS` DAT for SHA-1 identification. Use its `releaseyear`, `developer`, `publisher` and `genre` metadat for optional CRC32-based enrichment. Filter the DAT to `.nds` before indexing hashes, DAT search, suggestions and base titles. The DAT also lists `.bcn`, `.bin` and `.ids` entries. Keep the standard release naming, grouping and media lookup by DAT game name. Deduplicate by SHA-1 across the whole library before the header or DAT decision, as the existing global step 3 does (`IngestPipeline.IdentifyAsync`). On upgrade the app starts and creates the DS folders. **Admin → DATs → Refresh DATs** supplies the five required files. Until all five pass cache validation, every import request returns HTTP 409 `DatsMissing` without creating a job. This includes a selection that contains only other systems' files. An empty valid catalog is sufficient.
- **B3. Header and Review.** `ReadHeader` returns null for spans shorter than 0x160 bytes. Text fields keep bytes 0x20–0x7E and turn every other byte into `?`. `title` is the 12 bytes at 0x00 up to the first NUL, with trailing spaces trimmed and an empty result stored as null. `gameCode` is the 4 bytes at 0x0C and `makerCode` the 2 bytes at 0x10, kept whole with no trimming. `unitCode` (0x12) and `romVersion` (0x1E) are unsigned bytes. `headerChecksumValid` is the result of the CRC check below, following the GBA field name. Expose these six fields in the existing header view and classify the header as `nds`. The CRC is CRC-16 with initial value 0xFFFF and reflected polynomial 0xA001, over bytes 0x000–0x15D, compared with the little-endian value at 0x15E. On a mismatch, mark the header implausible, so an unmatched ROM is filed as `InvalidHeader`. A DAT hit remains authoritative. An unmatched ROM with a valid CRC enters Review, and its internal title can supply suggestions. Resolve it through the existing manual or DAT-selection workflow. That workflow reverifies the pending source hash before saving and leaves a changed source pending on verification failure.
- **B4. Browser play.** Expose `desmume` for eligible DS releases in the existing game detail Play dialog and library Quick Play. Use the existing embedded-release eligibility. Fetch the selected embedded ROM through its authenticated attachment route. Use the local non-threaded EmulatorJS 4.2.3 payloads and report pinned in [Pinned DeSmuME files](#pinned-desmume-files), never the CDN. Keep the generic `nds` control scheme and settings key, and pin the concrete core `desmume` before any saved `retroarch_core` is read. Hide the Core and Threads settings. Never select a `-thread` payload, even when `SharedArrayBuffer` exists and the saved settings enable threads. Never set `EJS_biosUrl` or external files for DeSmuME. The pinned core uses its built-in BIOS and firmware whenever the external files are absent, including when saved settings enable `desmume_use_external_bios` (see Decisions). Keep browser-local battery saves (`.dsv`) with the existing release naming and 30-second autosave. Set these defaults through `EJS_defaultOptions`, alongside the existing `save-save-interval`:
  - `desmume_screens_layout: 'top/bottom'`, so both screens show with the lower screen below.
  - `desmume_pointer_mouse: 'enabled'` and `desmume_pointer_type: 'touch'`, so a pointer press acts as a stylus press at that position on the lower screen. The core's default type is `mouse`.
  - `lockMouse: 'disabled'`. The report's `supportsMouse: true` otherwise turns on pointer lock when the canvas is clicked.

  See Verification gates for large-ROM playback and distribution.

### Pinned DeSmuME files

Take these files from the published [`@emulatorjs/core-desmume@4.2.3` npm archive](https://registry.npmjs.org/@emulatorjs/core-desmume/-/core-desmume-4.2.3.tgz) (integrity `sha512-3WgGPBWzjZJWPDKVOaK/Jmg34as0nMY9ClfmrGEiMFUfa3tocpQvL9unJ8Oh4ofWEDTf+45bak/PZ7wp2xwekA==`). The package also contains two `-thread` payloads, which are not vendored.

| File in npm package | Bytes | SHA-256 |
|---|---:|---|
| `desmume-wasm.data` | 1,172,604 | `a9fddaa4bd742e558dfe5095fa4eaf074493b591a7bc18c5f7c65d64b9fa7572` |
| `desmume-legacy-wasm.data` | 1,171,393 | `2af5d9642356b9ff8460a04ae5ab013afe34825abaded974a9b613dab73c1d4b` |
| `reports/desmume.json` | 167 | `d658e595994c6bf66acf3955d31eefe41e1418d58569c702fac80535262f02c2` |

The report gives build start 2025-06-14T18:20:07Z and `{"defaultWebGL2": true, "supportsMouse": true}`. Each payload is a 7z archive holding `desmume_libretro.js`, `desmume_libretro.wasm`, `core.json` (extensions `nds`, `bin`; save `dsv`; empty `license`) and `build.json` (version 2.0.2, minimum EmulatorJS 4.2.2). Neither payload contains `license.txt`. The corresponding source was identified from both binaries:

- DeSmuME: [EmulatorJS/desmume `c6317a4`](https://github.com/EmulatorJS/desmume/tree/c6317a446c3146ee30efe55a5c0763975a100ce7), from the string `git c6317a4` in both binaries. This was the fork's `master` head from 2024-12-10 through the build date, and it is still the head. It is a fork of libretro/desmume, and its source headers are GPL-2.0-or-later. The build used `desmume/src/frontend/libretro/Makefile.libretro` with `platform=emscripten`, matching the `desmume` entry in the build repository's `cores.json`.
- RetroArch `6dd4353`, EmulatorJS/build `b24e5b5`, and Emscripten 4.0.8. Both binaries carry `6dd4353` and the LLVM producer `23e3cbb2e82b62586266116c8ab77ce68e412cf8`. These are the same inputs `emulator-licenses/COMMON-BUILD.txt` already records.

## Changes

| Area | Files | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Ingest/IngestPipeline.cs`, `ReviewResolver.cs`, `src/RnRomManager.Core/Library/LibraryEditor.cs`, `tests/RnRomManager.Core.Tests/` | Bound embedded reads in both import entry points and avoid extra whole-ROM copies for unchanged bytes. Stream the `_removed/` copy when a release or game is deleted. |
| System | `src/RnRomManager.Systems.NintendoDs/`, `tests/RnRomManager.Systems.NintendoDs.Tests/`, `RnRomManager.slnx` | Add the handler, the header reader, and 512 × 512 original fallback art with its SVG source. |
| API and fixtures | `src/RnRomManager.Api/AppHost.cs`, API project file, `Dockerfile`, `tests/RnRomManager.TestData/`, `tests/RnRomManager.Api.Tests/` | Register the handler, include its project in the Docker restore layer, add DS DAT fixtures, and exercise the existing ingest, Review and download paths. |
| Browser | Web `package.json` and `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `static/emulator/params.js`, `static/emulator/play.js`, `emulator-licenses/`, `src/lib/{emulatorParams,emulatorPlay,vendorEmulatorjs}.test.ts`, API `licenses.json` and `LicenseInfoTests.cs` | Package the local core and its notices, pin the core, set the defaults, and allow it in the standard player. Apply a DS-only Play size limit if the browser size gate requires one. |
| Runnable fixture and E2E | `tests/RnRomManager.TestData/Fixtures/`, the seed CLI (`TestDataCommand.cs`, `FixtureDatWriter.cs`, `IngestSeeder.cs`), `scripts/seed-ingest.ps1`, `scripts/test-e2e.ps1`, `tests/e2e/specs/mvp.spec.ts` | Add an original runnable DS cartridge with its source, then stage, import and play it in E2E. |
| Docs | `README.md`, `docs/adding-a-system.md`, `docs/deployment.md`, `docs/development.md`, `docs/features.md`, `docs/zip-format.md` | Document import, archive limits, memory use, browser play, the DAT upgrade, header fields and distribution attribution. |

The existing system ID column, registry-driven API responses, configuration, and `info.json` schema accommodate DS. Existing releases stay readable without a migration. No configuration key or API contract changes. The only exception is the conditional Play size limit, which changes only whether `playRomId` is set and whether Play is offered.

## Risks

- **Server memory.** Embedded import and Review hold one full ROM in memory, up to 512 MiB, while hashing and writing the ZIP. Today both paths copy it again, and Review also grows a `MemoryStream`. Deleting a release also buffers the whole entry before writing the `_removed/` copy (`LibraryEditor.CopyToRemoved`). Step 1 leaves one ROM-size array for import and Review, and none for deletion. Downloads, moves, merges, ZIP rewrites and integrity hashing already stream. Step 5 measures and documents peak memory.
- **Browser memory.** The player buffers the response as a `Blob`. EmulatorJS then reads it into an `ArrayBuffer` and copies it into the emulator file system. The browser size gate measures this separately.
- **Distribution.** The payloads carry no licence text, so the app must ship the notices itself. The source revisions are verified above. The remaining gate is the notice set (see Verification gates).

## Verification gates

- **Distribution (before Step 6).** The revisions, toolchain and file pins are recorded in [Pinned DeSmuME files](#pinned-desmume-files). Recheck the three SHA-256 values and the integrity value against `npm pack @emulatorjs/core-desmume@4.2.3`. Determine which third-party code `Makefile.common` compiles at `c6317a4` under `platform=emscripten`. Check at least `utils/tinyxml`, `utils/libfat` (its error strings are in both binaries), `utils/decrypt` and `libretro-common`, and also `utils/AsmJit` if `DESMUME_JIT` is set. Write `desmume-NOTICE.txt` in the `yabause-NOTICE.txt` format, copy `desmume-LICENSE.txt` byte for byte from `desmume/COPYING` at `c6317a4`, and add one licence file per compiled component that carries its own terms. Add DeSmuME to `COMMON-BUILD.txt`. If any compiled component's terms forbid redistribution in the image, or the corresponding source cannot be identified, stop before Step 6 and ask. Import remains available after Step 5.
- **Browser size (first part of Step 9).** Before advertising Play, test with the runnable fixture from Step 7, padded with 0xFF to 512 MiB. Set its capacity byte (0x14) to 0x0C (128 KiB << 12 = 512 MiB) and recompute the header CRC. Import it loose and resolve it in Review with **Manual**. Then open `/emulator/play.html?game=…&rom=…&core=desmume&name=…` directly as Admin in the E2E stack. Use the repository's Playwright Chromium (record its version), with a fresh browser context for each of three cold starts. Each run passes when it meets all of the following within the suite's 60-second test timeout. Both guest markers render after **Start Game**. A pointer click on the lower screen changes the lower marker within the suite's 15-second expect timeout. No `.ejs_error_text`, page error or renderer crash occurs. Record the host OS and RAM, the tested sizes, the time to both markers, and the peak working set of the Chromium renderer and GPU processes from the OS process monitor. If 512 MiB fails any run, test 256, 128 and 64 MiB in turn, and set the inclusive limit to the largest size that passes all three runs. If the unpadded fixture also fails, leave Play disabled and report it. When a limit applies:
  - The library list sets `playRomId` only when the primary release is at or below the limit. This needs the primary release's size in the list query.
  - The detail eligibility predicate in `src/lib/emulator.ts` checks `sizeBytes`.
  - `play.js` reads the release's `sizeBytes` from `GET /api/games/{id}` before fetching the ROM, including when the modal URL is opened directly. If the release is over the limit, it shows a reason that names the limit and requests no ROM, loader or core. Download stays available.
  - One constant on each side (`LibraryEndpoints.BrowserPlayMaxBytes` and `params.js` `maxPlayBytes`) holds the limit. Tests assert both values.

  Record the result and the limit in Decisions and in `docs/features.md` before enabling Play.

## Steps

- [ ] **1. Bound memory for embedded imports and deletions.** Share one bounded read between `IngestPipeline.ProcessCoreAsync` and `ReviewResolver`. The read checks the open handle's length against the path's limits before allocating. It then reads exactly that length into one array, detects early EOF or growth, and closes the handle before the store or source cleanup. Preserve ingest's `TooSmall`/`TooLarge` for out-of-range sizes and `IoError` for read failures. Preserve Review's `VerifyFailed` for over-limit or changed content and `IoError` for ordinary open or read failures. Review keeps normalizing through the queued item's handler and keeps its SHA-1 check. Reuse the backing array when normalization returns all of it, and otherwise copy the slice once. In `LibraryEditor.CopyToRemoved`, stream the entry to the new `_removed/` file while hashing it incrementally. Keep the current outcomes: the SHA-1 mismatch warning, `VerifyFailed` for an unreadable or over-long entry, and `IoError` for a write failure. On a read failure, delete the partial file. Tests in `tests/RnRomManager.Core.Tests/Ingest/` and `ReviewResolverTests.cs` cover the following:
  - A sparse temp file one byte over the limit allocates less than 1 MiB, measured with `GC.GetAllocatedBytesForCurrentThread`.
  - Early EOF and growth during the read are rejected.
  - Identity normalization returns the same array (`ReferenceEquals`).
  - Sliced and rewritten normalization, such as the existing SNES copier strip and N64 byte swap, preserve the hash and stored bytes.

  Extend `LibraryEditTests.cs` for the streamed copy's bytes and SHA-1, a truncated entry that leaves no partial file and returns `VerifyFailed`, and an unwritable `_removed/` that returns `IoError`. **Done when** these tests pass and the existing `IngestPipelineTests`, `ReviewResolverTests`, `ReviewEndpointsTests` and `LibraryEditTests` still pass.
- [ ] **2. Add the Nintendo DS handler and test data.** Add the system and test projects to `RnRomManager.slnx`. Implement B1–B3 in the handler with `BrowserCore` null. Include original fallback art and its SVG source in `Resources/boxart/`. Add a deterministic synthetic DS ROM builder and a DS catalog helper to `tests/RnRomManager.TestData/`. Do not change `IngestSeeder` output yet. NUnit verifies the handler contract values, the min/max, unchanged normalization, the six header fields, `?` replacement, title trimming and null, the PNG's 512 × 512 dimensions, and the synthetic DAT hashes. For the CRC, use 0x15E bytes that are zero except byte 0x15D set to 1, followed by checksum bytes `0D DB` (0xDB0D, verified). Verify a plausible result for that exact 0x160-byte header and an implausible one after a one-bit change at 0x15D. Verify that a change after the checksum stays plausible and that a 0x15F-byte span returns null. Verify the catalog helper can write a synthetic DS release or an empty valid catalog that `DatCache` loads. **Done when** the new test project passes and the solution builds with warnings as errors.
- [ ] **3. Register Nintendo DS and update ingest-ready fixtures.** Register the handler in `AppHost.AddSystemHandlers`, add the API project reference and the Docker restore copy, and add the README row between Nintendo 64 and NES with the cells Nintendo DS, `nds`, In ZIP, `.nds`, No-Intro and —. Make `IngestSeeder` create `ingest/nds/` and write an empty valid DS catalog for Basic and Full. Update every DAT manifest fixture that covers the full registry, preserving intentional omissions. Update the following:
  - `DatEndpointsTests.cs`: 97 → 102 refresh requests and 26 → 27 system rows.
  - `IngestSeederTests.cs`: 96 → 101 `.dat` files.
  - `SystemStartupTests.cs`: `roms/nds`, `ingest/nds` and their standard subfolders.
  - `LibraryEndpointsTests.cs`: `/api/systems` lists DS with `shortName` `DS`.
  - `IngestEndpointsTests.cs`.
  - The ordered system list in `mvp.spec.ts`, with `nds` between `n64` and `nes`.

  `SeedIngestTests` keeps Basic/Full at 41/241 inputs, 37/237 ingested, 28/228 games and 33/233 releases. The upgrade regression in `IngestEndpointsTests.cs` starts a ready host whose manifest lacks one DS metadat file. It proves that a selection of only a GB file returns 409 `DatsMissing` with no job row. It then refreshes against the stub so the five DS files load, and retries successfully. Add one loose DS import case. Update `docs/adding-a-system.md`, `docs/deployment.md` (the DAT refresh after upgrade), `docs/zip-format.md` (layout, `system` list and DS `header` fields) and `docs/development.md`. **Done when** the listed API and Core tests, `SupportedSystemsTableTests` and `pwsh scripts/build-docker.ps1` pass.
- [ ] **4. Verify identification and archive routing.** Add focused cases to `IngestArchiveTests.cs` and `IngestPipelineTests.cs`. Exercise loose, ZIP and real 7z inputs with distinct DAT-matched payloads, including an uppercase `.NDS` name, and put an ignored companion file in each archive. Commit the 7z, built from the synthetic builder, and record its recipe in `tests/RnRomManager.TestData/Fixtures/README.md` as for `basic.7z`. Assert canonical names, regional grouping, persisted metadata for all four CRC32 joins, and exact embedded bytes. Use a DAT-hit ROM with a bad header CRC to prove that the DAT match wins. Add a `.bcn` DAT entry whose SHA-1 equals an accepted `.nds` input. Prove that the input then goes to Review, that the entry appears in no DAT search or suggestion, and that it is not used as a base title. Assert that the same bytes under a different name are a `Duplicate` with no extra release. Exercise both loose size rejections, the 100-entry cap, and a DS ZIP whose two members are each within the DS limit but together exceed 256 MiB (`ArchiveUnsafe`, nothing stored). **Done when** these cases pass along with the existing ingest and DAT suites.
- [ ] **5. Verify Review, downloads and maximum-size storage.** Add Testcontainers cases to `ReviewEndpointsTests.cs` and `LibraryEndpointsTests.cs`. Cover distinct unmatched valid and invalid headers, title-based suggestions, a manual resolution of a loose item, a DAT-selection resolution of an archive item, and a same-length changed source that is rejected with the item still `Pending`. Verify the following responses:
  - A signed-in user without Admin gets 403 for import and resolution.
  - A signed-out download gets 401.
  - A mismatched game and release or a missing entry gets 404.
  - A role-less user's download returns 200 with the stored attachment name and exact bytes, including for a `Range: bytes=0-1` request.

  Assert that the `info.json` hashes agree with the ZIP entry and the source, and that no external ROM file is created. Outside routine CI, run the app container from `pwsh scripts/build-docker.ps1`. Generate distinct 512 MiB DS ROMs, one for direct DAT import and one for manual Review, and process them one at a time. Verify the ZIP entry and HTTP download SHA-1 with streamed reads (HTTP `ResponseHeadersRead`). Record the app process's idle and peak resident memory (`docker stats` or the process's `VmHWM`), the host and the container memory limit. Document the measured peak above idle in `docs/deployment.md` as the extra memory a maximum-size DS import needs. **Done when** the Testcontainers cases pass, both 512 MiB runs end `Ingested` or `Resolved` with matching hashes, and the measurement is in the deployment doc and Decisions.
- [ ] **6. Package the pinned local player.** Complete the distribution gate first. Pin `@emulatorjs/core-desmume` to exactly `4.2.3` in `package.json` and `package-lock.json`. The package depends on `@emulatorjs/emulatorjs@latest`, which the existing `overrides` entry already resolves to the pinned 4.2.3. Add `desmume` to the vendor script's `cores` list. That vendors exactly the files in [Pinned DeSmuME files](#pinned-desmume-files), under `data/cores/` and `data/cores/reports/`. Add the notices to `emulator-licenses/` and a matching entry to API `licenses.json`, and update the README attribution and `docs/development.md`. In `params.js`, add `desmume` to the allowlist (the PSP page still rejects it) and to `hidesCoreChooser`. In `play.js`, set `EJS_hideSettings` to `['retroarch_core', 'ejs_threads']` for `desmume` and add the B4 `EJS_defaultOptions`. Add `desmume` to these vendor edits:
  - The explicit-core `getCore()` guard.
  - The `threads = false` guard.
  - The `res === -1` local core-error guard, so no CDN payload fallback is attempted.
  - The `arrayBuffer()` guard that today rethrows for every core except `ppsspp`, so a rejected ROM read shows "Error reading the game file".

  Extend the web tests:
  - `vendorEmulatorjs.test.ts` exercises the generated `emulator.js` and `emulator.min.js`. With `SharedArrayBuffer` defined and saved settings of `retroarch_core: 'melonds'` and `ejs_threads: 'enabled'`, `getCore()` returns `desmume`, `getCore(true)` returns `nds`, and the stubbed download requests `cores/reports/desmume.json` and then `desmume-wasm.data`. With WebGL 2 off, it requests `desmume-legacy-wasm.data`. A missing payload shows the local error with no CDN request, and a rejected ROM read shows the read error.
  - `emulatorParams.test.ts` covers the allowlist, the PSP-page rejection and `hidesCoreChooser`.
  - `emulatorPlay.test.ts` asserts the exact globals, including no `EJS_biosUrl` or `EJS_externalFiles`.

  Handler `BrowserCore` stays null. **Done when** web lint, check, the Vitest files above and `npm run build` pass, `LicenseInfoTests.cs` passes, and the vendored output contains the three pinned files with matching SHA-256 values and no `desmume-thread*` file.
- [ ] **7. Add the runnable DS test cartridge.** Add `tests/RnRomManager.TestData/Fixtures/nds-marker/` with original source, a `Makefile` and a pinned toolchain recipe (a `devkitpro/devkitarm` image by digest). Commit the built `nds-marker.nds` and its licence notices for the linked devkitPro libraries. The program fills the top screen and the lower screen with two distinct solid colours. On the first stylus press, it changes the lower screen to a third colour and writes a marker byte to cartridge backup memory. At boot, it shows the third colour if the backup byte is already set. Its header CRC is valid. Update `Fixtures/README.md`, which today says every fixture comes from `SyntheticRomBuilder`. Add a test that the committed file's SHA-1 matches the TestData constant used for its DAT entry and that the handler reads a plausible header. **Done when** that test passes and rebuilding with the recipe reproduces the committed SHA-1.
- [ ] **8. Stage the DS cartridge in E2E.** Add an E2E staging mode to the seed CLI and `scripts/seed-ingest.ps1`. In the same pass that writes every other system's DATs and the one manifest (`FixtureDatWriter` replaces the manifest as a unit), the mode writes the DS No-Intro DAT with one game, `Marker Test (World)`. Its ROM `Marker Test (World).nds` has the size, CRC32, MD5 and SHA-1 of `nds-marker.nds`. It copies the ROM to `<work>/staged/Marker Test (World).nds`, outside `ingest/`. `scripts/test-e2e.ps1` runs the mode before its existing `chmod -R a+rwX $work`. The seeder has already created `ingest/nds/`, so the container's UID 1654 can move files out of it on Linux CI. The script exports the staged path as `E2E_STAGED_NDS`. The screenshots run skips staging. In `mvp.spec.ts`, add a test immediately after `Deleted ROMs: restore one release, delete the rest for good`. Every earlier test that asserts library-wide counts then runs before DS exists. The test copies the staged ROM to `ingest/nds/` and backdates its modified time by 60 seconds. It selects only that file and imports it. It then verifies that one release `Marker Test (World).nds` exists in game `Marker Test` and that the detail page offers no Play. **Done when** `SeedIngestTests`, `IngestSeederTests` and `pwsh scripts/test-e2e.ps1` pass with no change to earlier counts.
- [ ] **9. Enable and verify DS Play.** Complete the browser size gate. Set the handler's `BrowserCore` to `desmume`. Update the README Play cell to `` `desmume` `` in the same commit. Document playback, defaults, saves and any size limit in `docs/features.md` and `docs/adding-a-system.md`. Update `NintendoDsHandlerTests` and `LibraryEndpointsTests.cs` for detail `browserCore` and library `playRomId`. If a limit applies, also test eligibility at the limit and rejection one byte above it, in `LibraryEndpointsTests.cs`, `emulator.test.ts` and `emulatorPlay.test.ts`. Include a direct modal launch that makes no ROM, loader or core request. Extend the Step 8 E2E test so that it opens the release in a new browser context. The context aborts and records every request whose origin is not `E2E_BASE_URL`. Before navigation, an init script seeds `localStorage['ejs-1-nds-Marker Test (World).nds-settings']` with `{"controlSettings":{},"settings":{"retroarch_core":"melonds","ejs_threads":"enabled","desmume_use_external_bios":"enabled"},"cheats":[]}`. The test opens Play from the detail page and clicks **Start Game**, then asserts the following:
  - The release download returns 200.
  - `cores/reports/desmume.json` returns a same-origin 200, followed by exactly one payload request: `desmume-wasm.data` or `desmume-legacy-wasm.data`, never `-thread`.
  - No request goes to `/api/emulator/bios/`.
  - No off-origin request is made other than EmulatorJS's localhost-only `https://cdn.emulatorjs.org/stable/data/version.json` check, which CSP `connect-src 'self'` blocks.
  - The top and lower screen colours are present. The test samples the centre pixel of each screen region of a canvas screenshot, decoded in the page through a 2D canvas, within ±24 per channel.
  - After `page.mouse.click` at the centre of the lower screen, the lower screen shows the third colour.

  The test then waits 35 seconds for the autosave, closes the modal, reopens the same release in the same context, and asserts the third colour on boot. **Done when** the README table test, `pwsh scripts/test-all.ps1` and `pwsh scripts/test-e2e.ps1` pass, and the size-gate result is in Decisions.

## Decisions

- 2026-11-14 Embedded storage → keep the DS ROM bytes inside each game's ZIP (user).
- 2026-11-14 DS input → accept `.nds` only, with the existing shared input archive limits and loose import through 512 MiB. The 2026.10.07 DAT has 7,554 ROM entries: 7,471 `.nds` from 128 KiB to 512 MiB, 30 of them exactly 512 MiB, plus 55 `.bcn`, 23 `.bin` and 5 `.ids` (code and No-Intro DAT).
- 2026-11-14 Embedded read memory → reuse the original byte array when importing or resolving unchanged large dumps and verify both entry points (reviewer).
- 2026-11-14 Release deletion memory → stream `LibraryEditor.CopyToRemoved`, which today buffers the entry twice. This applies the embedded read memory decision to the one remaining whole-ROM buffer that a 512 MiB release reaches (reviewer, code).
- 2026-11-14 Browser engine → pin DeSmuME from EmulatorJS 4.2.3 and keep the generic DS controls. Use only the packaged non-threaded assets.
- 2026-11-14 BIOS and firmware → no app override. In DeSmuME `c6317a4`, `desmume_use_external_bios` is read only at first boot. When it is enabled, `PrepareBiosARM7`/`PrepareBiosARM9` mark a BIOS as loaded only after reading the file, and fall back to HLE otherwise. External firmware loads only when both BIOS files loaded (`NDSSystem.cpp`). The player never supplies those files, so a stale enabled setting still boots on the built-in BIOS and firmware. E2E seeds that setting to prove it (upstream source, reviewer).
- 2026-11-14 Pointer defaults → `desmume_pointer_type` defaults to `mouse` (a relative cursor) in `libretro_core_options.h`, so set `touch` for absolute stylus positions. It is read on every option update, so the runtime default applies. Disable `lockMouse`, which `supportsMouse: true` otherwise enables (upstream source, pinned runtime).
- 2026-11-14 Header fields → add `headerChecksumValid`, following GBA, so a DAT-matched release with a bad CRC shows why (reviewer).
- 2026-11-14 Upgrade → retain the existing global DAT preflight and require all five DS catalog files. Keep Basic and Full ROM counts unchanged.
- 2026-11-14 Browser size → measure the supported Chromium baseline. If reliable 512 MiB playback fails, enforce the largest validated size for Play and retain the 512 MiB import and download limit (user).
- 2026-11-14 Distribution → hold browser play until both package variants are tied to exact corresponding source and redistribution notices (user). The revisions are now verified from the binaries and public repositories ([Pinned DeSmuME files](#pinned-desmume-files)). Only the notice set remains (reviewer).
- 2026-11-14 Runnable fixture → add an original homebrew DS cartridge, built with a pinned devkitPro image and committed with its source and library notices. E2E checks guest output on both screens, stylus input and save persistence. This differs from earlier browser-play specs, which checked only launch routing and assets. It is the only automated proof that the core runs, and the browser size gate needs a cartridge that boots (user).
- 2026-11-14 E2E input → assert stylus input with a pointer (mouse) click, which `desmume_pointer_type: 'touch'` maps to an absolute press. Finger input on a touch device is not tested (reviewer).

## Open decisions and verification

- The browser size limit is measured in Step 9. The maximum-size server memory is measured in Step 5.
- Finger input on touch devices is not verified (see the E2E input decision).

## Review findings

- A missing DS catalog blocks even a selected import for a different system. The original upgrade language did not make the global preflight or cache validation clear. The startup and refresh regression now checks both. See `IngestJob.cs`, `DatCache.cs` and `IngestEndpointsTests.cs`.
- The existing archive tests could pass without routing any DS member through 7z. The revised cases require distinct DS payloads, verify ignored members and archive budgets, and prove the DAT precedence, extension filter and metadat joins independently. See `IngestArchiveTests.cs`, `ArchiveExtractor.cs` and `IngestPipeline.cs`.
- Both embedded read paths copied whole ROMs. Review also buffers through a growing stream and distinguishes verification from I/O failures. The revised cases check allocation, hash revalidation, ZIP bytes and the authenticated streaming response. See `IngestPipeline.cs`, `ReviewResolver.cs`, `LibraryEndpoints.cs` and `LibraryFiles.cs`.
- A stock modal cannot prove the saved-threads guard without `SharedArrayBuffer`. A visible canvas or successful asset fetch also cannot prove DS touch input. The revised tests exercise the branch in the vendored runtime and use guest-visible screen changes. See `vendorEmulatorjs.test.ts`, `static/emulator/play.js` and the pinned runtime below.
- The E2E seed writer replaces the manifest as a unit. A second DS-only write would drop every other catalog. The script now has a specified single-pass seed handoff, and the fixture remains outside ingest until the count-sensitive assertions finish. See `FixtureDatWriter.cs`, `TestDataCommand.cs`, `scripts/test-e2e.ps1` and `mvp.spec.ts`.
- Second review (2026-11-14):
  - B2 said deduplication was "within the system", but `roms.GetBySha1Async` in step 3 is global.
  - B1 left the handler's display name, DAT name and storage flags implicit, and the system menu's order depends on the display name.
  - Header "ASCII replacement" named no replacement character.
  - `LibraryEditor.CopyToRemoved` buffers a whole deleted release.
  - "Force internal BIOS" named no mechanism. The core already falls back.
  - The provenance gate was open, although the revisions can be read from the binaries.
  - The pointer and mouse-lock defaults and the payload request assertion were not concrete.
  - E2E staging did not create `ingest/nds/` before the Linux `chmod`, and it did not say where the DS test goes.
  - Step 5's memory run had no pass condition or documentation target.
  - The size-limit enforcement named no constants.
  - The `arrayBuffer` guard is PPSSPP-only today.
  - Steps had no Done-when lists.
  - Step 7 combined the fixture build and the E2E staging.
  - The ndstool line anchors had drifted.

## References

- [EmulatorJS Nintendo DS core options](https://emulatorjs.org/docs/systems/nintendo-ds/)
- [EmulatorJS 4.2.3 runtime](https://github.com/EmulatorJS/EmulatorJS/blob/v4.2.3/data/src/emulator.js) (`getCore`, `downloadGameCore`, `getLocalStorageKey`, `preGetSetting`, `getCoreSettings`), [core build manifest](https://cdn.emulatorjs.org/4.2.3/data/cores/cores.json) and [DeSmuME build report](https://cdn.emulatorjs.org/4.2.3/data/cores/reports/desmume.json)
- [`@emulatorjs/core-desmume@4.2.3`](https://www.npmjs.com/package/@emulatorjs/core-desmume/v/4.2.3)
- DeSmuME `c6317a4`: [`libretro_core_options.h`](https://github.com/EmulatorJS/desmume/blob/c6317a446c3146ee30efe55a5c0763975a100ce7/desmume/src/frontend/libretro/libretro_core_options.h), [`libretro.cpp`](https://github.com/EmulatorJS/desmume/blob/c6317a446c3146ee30efe55a5c0763975a100ce7/desmume/src/frontend/libretro/libretro.cpp) (`check_variables`), [`NDSSystem.cpp`](https://github.com/EmulatorJS/desmume/blob/c6317a446c3146ee30efe55a5c0763975a100ce7/desmume/src/NDSSystem.cpp) (`PrepareBiosARM7`, `PrepareBiosARM9`), [`Makefile.common`](https://github.com/EmulatorJS/desmume/blob/c6317a446c3146ee30efe55a5c0763975a100ce7/desmume/src/frontend/libretro/Makefile.common)
- [EmulatorJS/build `b24e5b5` `cores.json`](https://github.com/EmulatorJS/build/blob/b24e5b5/cores.json)
- [Libretro DeSmuME documentation](https://docs.libretro.com/library/desmume/)
- [Libretro Nintendo DS No-Intro DAT](https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/Nintendo%20-%20Nintendo%20DS.dat)
- Nintendo DS metadat for [release year](https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/releaseyear/Nintendo%20-%20Nintendo%20DS.dat), [developer](https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/developer/Nintendo%20-%20Nintendo%20DS.dat), [publisher](https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/publisher/Nintendo%20-%20Nintendo%20DS.dat) and [genre](https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/genre/Nintendo%20-%20Nintendo%20DS.dat)
- ndstool [cartridge header](https://github.com/devkitPro/ndstool/blob/master/source/header.h), [`CalcHeaderCRC`](https://github.com/devkitPro/ndstool/blob/master/source/header.cpp) (CRC over 0x15E bytes) and [`CalcCrc`](https://github.com/devkitPro/ndstool/blob/master/source/crc.h) (initial `~0`, reflected table)

## Kickoff prompt

```text
$spec implement 2026-11-14-nintendo-ds-support.md
```

Starts the spec, or resumes it at the first unticked step. Stop before Step 6 if the distribution gate remains open.
