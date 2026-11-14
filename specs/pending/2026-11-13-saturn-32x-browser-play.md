---
title: Saturn and 32X browser play
status: ready
created: 2026-11-13
---

# Saturn and 32X browser play

## Summary
Saturn and 32X releases get a **Play** button. Saturn runs on EmulatorJS's Yabause core, which is bundled in the image, and plays bin/cue discs through the existing play-disc endpoint. 32X runs on EmulatorJS's PicoDrive core. PicoDrive's licence forbids commercial use, so operators supply its files the same way they do for Genesis Plus GX. The Genesis-only plumbing becomes per-core to support a second operator-supplied core. Mega Drive / Genesis already plays (Genesis Plus GX) and doesn't change.

## Out of scope
- Any change to Mega Drive / Genesis play (it keeps Genesis Plus GX, with no PicoDrive fallback).
- Sega CD.
- Playing Saturn `.nrg` releases (Yabause can't load Nero images).
- Saturn multi-disc swapping within one session: one disc per launch, as for PlayStation.
- Bundling PicoDrive or Genesis Plus GX.
- Real-game boot checks before the PR: the maintainer does them after merge, as for the previous browser-play specs.

## Behaviour
- **B1 Operator-supplied cores.** The server knows a fixed set of operator-supplied cores: `genesis_plus_gx` and `picodrive`. Each core has:
  - an option: `Emulator:GenesisCorePath` (unchanged), or the new `Emulator:PicodriveCorePath` (Docker `Emulator__PicodriveCorePath`);
  - a pinned manifest of the EmulatorJS 4.2.3 files `<core>-wasm.data`, `<core>-legacy-wasm.data` and `<core>.json`, with exact lengths and SHA-256.

  Validation is the same as for Genesis: a blank path disables the core, a relative path fails startup, and a missing folder makes the core unavailable but isn't fatal.
- **B2 Core routes.**
  - `HEAD /api/emulator/cores/{core}` is the availability check:
    - 204 when every file matches;
    - 404 when the core isn't configured or a file is missing;
    - bodyless 409 when a length or hash doesn't match;
    - 500 on an I/O error.

    Across the files, the priority is 500, then 409, then 404, as today.
  - `GET /api/emulator/cores/{core}/{name}` serves one manifest file after the same length and hash check:
    - a mismatch returns 409 ProblemDetails with code `GenesisCoreMismatch` or `PicodriveCoreMismatch`;
    - an unknown core returns 404;
    - a name that doesn't exactly match (case-sensitive) a name in that core's manifest returns 404. Traversal attempts such as `..%2F` fall under this rule.
  - `HEAD /api/emulator/genesis` and `GET /api/emulator/genesis/{name}` keep working unchanged, as aliases of the Genesis core routes.
  - Access is the same as for the existing Genesis routes. Both cores share one verify/read routine.
- **B3 32X play.** The `s32x` handler declares `BrowserCore => "picodrive"`. The player:
  1. runs the B2 HEAD check for `picodrive`. When the core is unavailable, it shows a PicoDrive-specific message that links to the configuration docs, the same way it does for Genesis;
  2. otherwise sets `EJS_paths` to the `/api/emulator/cores/picodrive/<file>` URLs and loads the ROM from `/download`, because the ROM is in the ZIP.

  The core is pinned, the core chooser is hidden, the control scheme is `sega32x`, and the CDN fallback is blocked. The core is expected to run non-threaded; see B9.
- **B4 Saturn core.** EmulatorJS's `@emulatorjs/core-yabause` 4.2.3 (GPL-2.0) is vendored like the other bundled cores, with its licence and NOTICE in `emulator-licenses/`. The vendored `emulator.js` keeps `yabause` as an explicit core even when another core was saved in settings. See B9 for threading.
- **B5 Saturn disc.** `GET /api/games/{id}/roms/{romId}/play-disc` accepts `saturn` alongside `psx` and `psp`. Limits come from a per-system table: `psx` and `saturn` 1 GiB, `psp` 2 GiB, all inclusive; above the cap the endpoint returns 409 `PlayDiscTooLarge`.

  The accepted shapes for Saturn match PlayStation:
  - a single `.bin`;
  - exactly one `.cue` with up to 99 bins, returned as a stored ZIP with the cue rewritten.

  A rom whose files include any `.nrg` (case-insensitive) returns 409 `InvalidPlayDisc`. A lone `.nrg` already fails `ShapeProblem`; a test pins this. A Saturn `.iso` isn't a stored extension, so it never occurs.
- **B6 Saturn play.** The `saturn` handler declares `BrowserCore => "yabause"`, and `yabause` becomes an external browser core in the API (`LibraryEndpoints.ExternalBrowserCores`) and in the SPA (`externalBrowserCores` in `emulator.ts`). A Saturn release whose primary rom's `ExternalFileName` ends in `.nrg` (case-insensitive):
  - gets `playable: false` in the list;
  - gets `canPlay(...) === false` in the SPA. `canPlay` already receives `externalFileName`, so its signature doesn't change and no Svelte file changes.

  The player loads the disc from play-disc. The `discError` wording names the right system, and the 1 GiB cap for Saturn. The core is pinned, the chooser is hidden, and the control scheme is `segaSaturn`.
- **B7 Saturn BIOS.** Saturn uses the PlayStation BIOS path, generalised: `loadPsxBios` becomes `loadOptionalBios(name, maxBytes)`, used by both systems, and `optionalBiosError` is reused unchanged. The player tries `/api/emulator/bios/saturn_bios.bin`:
  - when the file is present, it is passed as `EJS_externalFiles['/saturn_bios.bin']`;
  - on a 404, the player falls back to Yabause's HLE BIOS;
  - a file over 512 KiB, or any other error, is shown to the user, exactly as an oversized or failing `scph5501.bin` is today.

  `saturn_bios.bin` passes the existing BIOS name check (`IsBiosName`).
- **B8 Mega Drive.** No behaviour change. The SPA's Genesis code moves onto the generic per-core helpers (B2), and every existing Genesis test still passes; only URLs or helper names are updated where the code moved.
- **B9 Threading.** Both new cores are expected to run non-threaded on the normal `play.html`. Steps 2 and 3 check this from the package's files and `emulator.js`. If either core needs threads, it would need the isolated PSP-style tab: stop and ask the user, then record the answer in § Decisions, in the same commit as the code. Either way, each step records its finding in § Decisions.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Config | `src/RnRomManager.Core/Configuration/EmulatorOptions.cs`, `src/RnRomManager.Core/Configuration/EmulatorOptionsValidator.cs`, `src/RnRomManager.Api/Configuration/EffectiveSettings.cs` | New `PicodriveCorePath`, validated like `GenesisCorePath`. The effective-settings JSON gains `"picodriveCorePath"`. New config key `Emulator:PicodriveCorePath`, which defaults to blank (disabled). No data migration. |
| Api | `src/RnRomManager.Api/Endpoints/GenesisCoreManifest.cs` → `OperatorCoreManifest.cs`, `src/RnRomManager.Api/Endpoints/EmulatorEndpoints.cs` | Per-core manifest: core id → files, option accessor and problem code. A code comment in the manifest records the source package (`@emulatorjs/core-picodrive@4.2.3` tarball URL). Generic `/api/emulator/cores/{core}` routes; the Genesis routes stay as aliases; one shared verifier. |
| Systems | `src/RnRomManager.Systems.Sega/Sega32xHandler.cs`, `src/RnRomManager.Systems.Sega/SaturnHandler.cs` | `BrowserCore` set to `picodrive` / `yabause`. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs`, `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | The gate becomes a per-system cap table that includes `saturn`. `.nrg` → `InvalidPlayDisc`. `yabause` is added to the external cores. `playable` is false for an `.nrg` primary. |
| Web / vendoring | `src/RnRomManager.Web/package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `src/RnRomManager.Web/emulator-licenses/`, `src/RnRomManager.Api/licenses.json` | Adds `@emulatorjs/core-yabause@4.2.3` and puts `yabause` in the vendor `cores` list. PicoDrive's core files stay out of `package.json`, the lockfile and the `cores` list; only the guarded `emulator.js` edits name it. Explicit-core patch (the `getCore()` edit): adds `picodrive` and `yabause`. Non-threaded guard and no-CDN guard (the two `genesis_plus_gx` edits): adds `picodrive`, and adds `yabause` to the non-threaded guard if B9 holds. Yabause licence/NOTICE and a `licenses.json` entry. |
| Web | `src/RnRomManager.Web/static/emulator/params.js`, `static/emulator/play.js`, `src/RnRomManager.Web/src/lib/emulator.ts` | Allowlist gains `picodrive` and `yabause`. Generic `operatorCorePaths(core)`, `operatorCoreCheckUrl(core)` and `operatorCoreError(core, status)`, which Genesis now uses too. Control schemes `sega32x` and `segaSaturn`. `loadOptionalBios`. Saturn disc load. `canPlay` handles Saturn and `.nrg`. |
| Docs | `README.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md` | Supported-systems table rows: 32X → `picodrive`, Saturn → `yabause`; Mega Drive is unchanged. Also updates the Play bullet, the attribution, the PicoDrive core section, `Emulator:PicodriveCorePath`, the `/api/emulator/cores` routes and the Saturn BIOS. |

API contract: the new routes are additive, and the existing Genesis routes and `GenesisCoreMismatch` are unchanged. The `playable` flag and the `browserCore` value change only for `s32x` and `saturn` games.

## Risks
- **Licences.** PicoDrive isn't distributed, so it stays out of the image and the npm lockfile, like Genesis Plus GX. Yabause (GPL-2.0) is distributed, so its NOTICE lists its source commit and the RetroArch commit it links, matching the other bundled cores.
- **Pinned hashes.** The PicoDrive manifest values come from `@emulatorjs/core-picodrive@4.2.3`. Step 1 fetches it once with `npm pack @emulatorjs/core-picodrive@4.2.3` into a temp folder outside the repo, computes the lengths and SHA-256, and writes them into the manifest and its test. Nothing from the package is committed, and the tarball URL goes into a manifest comment and § Decisions. Operators whose files differ get a clear 409.
- **Saturn performance.** Yabause in WASM is slow on weak devices. This is documented, not solved.
- **Threading unknown.** See B9; implementation stops and asks if a core needs threads.
- **Refactoring the Genesis plumbing** could break the shipped Genesis feature. To guard against that, the old routes stay as aliases and the existing Genesis tests keep passing.
- **Green commits.** `SupportedSystemsTableTests` compares the README table with each handler's `BrowserCore`, so each step that changes a `BrowserCore` updates that README row in the same commit.

## Steps
- [ ] **1. Per-core operator-supplied core API** (B1, B2): adds the `PicodriveCorePath` option, its validation and the effective-settings field; `OperatorCoreManifest` with the Genesis entry and the pinned PicoDrive entry; the `/api/emulator/cores/{core}` HEAD and GET routes; and the Genesis aliases, all on the shared verifier.
  - Files: `EmulatorOptions.cs`, `EmulatorOptionsValidator.cs`, `src/RnRomManager.Api/Configuration/EffectiveSettings.cs`, `OperatorCoreManifest.cs`, `EmulatorEndpoints.cs`.
  - Tests:
    - `OptionsValidatorTests` (`PicodriveCorePath_EmptyOrAbsolute`), `OptionsRegistrationTests`, `EffectiveSettingsTests`.
    - `EmulatorEndpointsTests`: the existing Genesis cases, plus a new `EmulatorEndpointsTests.OperatorCores.cs` covering:
      - per-core 204, 404, 409 and 500 (500 forced the same way the existing Genesis I/O-error test forces it);
      - the 500 > 409 > 404 priority across mixed files;
      - unknown core → 404; unknown, case-variant or traversal name → 404;
      - the `PicodriveCoreMismatch` body;
      - Genesis alias parity.
    - A manifest test that pins the PicoDrive names, lengths and hashes.
  - Done when:
    - the listed tests pass;
    - `/api/emulator/genesis` behaves as before;
    - the manifest comment and a § Decisions line record the PicoDrive tarball URL.
- [ ] **2. 32X play** (B3, B8, B9):
  - `Sega32xHandler.BrowserCore = "picodrive"`, and the README table's 32X row → `picodrive`.
  - Vendor script: adds `picodrive` to the explicit-core patch, the non-threaded guard and the no-CDN guard, after confirming B9 from the package and `emulator.js`. The finding goes into § Decisions.
  - `params.js`: the allowlist entry, the generic operator-core helpers (with Genesis moved onto them), the `sega32x` scheme and the hidden chooser.
  - `play.js`: a 32X path that runs the HEAD check, then loads with `EJS_paths`.
  - Files: `Sega32xHandler.cs`, `README.md` (table row only), `scripts/vendor-emulatorjs.mjs`, `params.js`, `play.js`.
  - Tests:
    - `Sega32xHandlerTests`, `MegaDriveHandlerTests`, `SupportedSystemsTableTests`.
    - `vendorEmulatorjs.test.ts`.
    - `emulatorParams.test.ts`: a new `describe('32X (B3)')`; the Genesis suite still passes.
    - `emulatorPlay.test.ts`: a new `play page 32X`; `play page Genesis and Master System` still passes.
    - `emulator.test.ts`.
  - Done when:
    - `npm run lint`, `npm run check` and those tests pass;
    - a 32X game shows Play;
    - the player requests `/api/emulator/cores/picodrive` and the three file URLs;
    - a missing core shows the PicoDrive message;
    - Genesis behaves as before.
- [ ] **3. Vendor Yabause** (B4, B9):
  - Adds `@emulatorjs/core-yabause@4.2.3` to `package.json` and the lockfile, and `yabause` to the vendor `cores` list.
  - Adds `yabause` to the explicit-core patch, and to the non-threaded guard if B9 holds (stop and ask if it doesn't). The finding goes into § Decisions.
  - Adds the Yabause licence and NOTICE to `emulator-licenses/`, and an entry to `src/RnRomManager.Api/licenses.json`.
  - Nothing selects `yabause` yet, so behaviour is unchanged.
  - Files: `package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `src/RnRomManager.Web/emulator-licenses/`, `src/RnRomManager.Api/licenses.json`.
  - Tests: `vendorEmulatorjs.test.ts`, `LicenseInfoTests`.
  - Done when:
    - `node scripts/vendor-emulatorjs.mjs` copies the three Yabause files and its licences;
    - `npm run lint`, `npm run check` and the listed tests pass.
- [ ] **4. Saturn play-disc** (B5): `PlayDisc` takes the per-system cap table (`psx` and `saturn` 1 GiB, `psp` 2 GiB), and any `.nrg` in the set → 409 `InvalidPlayDisc`. `BrowserCore` isn't changed yet, so no Play button appears.
  - Files: `PlayDisc.cs`.
  - Tests:
    - `LibraryEndpointsTests.PlayDisc`:
      - Saturn single bin; Saturn cue set;
      - Saturn over 1 GiB → 409; Saturn exactly 1 GiB → 200;
      - lone `.nrg` → 409 `InvalidPlayDisc`; cue set with an `.nrg` → 409 `InvalidPlayDisc`;
      - psx and psp unchanged.
    - `PlayDiscCopyTests`.

    These integration tests need Docker.
  - Done when those tests pass and PlayStation and PSP behaviour is unchanged.
- [ ] **5. Saturn play** (B6, B7):
  - `SaturnHandler.BrowserCore = "yabause"`, and the README table's Saturn row → `yabause`.
  - `LibraryEndpoints`: `yabause` in `ExternalBrowserCores`; `playable` false for an `.nrg` primary.
  - `params.js`: the `yabause` allowlist entry, the `segaSaturn` scheme, the hidden chooser, `saturnBios = 'saturn_bios.bin'` with a 512 KiB bound, and system-aware `discError` wording.
  - `play.js`: `loadOptionalBios`, a Saturn disc path, and the optional BIOS through `EJS_externalFiles`.
  - `emulator.ts`: `yabause` in the external cores, and `canPlay` false for `.nrg`. The existing "Saturn doesn't play" case is replaced.
  - Files: `SaturnHandler.cs`, `LibraryEndpoints.cs`, `README.md` (table row only), `params.js`, `play.js`, `src/lib/emulator.ts`.
  - Tests:
    - `SaturnHandlerTests`, `PlayStationHandlerTests`, `SupportedSystemsTableTests`.
    - `LibraryEndpointsTests`: Saturn bin/cue `playable` true, `.nrg` false, `browserCore` `yabause`.
    - `EmulatorEndpointsTests`: `saturn_bios.bin` is served from `Paths:Bios`.
    - `emulatorParams.test.ts`: a new `describe('Saturn (B6, B7)')`.
    - `emulatorPlay.test.ts`:
      - a new `play page Saturn` covering the disc URL, BIOS present, BIOS 404 → HLE, BIOS over 512 KiB → error, other BIOS error → shown;
      - `play page PlayStation` still passes.
    - `emulator.test.ts`.
  - Done when:
    - `npm run lint`, `npm run check` and those tests pass;
    - a Saturn bin/cue game shows Play and an `.nrg` one doesn't.
- [ ] **6. Docs**:
  - `README.md`:
    - the Play bullet gains Saturn (one disc per launch, up to 1 GiB, bin/cue only, optional `saturn_bios.bin`) and 32X (needs the PicoDrive files you supply);
    - the attribution gains Yabause with its source commit and licence, and a line saying PicoDrive isn't included.
  - `docs/configuration.md`: the `Emulator:PicodriveCorePath` row and a "PicoDrive core" section.
  - `docs/deployment.md`: the PicoDrive mount.
  - `docs/features.md`: the Saturn and 32X play rules, the `/api/emulator/cores` routes and the Saturn BIOS.
  - `docs/development.md`: the explicit-core patch list and the non-threaded and no-CDN guards.
  - `docs/adding-a-system.md`: how to add an operator-supplied core.
  - Tests: `SupportedSystemsTableTests`.
  - Done when:
    - every file listed above has the stated content;
    - the README table shows 32X `picodrive`, Saturn `yabause` and Mega Drive `genesis_plus_gx`;
    - `SupportedSystemsTableTests` passes.

## Decisions
- 2026-11-13 Mega Drive already plays on Genesis Plus GX; what should change? → Nothing; no PicoDrive fallback (user)
- 2026-11-13 How is a second operator-supplied core added? → Generalise to per-core config, manifest and `/api/emulator/cores/{core}` routes, keeping the Genesis routes as aliases (user)
- 2026-11-13 PicoDrive licence → non-commercial, so it's operator-supplied and never bundled, like Genesis Plus GX (user)
- 2026-11-13 Saturn BIOS → optional `saturn_bios.bin` with an HLE fallback, like PlayStation (user)
- 2026-11-13 Saturn `.nrg` → no Play button; play-disc answers 409 `InvalidPlayDisc` (user)
- 2026-11-13 Saturn disc cap and shape → 1 GiB, one disc per launch, the same shapes as PlayStation (code: matches `SaturnHandler.MaxRomSizeBytes`)
- 2026-11-13 A core needs threads → stop and ask the user; never move it to the isolated tab silently (reviewer)
- 2026-11-13 Step order → each `BrowserCore` change lands with its README row and working player, so every commit is green and no dead Play button ships (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-13-saturn-32x-browser-play.md
```
Starts the spec, or resumes it at the first unticked step.
