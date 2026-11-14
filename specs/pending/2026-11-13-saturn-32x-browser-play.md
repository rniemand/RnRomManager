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
  - a display name for messages: "Genesis Plus GX" or "PicoDrive";
  - a pinned manifest of the EmulatorJS 4.2.3 files `<core>-wasm.data`, `<core>-legacy-wasm.data` and `<core>.json` (the package's `reports/` copy), with exact lengths and SHA-256. Genesis keeps today's values. PicoDrive's, from `@emulatorjs/core-picodrive@4.2.3` (checked at review):

    | File | Bytes | SHA-256 |
    |---|---|---|
    | `picodrive-wasm.data` | 1,034,483 | `bb5d50b8b88111b583977d2f7a16d01a822b3deda9205048d99ecaad2c56d861` |
    | `picodrive-legacy-wasm.data` | 1,035,609 | `cc42b0c95e9edfd0e5c689211e8e85e45f4525be2d814af8ca9331ef90e0620b` |
    | `picodrive.json` | 123 | `2b09f29e095a003c15abf8e5bdfb0eaca96d6c6121183b3bd92ed85e860918d3` |

  Validation is the same as for Genesis: a blank path disables the core, a relative or whitespace-only path fails startup, and a missing folder makes the core unavailable but isn't fatal. Like `GenesisCorePath`, the new path isn't created, probed or one of the paths that must differ; it may be the same folder as `GenesisCorePath`, since the file names don't overlap. The manifest is registered as a singleton that tests replace with a synthetic one, as `GenesisCoreManifest` is today.
- **B2 Core routes.** `{core}` and `{name}` match ordinally (case-sensitive).
  - `HEAD /api/emulator/cores/{core}` is the bodyless availability check:
    - 204 when every file matches;
    - 404 when the core is unknown, isn't configured, or a file is missing, empty or not a regular file;
    - 409 when a length or hash doesn't match;
    - 500 on an I/O error.

    Across the files, the priority is 500, then 409, then 404, as today.
  - `GET /api/emulator/cores/{core}/{name}` serves one manifest file, with the verified in-memory bytes and `Cache-Control: no-store`, after the same length and hash check:
    - a mismatch returns 409 ProblemDetails with code `GenesisCoreMismatch` or `PicodriveCoreMismatch`, titled "The <display name> core file doesn't match EmulatorJS 4.2.3" (so Genesis's title now reads "Genesis Plus GX" where it read "Genesis"; the code is unchanged);
    - an I/O error returns 500 `IoError`, titled "The <display name> core file can't be read";
    - an unknown or unconfigured core returns 404;
    - a name that doesn't exactly match a name in that core's manifest returns 404. Traversal attempts such as `..%2F` fall under this rule.
  - `HEAD /api/emulator/genesis` and `GET /api/emulator/genesis/{name}` keep working unchanged, as aliases of the `genesis_plus_gx` routes.
  - Access is the same as for the existing Genesis routes (any signed-in user; 401 anonymous, 403 during a forced password change; nothing audited). Both cores share one verify/read routine.
- **B3 32X play.** The `s32x` handler declares `BrowserCore => "picodrive"`. The player:
  1. runs the B2 HEAD check for `picodrive`. When the core is unavailable, it shows `operatorCoreError`'s message and stops before the ROM or loader, as Genesis does: 404 → "32X games need the PicoDrive core files: ask an Admin to set them up (Emulator:PicodriveCorePath).", 409 → "The PicoDrive core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:PicodriveCorePath).", 401 → the session-expiry message, anything else → "Couldn't load the PicoDrive core." (with ` (HTTP <status>)` when there is a status);
  2. otherwise sets `EJS_paths` to the `/api/emulator/cores/picodrive/<file>` URLs and loads the ROM from `/download`, because the ROM is in the ZIP.

  The core is pinned, the core chooser is hidden, the control scheme is `sega32x` (EmulatorJS would otherwise take `segaMS`, PicoDrive's first group), and the CDN fallback is blocked. `EJS_disableCue` stays unset: upstream 4.2.3 already skips cue generation for `picodrive`, as for Genesis Plus GX. The core runs non-threaded; see B9.

  `operatorCoreError(core, status)` keeps each core's wording in a per-core table: the system label of the 404 message ("Genesis games" / "32X games"), the core's display name ("Genesis Plus GX" / "PicoDrive"), the short name of the fallback message ("Genesis" / "PicoDrive", so Genesis still says "Couldn't load the Genesis core.") and the option key. Genesis's messages stay byte-identical.
- **B4 Saturn core.** EmulatorJS's `@emulatorjs/core-yabause` 4.2.3 is vendored like the other bundled cores (its `-wasm` and `-legacy-wasm` payloads and report, never the `-thread` ones). The package's `license` field is empty and its payloads hold no `license.txt`, so `emulator-licenses/` gets a reviewed `yabause-LICENSE.txt` (the source's `yabause/COPYING`, GPL v2, byte for byte) and a `yabause-NOTICE.txt` like `smsplus-NOTICE.txt`. Review resolved the facts it records:
  - Source: both binaries carry the version string `0.9.15 e24ebfc`, which is https://github.com/EmulatorJS/yabause/tree/e24ebfca357bf70732a77a58c14491f13209a1cd. The payloads' `core.json` gives the build entry (`buildpath` `./yabause/src/libretro`, `makescript` `Makefile`, no arguments), and both binaries carry RetroArch `6dd4353`, so `COMMON-BUILD.txt` applies.
  - Licence: GPL-2.0-or-later (the source headers say "either version 2 of the License, or (at your option) any later version").
  - Compiled-in components: `yabause/src/libretro/Makefile` defaults to `ENABLE_CHD = 1`, `ENABLE_ZLIB = 1` and `HAVE_MUSASHI = 1`, and `Makefile.common` then compiles libchdr, FLAC 1.3.3's decoder, the LZMA SDK 19.00 (`LzmaDec`, `LzmaEnc`, `LzFind`), zlib 1.2.11's inflate, JUnzip and the Musashi 68000 core. Step 5 re-checks each one's terms at that commit.

  As for `pcsx_rearmed`, a component whose licence must travel with binaries gets its text copied byte for byte as `yabause-<component>-LICENSE.txt` (for example `yabause-libchdr-LICENSE.txt` and `yabause-flac-LICENSE.txt`), and the NOTICE names every component and its terms. `COMMON-BUILD.txt` gains the package. The `licenses.json` and README licence expression starts from `GPL-2.0-or-later AND BSD-3-Clause AND Zlib, plus the public-domain LZMA SDK`, adjusted to what the NOTICE finds. The vendored `emulator.js` keeps `yabause` as an explicit core even when another core was saved in settings. See B9 for threading.
- **B5 Saturn disc.** `GET /api/games/{id}/roms/{romId}/play-disc` accepts `saturn` alongside `psx` and `psp`. Limits come from a per-system table: `psx` and `saturn` 1 GiB, `psp` 2 GiB, all inclusive; above the cap the endpoint returns 409 `PlayDiscTooLarge`.

  The accepted shapes for Saturn match PlayStation:
  - a single `.bin`;
  - exactly one `.cue` with up to 99 bins, the primary `.bin` among them, returned as a stored ZIP with the cue rewritten.

  A rom whose files include any `.nrg` returns 409 `InvalidPlayDisc`. `ShapeProblem` already does this for both shapes (a lone `.nrg` isn't a `.bin`/`.iso`; a set member that's neither the cue nor a `.bin` breaks the set rule), so there's no new check, only tests that pin it. A Saturn `.iso` isn't a stored extension, so it never occurs.
- **B6 Saturn play.** The `saturn` handler declares `BrowserCore => "yabause"`, and `yabause` becomes an external browser core in the API (`LibraryEndpoints.ExternalBrowserCores`) and in the SPA (`externalBrowserCores` in `emulator.ts`). A Saturn release whose primary rom is an `.nrg` (case-insensitive) doesn't play:
  - in the games list, `playRomId` and `playRomFileName` are null when the primary rom's file name (`PrimaryRomFileName`, which equals its external name for a FileSets system) ends in `.nrg`; no repository change;
  - in the SPA, `canPlay(...) === false` when the rom's `externalFileName` ends in `.nrg`. `canPlay` already receives `externalFileName`, so its signature doesn't change and no Svelte file changes.

  Both checks apply whatever the system; only Saturn stores `.nrg` today.

  The player loads the disc from play-disc. Yabause's `core.json` lists `cue` but not `bin`, so EmulatorJS 4.2.3 picks the cue from a set's ZIP, and for a lone `.bin` writes its own `MODE1/2352` cue: `EJS_disableCue` stays unset. `discError` doesn't change: its non-PSP wording names no system and already says 1 GiB. The core is pinned, the chooser is hidden, and the control scheme is `segaSaturn`.
- **B7 Saturn BIOS.** Saturn uses the PlayStation BIOS path, generalised: `loadPsxBios` becomes `loadOptionalBios(name, bytes, sizeError)`, used by both systems, and `optionalBiosError` is reused unchanged. The player tries only `/api/emulator/bios/saturn_bios.bin`. Yabause (`libretro.c` at `e24ebfc`) looks in its system directory for `saturn_bios.bin`, then `sega_101.bin`, then `mpr-17933.bin`, and falls back to HLE when none exists. EmulatorJS 4.2.3 sets no `system_directory`, so RetroArch uses the content directory `/`, where `EJS_externalFiles` writes the file, as for `scph5501.bin`:
  - when the file is present and exactly 512 KiB, it is passed as `EJS_externalFiles['/saturn_bios.bin']`;
  - on a 404, the player falls back to Yabause's HLE BIOS;
  - a file that isn't exactly 512 KiB shows `saturnBiosSizeError` ("The Saturn BIOS in the BIOS folder (Paths:Bios) must be the 512 KiB saturn_bios.bin: ask an Admin to replace it."), and any other error shows `optionalBiosError`'s message, before the disc is requested, exactly as for `scph5501.bin` today.

  `contentFileName` gives a standalone Saturn image named `saturn_bios.bin`, `sega_101.bin` or `mpr-17933.bin` (any case) the name `<game id>-<name>`, as it does for `scph5501.bin`. The firmware can't be overwritten, and Yabause never takes the disc for its BIOS. `saturn_bios.bin` passes the existing BIOS name check (`IsBiosName`).
- **B8 Mega Drive.** No behaviour change. The API's Genesis code moves onto the shared per-core manifest and verifier, and the SPA's Genesis code onto the generic per-core helpers, so the player now checks `/api/emulator/cores/genesis_plus_gx`. Every existing Genesis test (API, vitest and e2e) still passes; only type names, URLs or helper names are updated where the code moved.
- **B9 Threading.** Both new cores run non-threaded on the normal `play.html`. Review found that each package has non-threaded `-wasm` and `-legacy-wasm` payloads whose binaries reference neither `SharedArrayBuffer` nor `pthread_create`, and EmulatorJS 4.2.3's `requiresThreads` lists only `ppsspp` and `dosbox_pure`. Steps 4 and 5 re-check this against the vendored `emulator.js`. If either core turns out to need threads, it would need the isolated PSP-style tab: stop and ask the user, then record the answer in § Decisions, in the same commit as the code.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Config | `src/RnRomManager.Core/Configuration/EmulatorOptions.cs`, `src/RnRomManager.Core/Configuration/EmulatorOptionsValidator.cs` | New `PicodriveCorePath`, validated like `GenesisCorePath`. New config key `Emulator:PicodriveCorePath`, which defaults to blank (disabled). `EffectiveSettings` serialises the whole options object, so its JSON gains `"picodriveCorePath"` without a code change. No data migration. |
| Api | `src/RnRomManager.Api/Endpoints/GenesisCoreManifest.cs` → `OperatorCoreManifest.cs`, `src/RnRomManager.Api/Endpoints/EmulatorEndpoints.cs`, `src/RnRomManager.Api/AppHost.cs` | Per-core manifest: core id → display name, files, option accessor and problem code, registered as a singleton (`AppHost`). A code comment in the manifest records the source package (`@emulatorjs/core-picodrive@4.2.3` tarball URL). Generic `/api/emulator/cores/{core}` routes; the Genesis routes stay as aliases; one shared verifier. |
| Systems | `src/RnRomManager.Systems.Sega/Sega32xHandler.cs`, `src/RnRomManager.Systems.Sega/SaturnHandler.cs` | `BrowserCore` set to `picodrive` / `yabause`. |
| Api | `src/RnRomManager.Api/Endpoints/PlayDisc.cs`, `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | The gate becomes a per-system cap table that includes `saturn` (`.nrg` is already rejected by `ShapeProblem`). `yabause` is added to the external cores. `playRomId`/`playRomFileName` are null for an `.nrg` primary. |
| Web / vendoring | `src/RnRomManager.Web/package.json`, `src/RnRomManager.Web/package-lock.json`, `src/RnRomManager.Web/scripts/vendor-emulatorjs.mjs`, `src/RnRomManager.Web/emulator-licenses/` (`yabause-LICENSE.txt`, `yabause-NOTICE.txt`, the `yabause-<component>-LICENSE.txt` files B4 calls for, `COMMON-BUILD.txt`), `src/RnRomManager.Api/licenses.json` | Adds `@emulatorjs/core-yabause@4.2.3` and puts `yabause` in the vendor `cores` list. PicoDrive's core files stay out of `package.json`, the lockfile and the `cores` list; only the guarded `emulator.js` edits name it. Explicit-core patch (the `getCore()` edit): adds `picodrive` and `yabause`. Non-threaded guard and no-CDN guard (the two `genesis_plus_gx` edits): adds `picodrive`, and adds `yabause` to the non-threaded guard. Yabause licence/NOTICE and a `licenses.json` entry. |
| Web | `src/RnRomManager.Web/static/emulator/params.js`, `src/RnRomManager.Web/static/emulator/play.js`, `src/RnRomManager.Web/src/lib/emulator.ts` | Allowlist gains `picodrive` and `yabause`. Generic `operatorCorePaths(core)`, `operatorCoreCheckUrl(core)` and `operatorCoreError(core, status)`, which Genesis now uses too. Control schemes `sega32x` and `segaSaturn`. `loadOptionalBios`, `saturnBios`, `saturnBiosSizeError` and the `contentFileName` guard. Saturn disc load. `canPlay` handles Saturn and `.nrg`. |
| E2E | `tests/e2e/specs/mvp.spec.ts` | The Genesis test waits for `/api/emulator/cores/genesis_plus_gx`. New 32X missing-core and Saturn play-request cases. |
| Docs | `README.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md` | Supported-systems table rows: 32X → `picodrive`, Saturn → `yabause`; Mega Drive is unchanged. Also updates the Play bullet, the attribution, the PicoDrive core section and file table, `Emulator:PicodriveCorePath`, the `/api/emulator/cores` routes, the Saturn BIOS and the Saturn performance note. |

API contract: the new routes are additive, and the existing Genesis routes and `GenesisCoreMismatch` are unchanged. The `playRomId`/`playRomFileName` fields and the `browserCore` value change only for `s32x` and `saturn` games.

## Risks
- **Licences.** PicoDrive isn't distributed, so it stays out of the image and the npm lockfile, like Genesis Plus GX (its payload's `license.txt` forbids selling or commercial use). Yabause is distributed, but its package carries no licence, so Step 5 writes the licence and NOTICE from the source: source commit, the RetroArch commit it links (via `COMMON-BUILD.txt`), and the licences of compiled-in components, matching the other bundled cores. The package's own `dependencies` ask for `@emulatorjs/emulatorjs@latest`; the existing `overrides` entry pins it to the direct 4.2.3 dependency, and Step 5 checks the lockfile still holds only that version.
- **Operator-supplied code.** PicoDrive's payloads run JavaScript in the app's origin, like Genesis Plus GX's. Only files whose length and SHA-256 match B1 are served, and `docs/deployment.md` gives the same read-only mount and permissions advice as for Genesis.
- **Pinned hashes.** The PicoDrive values in B1 come from `https://registry.npmjs.org/@emulatorjs/core-picodrive/-/core-picodrive-4.2.3.tgz`. Step 2 re-checks them with `npm pack @emulatorjs/core-picodrive@4.2.3` into a temp folder outside the repo before writing them into the manifest and its test. Nothing from the package is committed. Operators whose files differ get a clear 409.
- **Saturn performance.** Yabause in WASM is slow on weak devices. This is documented in `docs/features.md`, not solved.
- **Image size.** Yabause adds about 2 MB of vendored payloads to the image.
- **Threading.** See B9; implementation stops and asks if a core needs threads.
- **Refactoring the Genesis plumbing** could break the shipped Genesis feature. To guard against that, the API refactor (Step 1) and the SPA refactor (Step 3) land as their own commits with no behaviour change, the old routes stay as aliases, and the existing Genesis tests keep passing.
- **Green commits.** `SupportedSystemsTableTests` compares the README table with each handler's `BrowserCore`, and `LibraryEndpointsTests.GameDetail_BrowserCore_IsTheSystemHandlers` pins each system's core (it has `saturn` → null today), so each step that changes a `BrowserCore` updates that README row and that test in the same commit.

## Steps
Steps 1, 2, 6 and the `LibraryEndpointsTests`/`EmulatorEndpointsTests` parts of 4 and 7 run integration tests (category `Integration`), which need Docker. Playwright (`mvp.spec.ts`) runs only in Finish.

- [ ] **1. Per-core operator-supplied core API, Genesis only** (B2, B8): `GenesisCoreManifest.cs` becomes `OperatorCoreManifest.cs` with only the Genesis entry; the `/api/emulator/cores/{core}` HEAD and GET routes; the Genesis aliases; all on the shared verifier. No behaviour change for Genesis.
  - Files: `OperatorCoreManifest.cs`, `EmulatorEndpoints.cs`, `AppHost.cs`, `tests/RnRomManager.Api.Tests/Integration/EmulatorEndpointsTests.cs`, `EmulatorEndpointsTests.Genesis.cs`, new `EmulatorEndpointsTests.OperatorCores.cs`.
  - Tests:
    - `EmulatorEndpointsTests`: the existing Genesis cases, moved to the new manifest type (`PinnedManifest_HoldsExactlyTheThree4_2_3Files` and `Genesis_ByDefault_ExposesNothing` follow the rename).
    - `EmulatorEndpointsTests.OperatorCores.cs` for `genesis_plus_gx` on the generic routes: 204, 404, 409 and 500 (500 forced as `Genesis_ALockedFile_Is500` does); the 500 > 409 > 404 priority across mixed files; unknown or case-variant core → 404 on HEAD and GET; unknown, case-variant or traversal name → 404; alias parity (same status and bytes from both routes); anonymous → 401 and a forced password change → 403 on both generic routes.
  - Done when:
    - the listed tests pass;
    - `/api/emulator/genesis` behaves as before.
- [ ] **2. PicoDrive core config and manifest** (B1, B2):
  - The `PicodriveCorePath` option and its validation; the pinned PicoDrive entry (re-checked from the package, see Risks) with the tarball URL in a code comment; `PicodriveCoreMismatch`.
  - Files: `EmulatorOptions.cs`, `EmulatorOptionsValidator.cs`, `OperatorCoreManifest.cs`, `EmulatorEndpointsTests.cs` (synthetic manifest and temp folder for both cores), `EmulatorEndpointsTests.OperatorCores.cs`, `OptionsValidatorTests.cs`, `OptionsRegistrationTests.cs`, `EffectiveSettingsTests.cs`.
  - Tests:
    - `OptionsValidatorTests` (`PicodriveCorePath_EmptyOrAbsolute`), `OptionsRegistrationTests` (default blank, missing absolute folder passes startup, relative path fails), `EffectiveSettingsTests` (`"picodriveCorePath"` in the JSON).
    - `EmulatorEndpointsTests.OperatorCores.cs`: `picodrive` 204, 404, 409 and 500; 404 on a host with no `PicodriveCorePath` and the pinned manifest, as `Genesis_ByDefault_ExposesNothing` does; the `PicodriveCoreMismatch` body; one core's files don't satisfy the other; a manifest test that pins the PicoDrive names, lengths and hashes of B1.
  - Done when:
    - the listed tests pass;
    - § Decisions records that the re-checked values match B1.
- [ ] **3. Generic operator-core helpers in the player** (B8): `params.js` gains `operatorCorePaths(core)`, `operatorCoreCheckUrl(core)` and `operatorCoreError(core, status)`; Genesis moves onto them (so it now checks `/api/emulator/cores/genesis_plus_gx` and loads its files from there) and the Genesis-only exports go. `play.js` uses the generic check. No other behaviour change.
  - Files: `params.js`, `play.js`, `emulatorParams.test.ts`, `emulatorPlay.test.ts`, `tests/e2e/specs/mvp.spec.ts` (the Genesis test's URL match only).
  - Tests: `emulatorParams.test.ts` (`Genesis (Genesis B4, B5)`) and `emulatorPlay.test.ts` (`play page Genesis and Master System (Genesis B4, B5)`) pass with only URLs and helper names updated, and their message texts unchanged.
  - Done when `npm run lint`, `npm run check` and those tests pass.
- [ ] **4. 32X play** (B3, B9):
  - `Sega32xHandler.BrowserCore = "picodrive"`, and the README table's 32X row → `picodrive`.
  - Vendor script: adds `picodrive` to the explicit-core patch, the non-threaded guard and the no-CDN guard, after confirming B9 against `emulator.js`. The finding goes into § Decisions.
  - `params.js`: the allowlist entry, PicoDrive in the operator-core helpers with the B3 messages, the `sega32x` scheme and the hidden chooser.
  - `play.js`: the 32X path runs the HEAD check, then loads with `EJS_paths`.
  - `mvp.spec.ts`: a 32X case like the Genesis one: Star Relay's Play shows the PicoDrive missing-core message (the e2e image has no `Emulator:PicodriveCorePath`), and no ROM or core file is requested after the HEAD.
  - Files: `Sega32xHandler.cs`, `README.md` (table row only), `scripts/vendor-emulatorjs.mjs`, `params.js`, `play.js`, and the tests below.
  - Tests:
    - `Sega32xHandlerTests` (`BrowserCore` is `picodrive`), `MegaDriveHandlerTests`, `SupportedSystemsTableTests`.
    - `LibraryEndpointsTests`: `GameDetail_BrowserCore_IsTheSystemHandlers` gains `s32x` → `picodrive`; a 32X game in the list has `playRomId`.
    - `vendorEmulatorjs.test.ts`.
    - `emulatorParams.test.ts`: a new `describe('32X (B3)')`; the Genesis suite still passes.
    - `emulatorPlay.test.ts`: a new `play page 32X`; `play page Genesis and Master System` still passes.
    - `mvp.spec.ts`: the 32X case (runs in Finish).
  - Done when:
    - `npm run lint`, `npm run check` and those tests (except Playwright) pass;
    - a 32X game shows Play;
    - the player requests `/api/emulator/cores/picodrive` and the three file URLs;
    - a missing core shows the PicoDrive message;
    - Genesis behaves as before.
- [ ] **5. Vendor Yabause** (B4, B9):
  - Adds `@emulatorjs/core-yabause@4.2.3` (exact version) to `package.json` and the lockfile, and `yabause` to the vendor `cores` list.
  - Adds `yabause` to the explicit-core patch and to the non-threaded guard, after confirming B9 (stop and ask if it fails). The finding goes into § Decisions.
  - Adds `yabause-LICENSE.txt`, `yabause-NOTICE.txt` and the `yabause-<component>-LICENSE.txt` files (B4) to `emulator-licenses/`, the package to `COMMON-BUILD.txt`, and an entry to `src/RnRomManager.Api/licenses.json`.
  - Nothing selects `yabause` yet, so behaviour is unchanged.
  - Files: `package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `emulator-licenses/` (the files above), `src/RnRomManager.Api/licenses.json`. Verbatim third-party licence texts don't count toward the step's line budget.
  - Tests: `vendorEmulatorjs.test.ts` (its core list and its `emulator-licenses/` file list gain the new files), `LicenseInfoTests`.
  - Done when:
    - `node scripts/vendor-emulatorjs.mjs` (run in `src/RnRomManager.Web`) copies the three Yabause files and its licences, and no `yabause-thread` file;
    - `package-lock.json` resolves `@emulatorjs/emulatorjs` only at 4.2.3;
    - `npm run lint`, `npm run check` and the listed tests pass.
- [ ] **6. Saturn play-disc** (B5): `PlayDisc` takes the per-system cap table (`psx` and `saturn` 1 GiB, `psp` 2 GiB). `BrowserCore` isn't changed yet, so no Play button appears.
  - Files: `PlayDisc.cs`, `LibraryEndpointsTests.PlayDisc.cs`.
  - Tests:
    - `LibraryEndpointsTests.PlayDisc`:
      - Saturn single bin; Saturn cue set;
      - Saturn over 1 GiB → 409; Saturn exactly 1 GiB → 200;
      - lone `.nrg` → 409 `InvalidPlayDisc`; cue set with an `.nrg` → 409 `InvalidPlayDisc`;
      - psx and psp unchanged.
    - `PlayDiscCopyTests`.
  - Done when those tests pass and PlayStation and PSP behaviour is unchanged.
- [ ] **7. Saturn play** (B6, B7):
  - `SaturnHandler.BrowserCore = "yabause"`, and the README table's Saturn row → `yabause`.
  - `LibraryEndpoints`: `yabause` in `ExternalBrowserCores`; `playRomId`/`playRomFileName` null for an `.nrg` primary.
  - `params.js`: the `yabause` allowlist entry, the `segaSaturn` scheme, the hidden chooser, `saturnBios = 'saturn_bios.bin'`, `saturnBiosSizeError` and the `contentFileName` guard.
  - `play.js`: `loadOptionalBios` (PlayStation moved onto it), a Saturn disc path, and the optional BIOS through `EJS_externalFiles`.
  - `emulator.ts`: `yabause` in the external cores, and `canPlay` false for `.nrg`. The existing "Saturn doesn't play" case is replaced.
  - `mvp.spec.ts`: Sky Rally's Play requests `saturn_bios.bin` (404, so HLE), then play-disc, then `loader.js`; after **Start Game**, a Yabause payload (`yabause-wasm.data` or `yabause-legacy-wasm.data`) is fetched from `/emulator/vendor/data/cores/`, as the Disc Runner case checks PCSX ReARMed's. It doesn't wait for the game to run.
  - Files: `SaturnHandler.cs`, `LibraryEndpoints.cs`, `README.md` (table row only), `params.js`, `play.js`, `src/lib/emulator.ts`, and the tests below.
  - Tests:
    - `SaturnHandlerTests`, `SupportedSystemsTableTests`.
    - `LibraryEndpointsTests`: `GameDetail_BrowserCore_IsTheSystemHandlers` `saturn` → `yabause`; in the list, a Saturn bin/cue game has `playRomId` and an `.nrg` one doesn't.
    - `EmulatorEndpointsTests`: `saturn_bios.bin` is served from `Paths:Bios`.
    - `emulatorParams.test.ts`: a new `describe('Saturn (B6, B7)')`, including `contentFileName` for all three BIOS names (any case) and `discError` for `yabause`.
    - `emulatorPlay.test.ts`:
      - a new `play page Saturn` covering the disc URL, BIOS present, BIOS 404 → HLE, BIOS not 512 KiB → `saturnBiosSizeError`, other BIOS error → shown;
      - `play page PlayStation` still passes.
    - `emulator.test.ts`.
    - `mvp.spec.ts`: the Saturn case (runs in Finish).
  - Done when:
    - `npm run lint`, `npm run check` and those tests (except Playwright) pass;
    - a Saturn bin/cue game shows Play and an `.nrg` one doesn't.
- [ ] **8. Docs**:
  - `README.md`:
    - the Play bullet gains Saturn (one disc per launch, up to 1 GiB, bin/cue only, optional `saturn_bios.bin`) and 32X (needs the PicoDrive files you supply);
    - the attribution gains Yabause with its source commit and licence, and a line saying PicoDrive isn't included.
  - `docs/configuration.md`: the `Emulator:PicodriveCorePath` row and a "PicoDrive core" section with the B1 file table.
  - `docs/deployment.md`: the PicoDrive mount, from `@emulatorjs/core-picodrive@4.2.3` (the two payloads at the package root, the report from `reports/`), with the Genesis section's read-only, UID 1654 and trusted-folder advice.
  - `docs/features.md`: the Saturn and 32X play rules, the Saturn performance note, the `/api/emulator/cores` routes and the Saturn BIOS.
  - `docs/development.md`: the vendored core list and count, the explicit-core patch list and the non-threaded and no-CDN guards, the operator-core manifest and routes (replacing the `GenesisCoreManifest` and `/api/emulator/genesis` mentions), and the e2e walkthrough's 32X and Saturn cases.
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
- 2026-11-13 PicoDrive pinned values → B1's lengths and SHA-256, from `https://registry.npmjs.org/@emulatorjs/core-picodrive/-/core-picodrive-4.2.3.tgz` (code)
- 2026-11-13 Do the new cores need threads? → No, as far as the packages show: non-threaded payloads exist, their binaries use no `SharedArrayBuffer`/`pthread_create`, and `requiresThreads` lists only `ppsspp` and `dosbox_pure`; Steps 4 and 5 re-check `emulator.js` (code)
- 2026-11-13 How does play-disc reject `.nrg`? → `ShapeProblem` already rejects a lone `.nrg` and a set containing one; no new check, only tests (code)
- 2026-11-13 How does the games list hide Play for an `.nrg` primary? → It has no `playable` field: `playRomId`/`playRomFileName` are null when `PrimaryRomFileName` ends in `.nrg` (code)
- 2026-11-13 Saturn BIOS size → exactly 512 KiB with its own `saturnBiosSizeError`, matching how `scph5501.bin` is checked today, and `contentFileName` guards `saturn_bios.bin` as it does `scph5501.bin` (reviewer)
- 2026-11-13 Saturn disc wording → `discError` unchanged; its non-PSP wording already fits Saturn (code)
- 2026-11-13 Yabause licence record → the package ships no licence, so the NOTICE and licence text come from the source, traced through the binaries' `0.9.15 e24ebfc` version string, with component licences (code)
- 2026-11-13 Step size → the Genesis API refactor, the PicoDrive config and the SPA helper refactor are separate steps, so each commit stays within ~300 lines (reviewer)
- 2026-11-13 E2E coverage → add a 32X missing-core case and a Saturn play-request case to `mvp.spec.ts` (user)
- 2026-11-13 Which Saturn BIOS names does the player try? → Only `saturn_bios.bin`; `contentFileName` still renames a disc named after any of Yabause's three BIOS names (user)
- 2026-11-13 Yabause source and licence → commit `e24ebfca357bf70732a77a58c14491f13209a1cd`, GPL-2.0-or-later, with libchdr, FLAC 1.3.3, LZMA SDK 19.00, zlib 1.2.11, JUnzip and Musashi compiled in; component texts as `yabause-<component>-LICENSE.txt`, as for `pcsx_rearmed` (code)
- 2026-11-13 Problem titles on the generic routes → "The <display name> core file …", so Genesis's 409/500 titles now say "Genesis Plus GX"; codes, statuses and the SPA's Genesis messages don't change (reviewer)
- 2026-11-13 What does the Saturn e2e case check after Start Game? → that the vendored Yabause payload is fetched, as the Disc Runner case does; not that the synthetic disc runs (reviewer)

## Kickoff prompt
```text
/spec implement 2026-11-13-saturn-32x-browser-play.md
```
Starts the spec, or resumes it at the first unticked step.
