---
title: In-browser emulators (GB, GBC, GBA)
status: ready
created: 2026-11-13
---

# In-browser emulators (GB, GBC, GBA)

## Summary
Signed-in users can play a Game Boy, Game Boy Color or Game Boy Advance release straight from the Game detail page. A **Play** button on each release card opens a modal running [EmulatorJS](https://emulatorjs.org) (Gambatte for GB/GBC, mGBA for GBA), self-hosted in the image. The ROM is streamed from the library ZIP through the existing download endpoint, so nothing is extracted to disk and nothing needs cleaning up.

## Out of scope
- Save states (the save/load-state buttons are hidden). In-game battery saves stay in the player's browser only (B8); nothing is stored server-side.
- Systems other than `gb`, `gbc`, `gba`; external-storage releases (Switch, disc systems).
- Netplay, cheats, BIOS files, ROM patches, shaders, a controls config UI.
- Loading EmulatorJS from its CDN; multi-threaded (`-thread`) cores (they need cross-origin isolation headers).
- A hotkey to start playing.

## Behaviour
- **B1 Supported systems.** A handler opts in through a new `ISystemHandler.BrowserCore` (`string?`, default `null`): the EmulatorJS core name. `gb` and `gbc` return `gambatte`, `gba` returns `mgba`; every other handler stays `null`.
- **B2 API.** `GET /api/games/{id}` adds a top-level `browserCore`: the game's system handler's `BrowserCore`, or `null` when the handler doesn't set one or the game's system has no registered handler (never a 500). No other contract change; no migration, no config key.
- **B3 Play button.** On the Game detail Releases and Updates tabs, a release card shows a Play button when `canPlay(game, rom)` (in `src/lib/emulator.ts`) is true: `game.browserCore` is non-null and `rom.externalFileName` is null. The button is `btn btn-sm btn-outline-success` with Icon `play` (`bi bi-play-fill`) and `aria-label="Play {fileName}"`, placed before the Download button. Every signed-in user sees it (same rule as Download).
- **B4 Modal.** The page holds one `EmulatorModal` instance, driven by a `playing` state (`{ rom } | null`) set by the button, like the page's other modals. It's a `ConfirmModal` info modal (M1 rules, Close only) with a new `size="xl"`, title `Play {fileName}`. The body is a Bootstrap `ratio ratio-4x3` container (EmulatorJS letterboxes GB/GBC's 10:9 and GBA's 3:2 inside it) holding a same-origin `<iframe>` with `title="Emulator: {fileName}"`, `allow="fullscreen; gamepad; autoplay"` and `src=/emulator/play.html?game={gameId}&rom={romId}&core={browserCore}&name={encodeURIComponent(fileName)}`. The iframe is rendered only while the modal is open. Closing the modal (×, Close, or Escape while focus is outside the iframe) removes it, which stops the emulator and its audio. Focus after close follows the existing `ConfirmModal` behaviour (no new focus handling). Escape while focus is inside the iframe goes to the emulator and doesn't close the modal. Keyboard input inside the iframe never reaches the app's global hotkeys (separate document).
- **B5 Play page.** `/emulator/play.html` is a static page with no inline script: a `#game` container, a `#status` line, and `<script type="module" src="play.js">`. `play.js`:
  1. Calls `parsePlayParams(location.search)` from `params.js`: `game` a GUID, `rom` a positive integer, `core` one of `gambatte`/`mgba`, `name` 1–255 chars. Anything else shows "Invalid emulator link." and stops.
  2. `fetch`es `/api/games/{game}/roms/{rom}/download` (same origin, cookies). A non-2xx shows "Couldn't load the ROM (HTTP {status})." (401: "Your session has expired. Sign in again.").
  3. Wraps the bytes as `new File([blob], name)`, so the core sees the real extension (the URL ends in `/download`).
  4. Sets the globals `EJS_player = '#game'`, `EJS_core`, `EJS_gameUrl = file`, `EJS_gameName = name`, `EJS_pathtodata = '/emulator/vendor/data/'`, `EJS_disableDatabases = true` (no ROM/core cache in IndexedDB), `EJS_disableAutoLang = false` (with `EJS_language` unset, `loader.js` loads no localization file and uses its built-in English), and `EJS_Buttons` hiding `saveState`, `loadState`, `quickSave`, `quickLoad`, `netplay`, `cheat`, `exitEmulation`.
  5. Only then appends `<script src="/emulator/vendor/data/loader.js">`. Its `error` event shows "Couldn't load the emulator."

  All status text is set with `textContent`. EmulatorJS's own start button stays (browsers need a click inside the frame before audio).
- **B6 No temp files.** The ROM is read through the existing `GET /api/games/{id}/roms/{romId}/download` (streams the ZIP entry, auth required). There's no server-side extraction, so closing the modal has nothing to delete.
- **B7 Security headers.** Responses whose path `StartsWithSegments("/emulator", StringComparison.OrdinalIgnoreCase)` get their own CSP: `default-src 'self'; script-src 'self' 'wasm-unsafe-eval' blob:; worker-src 'self' blob:; connect-src 'self' blob: data:; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'self'`. Every other response keeps today's CSP unchanged, including `frame-ancestors 'none'`. If the real cores need more (for example `'unsafe-eval'`), step 5 adds the minimum, records it in § Decisions and updates B7. EmulatorJS's update check and CDN core fallback (`cdn.emulatorjs.org`) are blocked by `connect-src 'self'` on purpose: console noise only, no third-party requests.
- **B8 Browser saves.** EmulatorJS keeps a cartridge's battery saves in the browser's IndexedDB, keyed by `EJS_gameName` (the release file name). They survive closing the modal and reloading, on that browser only. Two releases with the same file name would share saves; release file names come from the DATs and are unique per system, so that's accepted and documented.
- **B9 Vendored assets.** EmulatorJS `4.2.3` (`@emulatorjs/emulatorjs`, `@emulatorjs/core-gambatte`, `@emulatorjs/core-mgba`, exact versions) are npm `dependencies`. `node scripts/vendor-emulatorjs.mjs [outDir]` (web project; `outDir` defaults to `static/emulator/vendor`, relative to the web project) empties `outDir` and writes:
  - from `@emulatorjs/emulatorjs/data/`: `loader.js`, `src/*.js`, `emulator.css`, `compression/*` (except its `README.md`) and `version.json`, under `data/`;
  - from each core package's root: `<core>-wasm.data` and `<core>-legacy-wasm.data` (the WebGL 1 fallback) into `data/cores/`, and `reports/<core>.json` into `data/cores/reports/`;
  - `data/emulator.min.js` (the eight `src/` scripts concatenated in `loader.js`'s order) and `data/emulator.min.css` (a copy of `emulator.css`), because the npm package ships no minified build and `loader.js` otherwise 404s first;
  - each package's licence file as `licenses/<package>.txt`.

  A missing source file fails the script (non-zero exit). It runs from the `prebuild` and `predev` npm scripts, so `npm run build` and the Docker image always contain it. No localization files are copied (B5).
- **B10 Static files.** The static file middleware serves `.data` as `application/octet-stream` (ASP.NET's default content-type table has no `.data`, so those files would 404). Unknown paths under `/emulator/` return a plain 404 instead of the SPA's `index.html`, so a missing vendor file fails clearly.
- **B11 Licences.** `licenses.json` lists the three packages (EmulatorJS `GPL-3.0`, Gambatte `GPL-2.0`, mGBA `MPL-2.0`; confirm each from its licence file in step 3) with their repository URLs, so the About page shows them and `LicenseInfoTests` passes. The licence texts are served under `/emulator/vendor/licenses/`.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Systems/ISystemHandler.cs` | `string? BrowserCore => null;` (B1) |
| Systems | `src/RnRomManager.Systems.GameBoy/GameBoyHandlerBase.cs`, `GameBoyAdvanceHandler.cs` | `gambatte` / `mgba` (B1) |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | game detail `browserCore` via `ISystemRegistry` (B2) |
| Api | `src/RnRomManager.Api/Security/SecurityHeaders.cs` | the `/emulator` CSP (B7) |
| Api | `src/RnRomManager.Api/AppHost.cs` | a `FileExtensionContentTypeProvider` with `.data` added, in `staticFiles` (B10) |
| Api | `src/RnRomManager.Api/Endpoints/FallbackEndpoints.cs` | `/emulator/{**rest}` → 404 (B10) |
| Api | `src/RnRomManager.Api/licenses.json` | three EmulatorJS entries (B11) |
| Web | `package.json` (deps, `prebuild`, `predev`), `package-lock.json`, `scripts/vendor-emulatorjs.mjs`, `src/RnRomManager.Web/.gitignore` (`/static/emulator/vendor/`, which also keeps ESLint off it; Prettier already ignores `/static/`) | vendored EmulatorJS (B9) |
| Web | `static/emulator/play.html`, `static/emulator/params.js` (JSDoc-typed; type-checked through its test's import), `static/emulator/play.js` | play page (B5); both linted by ESLint only (Prettier and `tsconfig` skip `static/`) |
| Web | `src/lib/api/types.ts` (`GameDetail.browserCore`), `src/lib/emulator.ts` (`canPlay`, `playUrl`), `src/lib/components/icons.ts` (`play`), `ConfirmModal.svelte` (`size="xl"`), new `src/lib/components/EmulatorModal.svelte`, `src/routes/games/[id]/+page.svelte` | button + modal (B3, B4) |
| Docs | `docs/features.md` (Game detail: Play; `browserCore` in the `GET /api/games/{id}` reference), `docs/development.md` (vendoring, the `/emulator` CSP and 404, `EmulatorModal` in the component list), `docs/adding-a-system.md` (`BrowserCore`) | doc rule |
| e2e | `tests/e2e/specs/mvp.spec.ts` | play flow (step 7) |

There's no migration, no config key and no data change. The API change is additive.

## Risks
- **GPL in an MIT image:** EmulatorJS is GPL-3.0. It's shipped unmodified as separate files (aggregation), with its licence text and source URL (B11). The app's own code stays MIT.
- **CSP widening:** it's limited to `/emulator/*`, which serves only the vendored files and the play page; the SPA and API keep the strict policy. The play page accepts only validated ids and a fixed core list (B5), so it can't be pointed at another URL. Unknown `/emulator/*` paths are 404, never the SPA under the relaxed CSP (B10).
- **Minified build by concatenation (B9):** this relies on the eight scripts being plain globals loaded in order, which is exactly what `loader.js`'s own fallback does. It's verified manually in step 5, and pinned versions keep it stable. Upgrading EmulatorJS means re-checking it.
- **Image size:** about +5 MB (two cores × two variants, plus data).
- **axe:** EmulatorJS's own UI may not pass axe, so the e2e scan of the open modal excludes the iframe (documented in the test). The modal chrome is still scanned.

## Steps
- [ ] **1. Handler flag and API field**: `ISystemHandler.BrowserCore` (B1), the GB/GBC/GBA values, and `browserCore` on `GET /api/games/{id}` (B2). Files: `ISystemHandler.cs`, `GameBoyHandlerBase.cs`, `GameBoyAdvanceHandler.cs`, `LibraryEndpoints.cs`. Tests: `GameBoyHandlerTests` (gb/gbc → `gambatte`, gba → `mgba`); `LibraryEndpointsTests` (a GB game returns `"gambatte"`, a seeded non-GB game returns `null`, a game whose system has no registered handler returns `null` with 200). Done when: those tests pass and no other handler changed.
- [ ] **2. `/emulator` CSP, content type and 404**: B7 and B10. Files: `SecurityHeaders.cs`, `AppHost.cs`, `FallbackEndpoints.cs`. Tests: `SecurityHeadersTests` (`/emulator/play.html`, `/Emulator/x` and bare `/emulator` get the B7 policy; `/`, `/api/systems` and `/emulatorx` keep the old one with `frame-ancestors 'none'`); a test host whose temp web root (set through `WebHostDefaults.WebRootKey`, as `SecurityHeadersTests` already does) holds an `emulator/test.data` file returns 200 `application/octet-stream` for it, and 404 (not `index.html`) for `/emulator/missing.js`. Done when: tests pass.
- [ ] **3. Vendor EmulatorJS**: the npm deps pinned to `4.2.3`, `scripts/vendor-emulatorjs.mjs`, the `prebuild`/`predev` scripts, `.gitignore`, and the `licenses.json` entries (B9, B11). Files: `src/RnRomManager.Web/package.json`, `package-lock.json`, `src/RnRomManager.Web/scripts/vendor-emulatorjs.mjs`, `src/RnRomManager.Web/.gitignore`, `src/RnRomManager.Api/licenses.json`. Tests: `src/lib/vendorEmulatorjs.test.ts` (runs the script with a temp `outDir`; asserts the B9 file list, that `emulator.min.js` contains each `src/` script in loader order, that there are no `-thread` cores, and that the licences were copied); `LicenseInfoTests`. Done when: tests pass; `npm run lint` and `npm run check` pass after the script has run; `npm run build` output contains `emulator/vendor/data/loader.js` and all four core files; `docker build --target web .` succeeds and its `/src/web/build/emulator/vendor/data/cores/` holds them; and `git status` shows no vendored files.
- [ ] **4. Play page parameters**: `static/emulator/params.js` exporting `parsePlayParams(search)` → `{ game, rom, core, name }` or `null` (B5 step 1). Files: `static/emulator/params.js`. Tests: `src/lib/emulatorParams.test.ts`, importing `../../static/emulator/params.js` (a valid query; a bad GUID; rom `0`, `-1` and `1.5`; an unknown core; an empty name and a 256-char name; missing params → `null`). Done when: the test, lint and check pass.
- [ ] **5. Play page**: `play.html` and `play.js` (B5). Files: `static/emulator/play.html`, `static/emulator/play.js`. Manual check against the built app served by the API, so that B7 and B10 apply: `npm run build`, copy `build/` to `src/RnRomManager.Api/wwwroot/`, run the API, seed and ingest the Basic set. A seeded GB, GBC and GBA release each boot to the emulator when opened at `/emulator/play.html?…`. The network tab shows only same-origin requests (no `localization/` request). There are no CSP violations except the blocked `cdn.emulatorjs.org` update check. Signing out and reloading shows the session message. A bad query shows "Invalid emulator link." Record any CSP change in § Decisions. Done when: the manual check passes and is listed in the commit message.
- [ ] **6. Play button and modal**: `GameDetail.browserCore`, `src/lib/emulator.ts` (`canPlay`, `playUrl`), Icon `play`, `ConfirmModal` `size="xl"`, `EmulatorModal.svelte`, and the release-card button with the one modal instance (B3, B4). Files: `types.ts`, `emulator.ts`, `icons.ts`, `ConfirmModal.svelte`, `EmulatorModal.svelte`, `routes/games/[id]/+page.svelte`. Tests: `src/lib/emulator.test.ts` (`canPlay`: null core → false, external release → false, supported → true; `playUrl` encodes the name); `EmulatorModal.test.ts` (the iframe `src` and `title` come from the props; the iframe is gone after close); `ConfirmModal.test.ts` (`xl` adds `modal-xl`). Done when: lint, check and those tests pass.
- [ ] **7. Docs and e2e**: the docs per § Changes. In `mvp.spec.ts`, after ingest, open Orbit Blocks (the seeded GB game) and click **Play** on its primary release. Assert that the modal and iframe appear, the ROM download request returns 200, and `frameLocator('iframe')` shows EmulatorJS's start button. Run the axe scan with the iframe excluded. Click into the iframe and press Escape: the modal stays open. Close it with the Close button: the iframe is gone. Files: `docs/features.md`, `docs/development.md`, `docs/adding-a-system.md`, `tests/e2e/specs/mvp.spec.ts`. Tests: the e2e runs in Finish. Done when: the docs cover B1–B11 and `npx playwright test --list` includes the flow.

## Decisions
- 2026-11-13 Where do the EmulatorJS files come from? → Self-hosted from pinned npm packages, copied into the SPA build (user)
- 2026-11-13 Which cores? → Gambatte for GB/GBC, mGBA for GBA (user)
- 2026-11-13 Extract the ROM to a temp file and delete it on close? → No: stream it through the existing download endpoint, so there's nothing to clean up (user)
- 2026-11-13 Inline in the SPA or isolated? → A same-origin iframe with its own CSP; the app's CSP and hotkeys are untouched (user)
- 2026-11-13 Battery saves? → Keep EmulatorJS's browser-local saves and hide the save/load-state buttons (user)
- 2026-11-13 Where does "supported" live? → `ISystemHandler.BrowserCore`, like every other per-system fact (code)
- 2026-11-13 How does the core get a file name with an extension? → Pass a `File` named after the release (EmulatorJS uses its name), since the download URL ends in `/download` (code)
- 2026-11-13 Language? → `EJS_disableAutoLang = false` with no `EJS_language`, because `loader.js` fetches a localization file whenever auto-language is on, even for `en-US` (reviewer R4)
- 2026-11-13 `.data` files and missing vendor files? → Map `.data` to `application/octet-stream`; `/emulator/**` 404s instead of falling back to the SPA (reviewer R1, R6)
- 2026-11-13 Battery-save key? → The release file name; collisions need identical DAT file names, so they're accepted and documented (reviewer R8)

## Kickoff prompt
```text
/spec implement 2026-11-13-browser-emulators.md
```
Starts the spec, or resumes it at the first unticked step.
