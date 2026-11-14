---
title: Play NES, Nintendo 64 and PlayStation in the browser
status: ready
created: 2026-11-13
---

# Play NES, Nintendo 64 and PlayStation in the browser

## Summary
Signed-in users can launch NES, Nintendo 64 and PlayStation releases from the library grid or game detail. Use EmulatorJS 4.2.3 with its default cores for these systems. PlayStation launches one stored disc at a time, including the cue sheet and all tracks when present.

## Out of scope

- New import formats, disc conversion, and in-session swapping between releases of a multi-disc game.
- BIOS uploads, bundled firmware, alternative cores, and emulator version upgrades.

## Behaviour

- **B1** Map `nes` to `fceumm`, `n64` to `mupen64plus_next` and `psx` to `pcsx_rearmed`. NES uses the stored headered `.nes` and N64 the normalized big-endian `.z64`, both served through the current release download path. Neither cartridge core requests a BIOS. The game detail offers Play per eligible release. Quick Play uses the current primary release only, including an external primary for PSX. NES and N64 require embedded releases. External releases of other systems remain ineligible. The existing list/detail fields and query parameters carry the launch.
- **B2** Add authenticated `GET /api/games/{id}/roms/{romId}/play-disc` for PSX. Verify the release belongs to the specified game and the handler is PSX. A standalone external `.bin` or `.iso` streams unchanged. For a set, stream an uncompressed ZIP containing one cue and all its BIN tracks at archive root, including the release's main track. Before opening files, require portable case-insensitively unique archive leaf names and validate shape: one cue, 1–99 BINs including the main file. After opening the cue, require one-to-one coverage of all stored tracks by its distinct FILE names, with repeated references to a track allowed. Reuse the ingestion cue grammar, UTF-8/BOM and Latin-1 decoding, 64 KiB bound and portable leaf-name checks. In the playback copy, rewrite each FILE basename to the exact stored BIN spelling, retaining the rest of the cue commands. Emit the cue as UTF-8 without BOM. Keep original cue and track files byte-for-byte on disk. Scope every open to checked external-library paths and the release's registered claims. Open and validate every member before starting the response. Own every pre-opened handle through result execution and dispose it on validation failure, cancellation, write failure, or if the result never executes. Copy at most each measured member length, abort the response if a member shrinks or streaming otherwise fails after headers, and propagate request cancellation through the copy. Deliver with `Cache-Control: no-store`; no persistent archive or audit event. Use .NET 10 asynchronous ZIP create, entry open, copy and finalization with UTF-8 entry names and no compression. Stream to Kestrel without buffering disk images on the server or enabling synchronous response IO.
- **B3** The disc endpoint returns 404 `NotFound` for wrong game, release, system, missing, unsafe or conflicting member, 500 `IoError` for an unreadable member before headers, 409 `InvalidPlayDisc` for malformed or inconsistent disc metadata or cue contents, and 409 `PlayDiscTooLarge` if the combined opened lengths exceed 1 GiB. Reject empty members as invalid. Use the authenticated API group's existing anonymous and password-change responses. The limit covers a standard single CD while bounding browser memory. The list and detail buttons use library metadata and the endpoint checks current files on launch.
- **B4** For `pcsx_rearmed`, the play page fetches `play-disc` and uses the endpoint's bytes with a File named from the selected release (the existing filename sanitization applies to the File wrapper only). Keep the release name as `EJS_gameName`. In 4.2.3, compressed content is unpacked into the runtime filesystem and a supplied cue takes priority for a cue-plus-BIN archive. Display the existing session-expiry message for 401, a specific disc-too-large or invalid-disc message for the corresponding problem code, and a disc-loading message for other HTTP/network failures. Do not start the loader on any such failure. PlayStation optionally uses operator-supplied `scph5501.bin` through the existing authenticated BIOS endpoint. A 404 uses PCSX ReARMed's HLE BIOS with its documented compatibility limits; a 401, other HTTP error or network error uses the current BIOS error messages and stops before content is fetched. Set `EJS_biosUrl` only on success. Confirm the pinned core selects this firmware in its default Auto BIOS mode. Preserve the per-core BIOS option, including an explicit HLE choice. If needed, add a guarded PCSX-only adjustment in the vendor script to set the RetroArch system directory to the root where the pinned runtime writes the BIOS. Preserve HLE when the file is absent. ColecoVision retains its required BIOS. Firmware is never bundled. [PCSX ReARMed BIOS and HLE](https://docs.libretro.com/library/pcsx_rearmed/).
- **B5** Extend the guarded vendor patch so the selected explicit core wins over any saved alternative for the three new cores and `smsplus`. Hide only their core chooser. Keep generic core resolution for each system's controls. Each new core is pinned to 4.2.3 and ships both non-threaded variants, report, reviewed notices and corresponding source information. Verify exact build inputs and applicable component licences as for the previous core addition. The pinned N64 and PSX reports default to WebGL2. Verify rendering with the pinned variants under the app's current browser policy and document tested browser capabilities. [N64 4.2.3 report](https://cdn.emulatorjs.org/4.2.3/data/cores/reports/mupen64plus_next.json) [PSX 4.2.3 report](https://cdn.emulatorjs.org/4.2.3/data/cores/reports/pcsx_rearmed.json).
- **B6** The README supported-systems table shows each new core using the exact core IDs. In the README Play feature bullet, explain that PSX plays one disc per launch. The README and play documentation explain the supported release types, optional BIOS and HLE compatibility, size limit, and disc-by-disc launch.

## Changes

| Area | File(s) | Change |
|---|---|---|
| Distribution | `src/RnRomManager.Web/package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `emulator-licenses/`, `src/RnRomManager.Api/licenses.json`, `README.md` | Exact packages, guarded core pin, binaries, notices and attribution. |
| Core / API | `src/RnRomManager.Core/Ingest/CueSheet.cs`, `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` and new `PlayDisc.cs` beside it | Reuse cue parsing for a playback-only rewrite and stream the authenticated disc payload through the existing safe file opens. |
| Play page | `src/RnRomManager.Web/static/emulator/{params,play}.js` | Allow the three core IDs, fetch the disc payload, and handle optional BIOS and disc failures. |
| Systems / UI | `src/RnRomManager.Systems.Nes/NesHandler.cs`, `src/RnRomManager.Systems.Nintendo64/Nintendo64Handler.cs`, `src/RnRomManager.Systems.Sony/PlayStationHandler.cs`, `LibraryEndpoints.cs`, `src/RnRomManager.Web/src/lib/emulator.ts`, `src/RnRomManager.Web/src/routes/games/[id]/+page.svelte` | Publish mappings and allow PSX external Play from the detail and grid. |
| Docs | `README.md`, `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md` | Update platform table, operator setup, attribution, API and implementation notes. |

Existing databases, library ZIPs and sidecars need no migration or rewrite. The API adds one route and three possible `browserCore` values. It also fills the existing Quick Play fields for an external PSX primary.

## Risks

- EmulatorJS reads and unpacks the disc in browser memory before boot. The 1 GiB cap bounds input, and enough free memory is still needed for multiple runtime copies. Document this for mobile and large discs.
- The PSX core must find the exact case of the BIN names in the runtime filesystem. Test case differences, names with spaces and `#`, and cue decoding. Verify that a disc with audio tracks boots and that the runtime selected the cue.
- Browser graphics support and BIOS compatibility depend on the actual pinned WASM builds. Record playback and firmware lookup results. Use legally redistributable playable fixtures or operator-supplied dumps separate from repository fixtures. Resolve source provenance gaps before bundling the affected core.

## Steps

- [ ] **1. Vendor and pin the cores.** Add the packages, build inputs, attribution and notices described in B5. Preserve the npm overrides. Update `docs/development.md`. Tests: `vendorEmulatorjs.test.ts` checks the exact file set for all 13 cores, notice bytes, both loader paths with saved alternative cores, and generic group resolution. Run `LicenseInfoTests`, the SPA checks, and `npm run build`. Done when all three pinned cores and their release materials are served locally and the existing core tests pass.
- [ ] **2. Prepare cue sheets for playback.** In `CueSheet.cs`, share the current parser/decoder between ingest and the playback rewrite. Tests: cue parser tests cover existing ingestion behavior, quoted/unquoted FILE, repeated references, mismatched case, non-ASCII names, ANSI Latin-1/UTF-8 BOM, literal `#`, unknown and unreferenced tracks, and preservation of track/index commands. Done when the playback copy names exactly the stored payload members.
- [ ] **3. Serve the PlayStation disc.** In `LibraryEndpoints.cs` and `PlayDisc.cs`, implement B2–B3 and document the route in `docs/features.md`. Tests: `LibraryEndpointsTests.Sets.cs` or a dedicated integration partial checks exact solo BIN/ISO bytes and ZIP contents with two tracks, separate discs within one game, malformed cue, wrong release/system, missing or colliding sidecars, excessive length, cancellation and no leaked file handles. Include a real Kestrel/Playwright payload download in Step 5 so ZIP finalization is checked with synchronous response IO disabled. Add User access in `EndpointRoleMatrixTests` and check anonymous and forced-password-change responses. Done when a valid set is streamed as a playable ZIP without temporary files and every validation error precedes response streaming.
- [ ] **4. Start the new cores from the iframe.** Implement B4 and the core allowlist/chooser in `params.js` and `play.js`; update the iframe section in `docs/development.md`. Tests: `emulatorParams.test.ts` and `emulatorPlay.test.ts` cover all three IDs, BIOS ordering and HLE fallback, errors that stop before loading, PSX route selection and ZIP handoff, unmodified cartridge flow, and ColecoVision missing-BIOS regression. Done when the loader gets the right core and content, and the pinned PSX runtime boots with and without supplied firmware, with firmware selection confirmed in the supplied case. If needed, include the PCSX-only vendor adjustment and its emitted-source tests in this step.
- [ ] **5. Turn on Play and update the platform table.** Map the three handlers, extend Quick Play and detail eligibility for PSX, and implement B6. Tests: relevant handler tests, `SupportedSystemsTableTests`, `LibraryEndpointsTests` detail and primary Play cases, `emulator.test.ts`, game detail tests and Playwright. Exercise both entry points and assert the selected release and local core assets, with the seeded NES, N64 and PSX test data for delivery checks. Done when the README and both entry points agree, external Switch/Saturn remain excluded, and the pinned cores boot playable content for all three systems under the app's browser policy. Record the standalone and cue-set PSX playback and BIOS/HLE results before completion.

## Decisions

- 2026-11-13 Use the three default cores documented by [EmulatorJS](https://emulatorjs.org/docs4devs/cores/), pinned like the existing integrations.
- 2026-11-13 A PSX release represents one disc; send its full set and present each disc as a separate launch, following the current library model.
- 2026-11-13 Use optional operator-supplied `scph5501.bin` with the core's built-in HLE fallback, keeping the current BIOS directory and permissions.

## Kickoff prompt

```text
$spec implement 2026-11-13-play-nes-n64-psx.md
```

Starts the spec, or resumes it at the first unticked step.
