---
title: PSP, Genesis and Master System browser play
status: ready
created: 2026-11-13
---

# PSP, Genesis and Master System browser play

## Summary

Signed-in library users can launch PlayStation Portable UMD images with PPSSPP and Sega Genesis (Mega Drive) cartridges with operator-supplied Genesis Plus GX. Sega Master System already plays through SMS Plus GX. Cover that path with a real browser regression test.

## Out of scope

- New ingestion formats, ROM storage changes, firmware management, and browser support for Sega CD or 32X.
- Automatic downloads or redistribution of Genesis Plus GX by the project.
- An EmulatorJS version upgrade, new save features, or changes to PSX disc packaging.

## Behaviour

- **B1. Discovery.** Existing PSP and Genesis games receive their core from the system handler. The library's quick Play uses the primary release, and game details offer Play per release. Use the existing JSON fields. Allow external PSP releases in the same list/detail eligibility checks that allow PSX. Genesis Play is visible on installations without the optional core and leads to the setup message in B4. SMS keeps its existing Play actions and modal.
- **B2. PSP content.** Extend authenticated `GET /api/games/{id}/roms/{romId}/play-disc` to stream a standalone external PSP `.iso` byte-for-byte with no-store and no range support. Apply the existing game/release association, matching system, exact stored name, sole ownership and checked file-open rules. Missing, unsafe or disputed files and unrelated IDs return 404. Nonportable or mismatched metadata names, sets, other extensions and empty files return 409 `InvalidPlayDisc`. Bound the opened ISO length at 2 GiB inclusive, covering standard UMD sizes, and return 409 `PlayDiscTooLarge` above it. Keep PSX's 1 GiB cap, statuses, firmware and ZIP behavior. Ordinary downloads keep their current eligibility and limits.
- **B3. PSP player.** Both Play actions open `/emulator/play-psp.html` in a new tab via a link with `rel="noopener"`. Serve the dedicated top-level document with COOP `same-origin`, COEP `require-corp`, and the emulator CSP with `frame-ancestors 'none'`. Use the shared player script and styles and leave the SPA and modal headers in place. Require the matching PSP core on the dedicated page and reject PSP on the modal page. Before fetching the ISO, report a clear error for insecure contexts or unavailable cross-origin isolation, SharedArrayBuffer, or WebGL2. Use HTTPS or a browser-trusted loopback origin. Fetch via B2 with same-origin credentials, name the EmulatorJS File after the ISO with the existing filename sanitization, and show PSP-specific size and invalid-disc errors, the existing expired-session message, or a disc-loading failure. No BIOS request. Set `EJS_threads = true`, force the threaded variant even if saved `ejs_threads` is disabled, and load the PPSSPP build report, `ppsspp-thread-wasm.data` and `ppsspp-assets.zip` from the same origin. Closing the tab stops playback.
- **B4. Genesis delivery.** Add optional `Emulator:GenesisCorePath` (`Emulator__GenesisCorePath` in Docker), an absolute directory supplied by the deployment operator. Blank means disabled. A relative configured path fails startup validation. A missing absolute mount behaves as unavailable without failing startup. The read-only mount contains the EmulatorJS 4.2.3 files `genesis_plus_gx-wasm.data`, `genesis_plus_gx-legacy-wasm.data` and `genesis_plus_gx.json` at its root. Serve just these exact names via authenticated `GET /api/emulator/genesis/{name}` using the checked read pattern from the BIOS endpoint, with no-store and the binary or JSON content type as applicable. Missing, empty, unsafe and unlisted files return 404, and unexpected read errors return 500 `IoError`. Support an authenticated `HEAD /api/emulator/genesis` availability check that opens and disposes all three regular files and returns 204 when all are present, 404 when disabled or incomplete, and 500 for an unexpected read error. Check this before fetching a ROM or starting the loader. Report missing setup with the configuration name, map 401 to session expiry, and show a core-loading error for other failures. Direct asset GETs enforce the same checks on the requested file.
- **B5. Genesis player.** Route `.md`, `.gen` and `.bin` through the existing authenticated cartridge download and modal. Allow the explicit `genesis_plus_gx` core in the play link. Pin it in the guarded vendoring edit and hide the core chooser so a saved alternate core cannot replace it. Set the `segaMD` control scheme explicitly because upstream resolves the core's first group to `segaMS`. Verify the pinned loader keeps its default of skipping automatic cue generation for Genesis cartridges. Map the three asset basenames to the B4 same-origin URLs through `EJS_paths`. Preserve the current database and save settings. Neither the image nor the npm lockfile contains the operator's core.
- **B6. Master System.** A stored `.sms` release plays with the bundled `smsplus` and the Master System controls. The saved core preference cannot switch it to Genesis Plus GX. Verify this both with Genesis assets absent and with the pinning regression test. Game Gear continues to use the existing filename-based control scheme.

