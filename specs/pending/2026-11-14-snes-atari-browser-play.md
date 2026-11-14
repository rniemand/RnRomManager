---
title: SNES and Atari browser play
status: ready
created: 2026-11-14
---

# SNES and Atari browser play

## Summary

Let signed-in library users play Super Nintendo cartridge releases through the existing browser player. Keep the existing Atari Jaguar, Lynx and 7800 launch paths covered and list all four cores in the README.

## Out of scope

- EmulatorJS upgrades, extra console peripherals, specialty SNES firmware, and ROM format or storage changes.

## Behaviour

- **B1.** Game list and detail responses advertise `snes9x` for `snes`. The existing Play actions open the same-origin modal for an embedded `.sfc` or `.smc` release, including releases already in the library. The player requests the selected ROM through the existing download endpoint and keeps the release name for saves. Standard cartridges need no BIOS request.
- **B2.** Administrators supply the EmulatorJS 4.2.3 Snes9x files through `Emulator:Snes9xCorePath` (environment variable `Emulator__Snes9xCorePath`). The option defaults to empty, uses existing operator-core path validation, and appears in effective settings as `emulator.snes9xCorePath`. The directory may be shared with the other operator cores. Use the existing authenticated `HEAD /api/emulator/cores/snes9x` and `GET /api/emulator/cores/snes9x/{name}` routes. The manifest allows only `snes9x-wasm.data`, `snes9x-legacy-wasm.data` and `snes9x.json`, with exact lengths and SHA-256 hashes from [`@emulatorjs/core-snes9x@4.2.3`](https://registry.npmjs.org/@emulatorjs/core-snes9x/-/core-snes9x-4.2.3.tgz), using its `reports/snes9x.json` copy. Use the existing verifier: HEAD returns a bodyless 204 when all three files match, and GET returns 200 with the requested matching file. Keep the existing 401 for unauthenticated requests, 404 for missing configuration or files, 409 for mismatches (`Snes9xCoreMismatch` in the GET problem body), and 500 for unreadable files.
- **B3.** The modal accepts `snes9x` and checks the operator endpoint before fetching the ROM. A 404 gives the Snes9x setup message with the option name, 409 asks for the matching 4.2.3 files, 401 shows session expiry, and other responses or network errors use the existing operator-core error pattern. Failure stops launch before the ROM or loader is requested. Success sets same-origin `EJS_paths` for the pinned files, uses EmulatorJS's SNES controls, and makes the existing core pin ignore a saved alternative selection and hide the chooser. Include Snes9x in the vendor guards that force single-threaded payload selection and report a local error if a payload fetch fails.
- **B4.** Existing Atari mappings stay available for their supported cartridge releases: `jaguar → virtualjaguar`, `lynx → handy`, and `a7800 → prosystem`. Keep the current bundled assets and cartridge launch paths. Exercise the launch path for one existing synthetic seed per Atari system in Playwright with an empty BIOS directory.
- **B5.** The README's supported systems table matches the handlers for all four consoles. The SNES feature overview and operator documentation explain setup, including exact file names, byte counts, hashes and a read-only Docker mount example for an operator-controlled directory readable by the app UID 1654. Show copying the report from the package's `reports/` directory to the root of the mounted directory.

## Changes

| Area | Files | Change |
|---|---|---|
| Core and API | `src/RnRomManager.Core/Configuration/EmulatorOptions.cs`, `EmulatorOptionsValidator.cs`, `src/RnRomManager.Api/Endpoints/OperatorCoreManifest.cs`, `EmulatorEndpoints.cs` | Register and validate the option and pin the three files with the existing verifier. |
| SNES | `src/RnRomManager.Systems.Snes/SnesHandler.cs` | Expose `snes9x` through the existing metadata contract. |
| Web | `src/RnRomManager.Web/static/emulator/params.js`, `src/RnRomManager.Web/scripts/vendor-emulatorjs.mjs` | Register Snes9x with the modal and the existing vendor script's core pin. Keep the operator-owned payloads outside the npm lockfile, vendored files and image. |
| Docs | `README.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/development.md`, `docs/features.md` | Explain support and setup at the points where users or developers need it. |

The change adds one optional configuration key and uses the existing API response fields and routes. Existing ZIPs and database records need no migration.

## Risks

- Snes9x has a noncommercial license. Use the existing operator-supplied core flow. [Snes9x license](https://docs.libretro.com/library/snes9x/).
- The modern and legacy payloads must match the pinned loader. Take the two payloads and the report from the same build and verify both asset-selection paths. [EmulatorJS 4.2.3 core files](https://cdn.emulatorjs.org/4.2.3/data/cores/).

## Steps

- [ ] **1. Serve the operator-supplied Snes9x files.** Implement B2 in the Core and API files above. Extend `tests/RnRomManager.Core.Tests/Configuration/OptionsValidatorTests.cs`, `tests/RnRomManager.Api.Tests/Configuration/{OptionsRegistrationTests,EffectiveSettingsTests}.cs`, and `tests/RnRomManager.Api.Tests/Integration/EmulatorEndpointsTests.OperatorCores.cs` (and its companion `EmulatorEndpointsTests.cs` fixture). Tests cover option defaults, validation, configuration binding, effective settings, authorized retrieval and missing or mismatched payloads using the existing synthetic manifest fixture. Done when the Core and API tests pass and the production pins match the official files.
- [ ] **2. Enable SNES launches and verify the four systems.** Implement B1, B3 and B5 in the SNES, Web and Docs files above. Extend `tests/RnRomManager.Systems.Snes.Tests/SnesHandlerTests.cs` and `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.cs` for the mapping and existing download path. Run `tests/RnRomManager.Api.Tests/SupportedSystemsTableTests.cs` against the updated README. Extend `src/RnRomManager.Web/src/lib/{emulatorParams,emulatorPlay,vendorEmulatorjs}.test.ts` for the allowlist, fail-before-ROM behavior, local paths and the three vendor guards (explicit pin, non-threaded selection, and local payload error). Replace the existing invalid-core `snes9x` fixture with an unsupported value. Extend the operator-core Playwright coverage in `tests/e2e/specs/mvp.spec.ts` for an SNES launch and missing configuration with a stubbed core. Add Playwright cases for the seeded Canyon Glide (7800), Cobalt Drift (Jaguar) and Prism Racer (Lynx) releases. Assert the modal selects the expected core and requests the release without a BIOS request (B4). Done when targeted suites pass, vendor tests cover modern and legacy local paths with thread and CDN fallback disabled for Snes9x, and SNES and Atari launch routing passes Playwright. If authorized playable ROMs are available, smoke-test gameplay and input for all four systems and record which systems were exercised.

## Decisions

- 2026-11-14 The three Atari cores are already registered, bundled and listed in the README. Retain their implementations and verify their launch behavior (code).
- 2026-11-14 Use the pinned EmulatorJS Snes9x build and the existing operator-supplied core flow, as used for PicoDrive and Genesis Plus GX (code and upstream license).

## Kickoff prompt

```text
$spec implement 2026-11-14-snes-atari-browser-play.md
```

Starts the spec or resumes it at the first unticked step.
