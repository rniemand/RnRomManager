---
title: Nintendo DS support
status: ready
created: 2026-11-14
---

# Nintendo DS support

## Summary

Admins can import Nintendo DS cartridge dumps into the library. The app stores each release inside its game's ZIP and offers browser play through the existing release player.

## Out of scope

- DSiWare, DSi-only execution, DS Download Play, multiplayer, and BIOS or firmware configuration.
- Changes to the shared input archive limits, emulator version, or storage format.

## Behaviour

- **B1. Import and storage.** Register Nintendo DS as `nds`, with short name `DS` and family `nds`. Accept case-insensitive `.nds` files from `ingest/nds/` loose or in the existing ZIP/7z flow. Accept 512 bytes through 512 MiB per ROM, including both endpoints. The existing 100-entry and 256 MiB combined extracted-ROM limits still apply to input archives. Import larger supported dumps loose. Existing rules skip other archive member extensions and reject unsupported loose extensions with `UnsupportedType`. Preserve `TooSmall`, `TooLarge` and `ArchiveUnsafe` handling. Hash and store the original bytes. Use the standard embedded release entry and the normal admin-only import and Review workflow.
- **B2. Identification.** Use libretro's No-Intro `Nintendo - Nintendo DS` DAT and its release year, developer, publisher and genre metadat. Match SHA-1 and keep the common filename, grouping, deduplication, and media behavior. Match and search only the handler's supported extension. Existing installations refresh DATs in Admin → DATs. Ingestion returns `DatsMissing` until the required DS DATs are present.
- **B3. Header.** Read the 12-byte internal title at 0x00 (ASCII through the first NUL, trim trailing spaces, blank becomes null), game code at 0x0C (4 ASCII bytes), maker code at 0x10 (2 ASCII bytes), unit code at 0x12 and ROM version at 0x1E (unsigned bytes). Expose those values as `title`, `gameCode`, `makerCode`, `unitCode` and `romVersion` in the existing header view. Classify as `nds`. Use the header CRC-16 (initial 0xFFFF, reflected polynomial 0xA001) over bytes 0x000–0x15D and compare it to the little-endian value at 0x15E. For unmatched input a matching CRC takes the existing Review path and the internal title can supply suggestions. A mismatch takes the existing `InvalidHeader` path. A DAT hit remains authoritative. Return null for buffers shorter than the checksum field and handle arbitrary input without throwing.
- **B4. Browser play.** Expose `desmume` for Nintendo DS and start the selected embedded release via the authenticated ROM download in the existing game-detail Play dialog. Vendor the 4.2.3 DeSmuME core with the app. Pin it against saved core selection and disable threaded payload selection. Use EmulatorJS's DS controls, including pointer input on the touch screen. Browser play uses the core's internal BIOS implementation and the existing save behavior. It uses the same permissions and download errors as other embedded cartridges.

## Changes