## Changes

| Area | Files | Change |
|---|---|---|
| Systems and library API | `src/RnRomManager.Systems.Sony/PlayStationPortableHandler.cs`, `src/RnRomManager.Systems.Sega/MegaDriveHandler.cs`, `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs`, `PlayDisc.cs` in the same folder | Advertise both cores and extend external playback and PSP disc streaming. |
| Operator configuration and asset API | `src/RnRomManager.Api/Configuration/OptionsRegistration.cs`, `src/RnRomManager.Core/Configuration/EmulatorOptions.cs` (new), `src/RnRomManager.Api/Endpoints/EmulatorEndpoints.cs` | Add and validate the optional path and serve the fixed asset set. |
| Browser isolation | `src/RnRomManager.Api/Security/SecurityHeaders.cs`, `src/RnRomManager.Web/static/emulator/play-psp.html` (new), `play.html` and `play.css` (new) in the same folder | Serve a dedicated isolated PSP document. |
| Web and vendoring | `src/RnRomManager.Web/package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `emulator-licenses/`, `static/emulator/params.js`, `static/emulator/play.js`, `src/lib/emulator.ts`, `src/lib/components/GameResults.svelte`, `src/routes/games/[id]/+page.svelte` (all under the web project) and `src/RnRomManager.Api/licenses.json` | Bundle PPSSPP, wire both player paths, and record the shipped PPSSPP notices and corresponding source. |
| Documentation | `README.md`, `docs/features.md`, `docs/development.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/adding-a-system.md` | Document support, size and browser requirements, optional read-only mount and exact files, troubleshooting, and the vendoring rules. |

No migrations or changes to stored ROMs are needed. Existing library records gain Play access through the handlers and API projections.

## Risks

- PPSSPP requires WebGL2, a secure isolated top-level page and enough memory to load the ISO. Confirm an actual boot with the 4.2.3 build and a redistributable or locally supplied lawful bootable ISO before opening the PR. The synthetic Playwright ISO proves request handling and startup wiring only. Keep the approval of browser support tied to observed boot, including a representative large ISO where available.
- PPSSPP assets have their own provenance and notices. The exact 4.2.3 npm tarball contains the threaded payload, build report and asset ZIP. Review the payload and asset ZIP license texts and source revisions before bundling. [EmulatorJS 4.2.3 core artifacts](https://cdn.emulatorjs.org/4.2.3/data/cores/)
- Genesis depends on the operator supplying compatible files and following their upstream terms. Verify an actual boot with privately provisioned 4.2.3 assets before opening the PR. Automated tests use synthetic assets for delivery/wiring and avoid publishing the restricted binary. [EmulatorJS Mega Drive cores](https://emulatorjs.org/docs/systems/sega-mega-drive/) · [Libretro core licenses](https://docs.libretro.com/development/licenses/)
- Scope isolation headers to the dedicated PSP document, so cross-origin isolation does not affect the SPA or its existing modal flows. [Cross-origin isolation requirements](https://developer.mozilla.org/en-US/docs/Web/API/Window/crossOriginIsolated)

## Steps

- [ ] **1. Serve PSP ISOs for playback.** Implement B2 in `PlayDisc.cs`. Tests: extend `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.PlayDisc.cs` for byte-exact streaming, 401/404, unsafe or conflicting names, ISO validation and actual file length above the limit using a sparse file. Done when PSP streams via the checked resolver and existing PSX coverage passes.
- [ ] **2. Serve optional Genesis core files.** Implement B4 server behavior in `EmulatorOptions.cs`, `OptionsRegistration.cs`, `EmulatorEndpoints.cs`. Tests: `Integration/EmulatorEndpointsTests.cs` covers signed-in User, anonymous denial, configured and disabled paths, incomplete set, unsupported names, unsafe files, content types and exact bytes. Update `Integration/EndpointRoleMatrixTests.cs` for both registered routes. Add coverage for rejecting a relative configured path at startup. Done when a default deployment exposes no Genesis assets and configured assets pass the checks.
- [ ] **3. Vendor and isolate PPSSPP.** Update `package.json`, its lock, `scripts/vendor-emulatorjs.mjs`, `emulator-licenses/` and `licenses.json`. Add `play-psp.html`, extract shared styles from `play.html` to `play.css`, and scope the headers in `SecurityHeaders.cs`. Tests: `vendorEmulatorjs.test.ts` verifies the three vendor outputs, required-thread override and inventory. `SecurityHeadersTests.cs` checks top-level isolation/CSP and ordinary routes. Done when a clean build serves the pinned payload and complete notices locally and only the PSP document opts into isolation.
- [ ] **4. Play PSP from the library.** Enable the handler and list/detail eligibility in the mapped Sony, API and web files. Implement B3 in `params.js` and `play.js`. Tests: `PlayStationPortableHandlerTests.cs`, `LibraryEndpointsTests.cs`, `emulator.test.ts`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, `GameResults.test.ts`, and `game-page.test.ts` prove eligibility, link targets, failure before ISO fetch on an incompatible browser, statuses, and threaded startup. In `tests/e2e/specs/mvp.spec.ts` use the seeded Pocket Ninja ISO to verify new-tab isolation, both launch paths and the real core and asset requests. Done when the scoped player flow and the PSP boot check in Risks pass.
- [ ] **5. Play Genesis and verify Master System.** Enable the Sega handler and implement B4 client handling and B5 in `play.js`, `params.js`, and the guarded vendor patch. Tests: `MegaDriveHandlerTests.cs`, `LibraryEndpointsTests.cs`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, and `vendorEmulatorjs.test.ts` cover eligibility, preflight stops, asset mappings, control scheme and saved-core pinning. In `tests/e2e/specs/mvp.spec.ts`, check the Genesis setup message and start the seeded Harbor Patrol SMS ROM with the real bundled core and assert that the emulator reaches a running canvas. The fixture verifies emulator initialization. Done when the Genesis boot check in Risks proves all three cartridge extensions and SMS plays with its existing core.
- [ ] **6. Document setup and limits.** Update the files in the Documentation row. Tests: check documented paths, flags, filenames, statuses, formats and links against the shipped behavior. Done when the README supported-systems table includes PSP, Genesis and current SMS behavior, and operators can set up Genesis and diagnose PSP startup from the documentation. Explain that local PSP development uses the built API host on loopback, since the standalone Vite server does not serve the isolation headers.

## Decisions

- 2026-11-13 Master System core → retain the existing SMS Plus GX integration and add real browser coverage (code).
- 2026-11-13 Genesis core delivery → operator-supplied Genesis Plus GX (user).
- 2026-11-13 PSP launch → isolated top-level tab with the authenticated disc path and a 2 GiB ISO limit (design).

## Kickoff prompt

```text
$spec implement 2026-11-13-psp-genesis-browser-play.md
```

Starts the spec, or resumes it at the first unticked step.
