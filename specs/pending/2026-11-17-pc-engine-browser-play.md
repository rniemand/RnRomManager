---
title: PC Engine browser play
status: ready # draft | ready | in-progress | completed
created: 2026-11-17
---

# PC Engine browser play

## Summary
PC Engine HuCards already import as the `tg16` system: `.pce`, stored in the game ZIP, matched against libretro's `NEC - PC Engine - TurboGrafx 16` No-Intro DAT, which lists both Japanese PC Engine and US TurboGrafx-16 releases. This spec adds in-browser play through EmulatorJS's `mednafen_pce` core (Beetle PCE, GPL; the exact expression is settled by P4). The core is vendored the same way PUAE was, so it needs no operator setup. The system's display name also changes to `TurboGrafx-16 / PC Engine` so users can find it under either name.

## Out of scope
- A separate `pce` system. Both systems would match the same DAT, so one game could be ingested twice.
- PC Engine CD / TurboGrafx-CD (disc, needs a System Card BIOS) and SuperGrafx (its own DAT, and `.sgx` isn't in the core's `core.json`).
- Changes to ingestion, storage, the DAT, the `tg16` id, folders or colours. Existing data is untouched.
- A `getCore()` pin, a core-chooser hide or a custom control scheme. EmulatorJS 4.2.3's `pce` group holds only `mednafen_pce`, so no other saved core can be chosen, and the `pce` controls apply as upstream (the same as `puae`).

## Behaviour
- **P1 Display name.** `TurboGrafx16Handler.DisplayName` = `TurboGrafx-16 / PC Engine`. `Id`, `ShortName` (`TG16`), `Family`, extensions, sizes and DAT are unchanged. `/api/systems` order is unchanged: the new name still sorts after `Super Nintendo Entertainment System` and before `Virtual Boy` (`OrdinalIgnoreCase`). Every place that shows the name from the API (System filter, badges, Admin views) shows the new name. Nothing in the SPA hard-codes it.
- **P2 Browser core.** `TurboGrafx16Handler.BrowserCore` = `mednafen_pce`. Game list and detail responses advertise it for `tg16`. Play follows the existing embedded-release rules: the card's Quick Play targets the primary release, the detail Play targets the selected release, and the play page fetches `GET /api/games/{id}/roms/{romId}/download` (the stored `.pce`, header already stripped at ingest). No BIOS request is made. `EJS_disableCue` stays unset, which upstream already treats as disabled for `mednafen_pce`. Battery and state saves use the existing generic save flow (`core.json` `save: "srm"`).
- **P3 Vendored core.** `@emulatorjs/core-mednafen_pce` `4.2.3` becomes an exact-version dependency. `vendor-emulatorjs.mjs` copies its two non-threaded payloads and its report into `static/emulator/vendor/data/cores/`, like the other vendored cores, and never copies the `-thread` variants. `params.js` adds `mednafen_pce` to the allowed play-page cores. It is not added to `hidesCoreChooser`, the `getCore()` pin, the threaded-payload guard or `biosFor`. Pinned bytes (npm archive `core-mednafen_pce-4.2.3.tgz`, checked 2026-11-17):

  | File in npm package | Bytes | SHA-256 |
  |---|---:|---|
  | `mednafen_pce-wasm.data` | 994,844 | `29cebda0c7a93bbcb5e67e97fe28a1886bd030715d5a25224e7d9175d1d985c3` |
  | `mednafen_pce-legacy-wasm.data` | 995,095 | `c3d6e52680175b5528964b2c506a462bfd330d856661f28c5a54f7114b9b9a4c` |
  | `reports/mednafen_pce.json` | 223 | `3c82092f1433cc2cf0d9265e137f44c5a80f5a7dfbfcb712ef659797299a67ca` |

  Each payload (7z) holds `mednafen_pce_libretro.js`, `.wasm`, `build.json`, `core.json` (extensions `pce, cue, ccd, iso, img, bin, chd`) and `license.txt` (GPL-2.0, 341 lines).