| Area | Files | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Ingest/IngestPipeline.cs`, `ReviewResolver.cs` | Bound embedded reads in both entry points and avoid extra whole-ROM copies for unchanged bytes. |
| System | `src/RnRomManager.Systems.NintendoDs/`, `tests/RnRomManager.Systems.NintendoDs.Tests/`, `RnRomManager.slnx` | Add handler, header reader, and 512 × 512 original fallback art with its SVG source. |
| API and fixtures | `src/RnRomManager.Api/AppHost.cs`, API project file, `Dockerfile`, `tests/RnRomManager.TestData/`, `tests/RnRomManager.Api.Tests/` | Register the handler, include its project in Docker restore, add DS DAT fixtures and exercise the existing ingest and download paths. |
| Browser | `src/RnRomManager.Web/package.json`, lockfile, `scripts/vendor-emulatorjs.mjs`, `static/emulator/params.js` and `emulator-licenses/` within the web project, `src/RnRomManager.Api/licenses.json` | Package the local core, build report and license notices and allow it in the standard player. Reuse the generic browser capability and ROM download APIs. |
| Docs | `README.md`, `docs/adding-a-system.md`, `docs/deployment.md`, `docs/development.md`, `docs/features.md`, `docs/zip-format.md` | Document import, archive limits, browser play, DAT upgrade, header fields and distribution attribution. |

The existing system ID column, registry-driven API responses, configuration, and `info.json` schema accommodate DS. Existing releases stay readable without a migration.

## Risks

- The embedded pipeline and browser buffer ROM bytes. Exercise direct import and manual Review resolution of 512 MiB loose synthetic dumps and verify byte preservation with streamed checksums. Check browser startup and memory use for a large runnable cartridge on the supported Chromium baseline and document any observed browser constraints.
- Keep the runtime, core package and provenance pinned. Verify the package report and corresponding source revisions and record the core and linked RetroArch license notices before distributing the binary.
- The DAT refresh is a preflight for all systems. Every fixture that represents an ingest-ready installation needs the DS DAT and its required metadat.

## Steps

- [ ] **1. Bound memory for embedded imports.** In the two Core ingest files in Changes, share a bounded embedded read that checks the open file length before allocation, reads exactly that length and detects growth or truncation. Preserve the normal I/O failure and Review verification behavior. Reuse the read array when normalization returns the full original memory; copy changed or sliced normalization results. Use NUnit file tests for the size bound, short reads and normalized slices, and run existing ingest and Review API integration tests. Done when either path retains only one ROM-sized array for unchanged bytes and existing normalizing systems store and hash the same bytes as before.
- [ ] **2. Add the Nintendo DS handler and fixtures.** Add the system and test projects to `RnRomManager.slnx`. Implement B1–B3 in the handler with browser capability enabled in step 5. Include fallback art. Add deterministic synthetic DS header/ROM data and a DS catalog helper in `tests/RnRomManager.TestData/`. Run NUnit tests for the handler contract, min/max, unchanged normalization, title and header fields, independently known CRC, mismatched CRC, short input, PNG dimensions and synthetic DAT hashes. Done when the new tests pass and fixtures can produce a catalog with one synthetic DS release or an empty valid catalog.
- [ ] **3. Register Nintendo DS.** Register the handler in `AppHost.cs`, add the API reference and `Dockerfile` restore copy, and update the README table for import with browser play pending step 5. Add the DS catalog to the active all-system DAT fixtures and update fixed assertions in `DatEndpointsTests.cs` (five additional refresh requests), `SystemStartupTests.cs`, `LibraryEndpointsTests.cs`, `IngestEndpointsTests.cs` and `tests/e2e/specs/mvp.spec.ts`. In Basic and Full seed catalogs add the five required DAT files and update `IngestSeederTests.cs` and developer docs; keep existing seed ROM totals and use focused DS fixtures for DS-specific flows. Update the ingest, deployment, supported-system and ZIP docs from Changes. Done when the supported-systems table test, startup tests, DAT tests and existing ingest suites pass.
- [ ] **4. Exercise DS import and Review.** In the API integration suite use Testcontainers and the focused DS fixtures to cover Admin and User permissions, loose and zipped DS input, byte-identical ZIP entry and authenticated download, duplicate, unmatched valid and invalid header cases, manual Review resolution and size rejection. Exercise ZIP/7z through the existing archive regression suite. Verify both large-ROM paths from Risks with one generated 512 MiB fixture at a time outside the routine test suite and record the results. Done when DS imports need no sidecar file and downloads preserve bytes from both ingest and Review.
- [ ] **5. Enable DeSmuME browser play.** Pin `@emulatorjs/core-desmume@4.2.3` in the web package and lockfile. Vendor standard and legacy non-threaded assets and report via `scripts/vendor-emulatorjs.mjs`. Add source and license notices in `src/RnRomManager.Web/emulator-licenses/` and the core entry in `src/RnRomManager.Api/licenses.json`. Verify attribution in `LicenseInfoTests.cs`. Add `desmume` to the play-page allowlist and hide its core chooser. Extend the vendor script's explicit-core and non-threaded guards. Set the handler's browser core and update the README table, browser feature list, attribution and developer docs in this commit. Add Vitest coverage for normal page acceptance, isolated PSP-page rejection, chooser and BIOS behavior, and a Playwright Chromium smoke test using a small original runnable DS test cartridge. In `scripts/test-e2e.ps1` add the DS test cartridge row to the E2E catalog before the container starts and stage its ROM outside the ingest tree. At the end of the count-sensitive serial flow in `tests/e2e/specs/mvp.spec.ts`, copy the ROM into `ingest/nds/` with the standard fixture age and import it before launching Play. Preserve the shared Basic/Full ROM totals. In Chromium seed stale `retroarch_core=melonds` and `ejs_threads=enabled` settings in the storage read by the pinned runtime, and assert that its first core payload request is for a local non-threaded DeSmuME variant. Verify Play opens the modal, starts the local core, displays both screens and accepts a touch input with no BIOS files and no external emulator requests during normal startup. Done when the vendored player starts DS releases, saved core/thread preferences cannot switch it off its packaged payloads, and the supported-systems table, web checks and Playwright pass.

## Decisions

- 2026-11-14 Embedded storage → keep the DS ROM bytes inside each game's ZIP (user).
- 2026-11-14 DS input → accept `.nds` only, with the existing shared input archive limits and loose import through 512 MiB (code and No-Intro DAT).
- 2026-11-14 Embedded read memory → reuse the original byte array when importing or resolving unchanged large dumps and verify both entry points (reviewer).
- 2026-11-14 Browser engine → pin DeSmuME so standard DS playback works without operator-supplied firmware (EmulatorJS and Libretro docs).

## References

- [EmulatorJS Nintendo DS core options](https://emulatorjs.org/docs/systems/nintendo-ds/)
- [EmulatorJS 4.2.3 core packages](https://cdn.emulatorjs.org/4.2.3/data/cores/)
- [Libretro DeSmuME documentation](https://docs.libretro.com/library/desmume/)
- [Libretro Nintendo DS No-Intro DAT](https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/Nintendo%20-%20Nintendo%20DS.dat)
- [ndstool cartridge header](https://github.com/devkitPro/ndstool/blob/master/source/header.h) and [CRC implementation](https://github.com/devkitPro/ndstool/blob/master/source/crc.cpp)

## Kickoff prompt

```text
$spec implement 2026-11-14-nintendo-ds-support.md
```

Starts the spec, or resumes it at the first unticked step.