- **P4 Licences.** `emulator-licenses/` gains `mednafen_pce-LICENSE.txt` (the payload's `license.txt`, byte for byte) and `mednafen_pce-NOTICE.txt`, which follows `puae-NOTICE.txt`: the source commit of `EmulatorJS/beetle-pce-libretro` (identified from the version string in both binaries and checked against the repository), build flags from `cores.json`, the copyright holders, the licence, and every bundled component with its own terms (each with a `mednafen_pce-<component>-LICENSE.txt` where the source ships one). `COMMON-BUILD.txt` lists the package in its opening package list and in its "license.txt only for" list. That is valid because the build provenance matches, as checked on 2026-11-17: `reports/mednafen_pce.json` `buildStart` is 2025-06-14, payload `build.json` `version` is `2.0.2`, and the `.wasm` contains the RetroArch version string `6dd4353`. Implement also checks that the `.wasm` producers section names Emscripten 4.0.8, as COMMON-BUILD.txt claims for the others. If it doesn't, stop and ask. **One licence expression** goes in the NOTICE, `licenses.json` and the README. It is decided from the source's file headers (`GPL-2.0` if they say version 2 only, `GPL-2.0-or-later` if they allow later versions) plus any component licences, and recorded in § Decisions. `src/RnRomManager.Api/licenses.json` gains an `@emulatorjs/core-mednafen_pce` entry with that expression and `https://github.com/EmulatorJS/beetle-pce-libretro`. README attribution: Beetle PCE joins the line-148 core name list ("…, DeSmuME, PUAE and Beetle PCE cores") and the line-149 RetroArch-build list, with its own sub-bullet after PUAE's: `[Beetle PCE \`<commit>\`](…/tree/<full sha>) (TurboGrafx-16 / PC Engine): <expression>`, plus its components.
- **P5 README.** The supported-systems row becomes `| TurboGrafx-16 / PC Engine | \`tg16\` | In ZIP | \`.pce\` | No-Intro | \`mednafen_pce\` |` (generated and checked by `SupportedSystemsTableTests`). The feature overview's Play list gains `TurboGrafx-16 / PC Engine`, and the upgrade note's system list uses the new name.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Systems | `src/RnRomManager.Systems.Nec/TurboGrafx16Handler.cs` | P1 name, P2 `BrowserCore` |
| Web | `src/RnRomManager.Web/package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `static/emulator/params.js` | P3 |
| Licences | `src/RnRomManager.Web/emulator-licenses/{mednafen_pce-LICENSE.txt,mednafen_pce-NOTICE.txt,COMMON-BUILD.txt}` (plus any component licences), `src/RnRomManager.Api/licenses.json` | P4 |
| Tests | `tests/RnRomManager.Systems.Nec.Tests/TurboGrafx16HandlerTests.cs`, `tests/RnRomManager.Api.Tests/Integration/LibraryEndpointsTests.cs`, `tests/RnRomManager.Api.Tests/SupportedSystemsTableTests.cs` (via README), `src/RnRomManager.Web/src/lib/{vendorEmulatorjs,emulatorParams}.test.ts`, `tests/e2e/specs/mvp.spec.ts` | P1–P3 |
| Docs | `README.md`, `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md`, `docs/zip-format.md` and `docs/deployment.md` wherever they show the display name | P1, P4, P5 |

No migrations, config keys or new routes. The API change is additive (`browserCore` is now non-null for `tg16`) plus the `displayName` text for `tg16`. Stored ZIPs and `info.json` hold the id, not the display name, so existing data needs no change.

## Risks
- **Licence notice accuracy.** The source commit and bundled components must be read from the binaries and checked against the repository, as for PUAE. If the commit can't be identified, stop and ask before vendoring.
- **Report quirk.** The upstream `reports/mednafen_pce.json` and `core.json` carry a `Beetle PSX HW` options block (an upstream copy-paste). It names a setting Beetle PCE doesn't read, so it's harmless. The files are copied unchanged and pinned by hash.
- **Rename.** External scripts that matched the `TurboGrafx-16` display name text break. The id is the stable key and is unchanged.
- **Image size.** About 2 MB more in the vendored output.

## Steps
- [ ] **1. Vendor the Beetle PCE core**: P3, P4 (all but the README attribution). Add the dependency (`npm install --save-exact @emulatorjs/core-mednafen_pce@4.2.3`), add `mednafen_pce` to `cores` in `vendor-emulatorjs.mjs` and `params.js`, and write the licence files and the `licenses.json` entry. Files: `package.json`, `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `static/emulator/params.js`, `emulator-licenses/*`, `src/RnRomManager.Api/licenses.json`, `src/lib/vendorEmulatorjs.test.ts`, `src/lib/emulatorParams.test.ts`. Tests: `vendorEmulatorjs.test.ts` (the licence file list gains the new files, the core list gains `mednafen_pce`, and the P3 table's three byte counts and SHA-256s are pinned like `puae`'s; no `-thread` file is vendored); `emulatorParams.test.ts` (`parsePlayParams` accepts `core=mednafen_pce`; `hidesCoreChooser('mednafen_pce')` is false; `controlSchemeFor('mednafen_pce', 'X (USA).pce')` is null). Done when: `npm run lint`, `npm run check`, `npx vitest run src/lib/vendorEmulatorjs.test.ts src/lib/emulatorParams.test.ts` and `npm run build` pass, `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~LicenseInfoTests"` passes, and the vendored output holds `mednafen_pce-wasm.data`, `mednafen_pce-legacy-wasm.data`, `reports/mednafen_pce.json` and both new licence files.
- [ ] **2. Enable Play and rename tg16**: P1, P2, P5 (table row only). Set `BrowserCore` and `DisplayName` in the handler. Update the README supported-systems row in the same commit. Files: `TurboGrafx16Handler.cs`, `TurboGrafx16HandlerTests.cs`, `LibraryEndpointsTests.cs`, `README.md`. Tests: `TurboGrafx16HandlerTests` (the contract tuple has the new display name; `BrowserCore` is `mednafen_pce`); `LibraryEndpointsTests` (`[TestCase("tg16", false, true, "mednafen_pce")]` in `Games_QuickPlay_IsThePrimaryReleaseWhenPlayable`, `[TestCase("tg16", "mednafen_pce")]` in `GameDetail_BrowserCore_IsTheSystemHandlers`, and the display-name list at line 186 uses `TurboGrafx-16 / PC Engine` in the same position); `SupportedSystemsTableTests`. Done when: `dotnet test tests/RnRomManager.Systems.Nec.Tests` passes, `dotnet test tests/RnRomManager.Api.Tests --filter "FullyQualifiedName~LibraryEndpointsTests|FullyQualifiedName~SupportedSystemsTableTests|FullyQualifiedName~SystemStartupTests"` passes (Docker running), and `dotnet build` passes with warnings as errors.
- [ ] **3. e2e Play**: P2 end to end. In `tests/e2e/specs/mvp.spec.ts`, add `test("Play: TurboGrafx-16 / PC Engine Quick Play from the library uses the local Beetle PCE core", { tag: "@shard-b" }, …)` modelled on the NES Quick Play case. Search `Neon Circuit`, click `Play Neon Circuit`, and assert the iframe `src` ends with `core=mednafen_pce&name=Neon%20Circuit%20\(USA\)\.pce`. Assert the ROM download returns 200 and that no `/api/emulator/bios/` request is made, then call `startEmulator("mednafen_pce")` and `closeEmulator()`. Update any e2e assertion that shows the old display name text. Files: `tests/e2e/specs/mvp.spec.ts`. Done when: the new case and the existing System-filter cases pass (the full e2e run happens in Finish).
- [ ] **4. Docs**: P4 README attribution, P5 overview and upgrade note. Also: `features.md` (the Play section gains a TurboGrafx-16 / PC Engine line: vendored Beetle PCE, HuCards only, no BIOS; the systems list at line 3 uses the new name); `development.md` (the vendoring list gains `@emulatorjs/core-mednafen_pce` and the stale "fourteen cores" becomes the actual count of non-PPSSPP vendored cores, sixteen; the play-page core list gains `mednafen_pce` as vendored); `adding-a-system.md` (`BrowserCore` list: `tg16` returns `mednafen_pce`, vendored, no BIOS; the box-art list at line 56 uses the new name). Rename the user-facing system lists only: README lines 15, 60 (step 2) and 126, `features.md:3`, and `adding-a-system.md:56`. Keep the rest, because they describe the handler, history or seed data: `adding-a-system.md:58`, `deployment.md:230` (the historical "versions that add" list), `development.md:81/84/88`, `features.md:183` (the ingest paragraph already says "TurboGrafx-16 / PC Engine"), and `zip-format.md:232`. Tests: none. Done when: every item is in its doc, and `grep -n "TurboGrafx-16" README.md docs/*.md | grep -v "TurboGrafx-16 / PC Engine"` lists exactly the kept lines above except `features.md:183` (line numbers may shift). That line also contains "TurboGrafx-16 / PC Engine", so `grep -v` drops it. Check it by eye: its bare mentions in the families and size-limit lists stay unchanged.

## Decisions
- 2026-11-17 Separate `pce` system or the existing `tg16` → `tg16` gets browser play. The shared DAT would ingest the same game twice (user).
- 2026-11-17 Display name → `TurboGrafx-16 / PC Engine`, id unchanged (user).
- 2026-11-17 Vendored or operator-supplied core → vendored. Beetle PCE is GPL (exact expression per P4) and ships `license.txt`, like PUAE (code and package).
- 2026-11-17 Pin or hide the core chooser → neither. `pce` is a single-core group in EmulatorJS 4.2.3, as with `puae` (code).
- 2026-11-17 Build provenance → matches COMMON-BUILD.txt (2025-06-14 build, build.json 2.0.2, RetroArch `6dd4353`). Emscripten 4.0.8 is checked in step 1 (code, reviewer).
- 2026-11-17 Doc rename → only the user-facing system lists. History, handler-pattern and seed mentions keep `TurboGrafx-16` (reviewer).
- 2026-11-17 PC Engine CD and SuperGrafx → out of scope. The request covers files stored in the ZIP (user request, `core.json`).

## Kickoff prompt
```text
/spec implement 2026-11-17-pc-engine-browser-play.md
```
Starts the spec, or resumes it at the first unticked step.
