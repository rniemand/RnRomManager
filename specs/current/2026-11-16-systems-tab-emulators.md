---
title: Systems tab and emulator links
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-16
---

# Systems tab and emulator links

## Summary
The Collections page gets a fourth tab, **Systems**: every supported system on the left, the selected system's games on the right (like Publishers and Genres). An **Emulators** button above the games opens a modal listing known emulators for that system, grouped by operating system, each linking to the project's own site. RnRomManager hosts nothing; it only links. A test fails when a registered system has no emulator entry, so adding a system forces adding its emulators.

## Out of scope
- Hosting, mirroring, proxying or checking downloads; automated link checks (network checks in CI are flaky).
- Configuring or launching emulators, BIOS guidance, per-user emulator preferences.
- Editing the emulator list in the UI (it's a file in the repo).
- Any API or database change: the tab uses the existing `GET /api/systems` and `GET /api/games?system=`.
- Detecting the viewer's OS to preselect a group.

## Behaviour
- **B1 Tab.** `/collections` shows four tabs: User collections, Publishers, Genres, **Systems** (`?tab=systems`). Like the others it's a link; changing tab clears the selection and paging. Any other `tab` value still means User collections.
- **B2 URL state.** `?tab=systems&system=<id>&gamePage=<n>`. `system` is the selected system's id; `gamePage` is one-based, omitted when 1 and when no system is selected. There's no `groupPage` (the system list isn't paged). Selecting a system is a link (new history entry); game paging replaces the entry, as on the discovery tabs.
  An empty or whitespace-only `system` means no selection (B4). Ids match the system list exactly (case-sensitive), so `system=GB` is unknown (B6).
- **B3 System list (left).** Loaded from `GET /api/systems` when the page shows the Systems tab (on page load, or on switching to it from another tab) and on retry, in its order (display name); selecting a system or paging never refetches it. Layout as on the discovery tabs: `row g-4`, the list in `col-12 col-lg-4`, the right side in `col-12 col-lg-8`. Every registered system is listed, including ones with 0 games. Each row is a link: the system icon (`Icon name="system-<id>"`, controller fallback), its display name and a `N game(s)` badge. The selected row is `active` with `aria-current="true"`. The list has `aria-label="Systems"`. Loading, error (with retry) and empty states use `Loading`, `ErrorAlert` and `EmptyState` like `DiscoveryGroups`.
- **B4 No selection (right).** `EmptyState` (icon `collections`, like the discovery tabs): "Choose a system to see its games and emulators." It shows at once, without waiting for the system list (but not when the list failed, B6).
- **B5 Selected system (right).** Heading: the display name and `· N game(s)` once loaded, N being the games response's `total` (it may differ from the list badge if the library changed since the list loaded; the list isn't refreshed). Directly above the games: an **Emulators** button (`btn btn-outline-primary`, `Icon name="emulators"`) that opens the modal (B7). Games come from `GET /api/games?system=<id>&page=<gamePage>&pageSize=<size>` (the API's default sort: title ascending) and show with `GameResults` in grid view, then `Pagination`; rows per page are remembered in this browser under the page-size key `systemGames`. A system with no games (`total` 0) shows "No games for <display name> yet." (the Emulators button still shows). A page past the end (`total` > 0, no items) shows "There are no games on this page." A failed load shows `ErrorAlert` with retry. A response for an earlier system or page never replaces the current one.
- **B6 Unknown system.** A `system` value that isn't in the loaded system list (including malformed values) selects nothing in the list; the right side shows "This system isn't supported." with a **Back to all systems** link (`?tab=systems`). No games request is made and no Emulators button shows. Until the system list has loaded, the right side shows `Loading`. If the system list fails to load, the left side shows its `ErrorAlert` with retry and the right side shows nothing; no games request is made until the list has loaded.
- **B7 Emulators modal.** Built on the existing `ConfirmModal` in info mode (no `onconfirm`, so its footer shows only **Close**), `size="lg"`, title "<display name> emulators". That gives the house modal behaviour unchanged: static backdrop (M1: a background click doesn't close it), Escape, ×, `aria-labelledby` and `releaseModal`.
  - Groups in this fixed order, showing only groups with at least one emulator: **Windows**, **macOS**, **Linux**, **Android**, **iOS**. Each group is an `h3` (`h6` style) with its decorative OS icon and label, then a `ul.list-group` with `aria-label` = the label (e.g. `aria-label="Windows"`). An emulator appears in every group its `os` list names, in catalog order.
  - Each emulator is a list-group row: its name as a link to its `url` (`target="_blank"`, `rel="noopener noreferrer"`, the `external` icon, and visually hidden " (opens in a new tab)", so the link's name is "mGBA (opens in a new tab)"), its optional `note` beneath in small muted text (e.g. "RetroArch core: mGBA"), and `linkHost(url)` in muted text so the user sees where the link goes. `linkHost` is `new URL(url).hostname` as is (`www.retroarch.com`, no `www.` stripping).
  - On `md` and wider the groups lay out in two columns (`row row-cols-1 row-cols-md-2 g-3`); on narrow screens one.
  - The last line of the body, muted and small: "Links go to each project's own site. RnRomManager doesn't host or check these downloads."
  - **B8 None known:** a system whose catalog entry is empty (or missing at runtime) still opens the modal, showing an info alert "There are no known emulators for <display name>." instead of groups.
  - Accessibility: `aria-labelledby` points at the title (from `ConfirmModal`); Escape, × and **Close** close it. Bootstrap doesn't return focus for a modal opened with `show()`, so `SystemGames` focuses the **Emulators** button itself when `open` turns from true to false. It passes axe.
- **B9 Catalog.** `src/RnRomManager.Web/src/lib/systemEmulators.json`: an object keyed by system id; each value is an array (possibly empty) of `{ "name": string, "url": string, "os": ("windows"|"macos"|"linux"|"android"|"ios")[], "note"?: string }`. Rules, enforced by `SystemEmulatorsCatalogTests` (.NET, so it can read the handler registry):
  - its keys are exactly the registered handlers' ids (a missing system or a stray key fails, naming it);
  - `name` non-empty and unique within the system; `url` an absolute `https` URL; `os` non-empty, values from the five above, no duplicates; `note`, when present, non-empty;
  - the same `name` has the same `url` in every system it appears in (so an emulator is linked identically everywhere).
- **B10 Catalog content.** Only the project's own home or download page (official site, or its official GitHub/Git host page when that's the project's home); never a ROM, BIOS, key, firmware or third-party download mirror. RetroArch entries are named `RetroArch` with the note `RetroArch core: <core name>`, where the core name is the libretro core info `display_name`'s core part as it appears in RetroArch (verified at implementation time; e.g. "mGBA", "Gambatte"). OS lists reflect official builds only. Every URL and OS list is checked by hand (opened in a browser) when written or changed. The starting list and URLs are § Catalog below; an entry that doesn't hold up at implementation time is dropped or corrected and recorded in § Decisions.
- **B10a Switch.** Include each community fork of Ryujinx/yuzu whose official project page can be verified at implementation time (candidates: Ryubing, Eden, Citron), with its verified URL and official-build OS list, and record the chosen forks in § Decisions. If none can be verified, `switch` is an empty array and B8 applies.
- **B11 Permissions.** Same as the Collections page: any signed-in user. No role checks.

### Catalog (starting list)
OS codes: W Windows, M macOS, L Linux, A Android, I iOS. RetroArch is W M L A I everywhere.

| System(s) | Emulators |
|---|---|
| `gb`, `gbc` | mGBA (W M L), SameBoy (W M I), Delta (I), RetroArch (Gambatte) |
| `gba` | mGBA (W M L), Delta (I), RetroArch (mGBA) |
| `nes` | Mesen (W M L), Delta (I), RetroArch (Mesen) |
| `snes` | Snes9x (W M L), bsnes (W M L), Delta (I), RetroArch (Snes9x) |
| `n64` | Gopher64 (W M L A), Delta (I), RetroArch (Mupen64Plus-Next) |
| `vb` | Mednafen (W), RetroArch (Beetle VB) |
| `a2600` | Stella (W M L), RetroArch (Stella) |
| `a5200` | Altirra (W), RetroArch (a5200) |
| `a7800` | RetroArch (ProSystem) |
| `lynx` | Mednafen (W), RetroArch (Handy) |
| `jaguar` | BigPEmu (W L I), RetroArch (Virtual Jaguar) |
| `coleco` | Gearcoleco (W M L), RetroArch (Gearcoleco) |
| `gg`, `sms` | Gearsystem (W M L), Emulicious (W M L), RetroArch (Genesis Plus GX) |
| `md` | BlastEm (W M L), ares (W M), RetroArch (Genesis Plus GX) |
| `s32x` | ares (W M), RetroArch (PicoDrive) |
| `segacd` | ares (W M), RetroArch (Genesis Plus GX) |
| `tg16` | Mednafen (W), ares (W M), RetroArch (Beetle PCE FAST) |
| `saturn` | Ymir (W M L), Mednafen (W), RetroArch (Beetle Saturn) |
| `3do` | RetroArch (Opera) |
| `psx` | DuckStation (W M L A), RetroArch (SwanStation) |
| `psp` | PPSSPP (W M L A I) |
| `nds` | melonDS (W M L), DeSmuME (W M), Delta (I), RetroArch (melonDS DS) |
| `gc`, `wii` | Dolphin (W M L A) |
| `x360` | Xenia Canary (W L) |
| `switch` | Ryubing (W M L), Eden (W M L A), per B10a. |

Canonical URLs (B9: one URL per name):

| Emulator | URL |
|---|---|
| RetroArch | `https://www.retroarch.com/?page=platforms` |
| mGBA | `https://mgba.io/downloads.html` |
| SameBoy | `https://sameboy.github.io/downloads/` |
| Delta | `https://deltaemulator.com/` |
| Mesen | `https://www.mesen.ca/` |
| Snes9x | `https://www.snes9x.com/` |
| bsnes | `https://github.com/bsnes-emu/bsnes` |
| Gopher64 | `https://github.com/gopher64/gopher64` |
| Mednafen | `https://mednafen.github.io/releases/` |
| Stella | `https://stella-emu.github.io/downloads.html` |
| Altirra | `https://www.virtualdub.org/altirra.html` |
| BigPEmu | `https://www.richwhitehouse.com/jaguar/` |
| Gearcoleco | `https://github.com/drhelius/Gearcoleco` |
| Gearsystem | `https://github.com/drhelius/Gearsystem` |
| Emulicious | `https://emulicious.net/` |
| BlastEm | `https://www.retrodev.com/blastem/` |
| ares | `https://ares-emu.net/` |
| Ymir | `https://github.com/ymir-emu/Ymir` |
| DuckStation | `https://www.duckstation.org/` |
| PPSSPP | `https://www.ppsspp.org/download/` |
| melonDS | `https://melonds.kuribo64.net/` |
| DeSmuME | `https://desmume.org/` |
| Dolphin | `https://dolphin-emu.org/download/` |
| Xenia Canary | `https://xenia.jp/` |
| Ryubing | `https://ryujinx.app/` |
| Eden | `https://eden-emu.dev/` |

## Changes
| Area | File(s) | Change |
|---|---|---|
| Web | `src/RnRomManager.Web/src/lib/systemEmulators.json` (new) | B9/B10 catalog, all 28 systems. |
| Web | `src/RnRomManager.Web/src/lib/systemEmulators.ts` (new) | Typed import of the JSON; `emulatorOsList` (order + labels + icons) and `emulatorGroups(systemId)` → ordered non-empty `{ os, label, emulators }[]` (empty for an unknown or empty system); `linkHost(url)`. |
| Web | `src/RnRomManager.Web/src/lib/discovery.ts` | `CollectionsTab` gains `'systems'`; `collectionsTabs` gains `{ tab: 'systems', label: 'Systems' }`; `collectionsTab` accepts it; `discoveryState` keeps returning null for it; new `SystemsState { system: string \| null; gamePage }`, `systemsState(url)`, `systemsSearch(state)`; `tabSearch('systems')` → `?tab=systems`. |
| Web | `src/RnRomManager.Web/src/lib/components/SystemList.svelte` (new) | B3 list; props `systems: SystemInfo[]`, `selected: string \| null`. |
| Web | `src/RnRomManager.Web/src/lib/components/SystemGames.svelte` (new) | B5 games panel + Emulators button (icon `emulators`) and the modal; props `system: SystemInfo`, `gamePage: number`, `onpage: (gamePage: number) => void`. Fetches with `api.get<Page<GameListItem>>`. |
| Web | `src/RnRomManager.Web/src/lib/components/SystemEmulatorsModal.svelte` (new) | B7/B8, wrapping `ConfirmModal`. Named so it doesn't clash with the existing browser-play `EmulatorModal`. External `href`s may need the same `svelte/no-navigation-without-resolve` lint exception `GameResults` uses. |
| Web | `src/RnRomManager.Web/src/lib/components/icons.ts` | Add `emulators: 'bi bi-joystick'` (the Emulators button), `external: 'bi bi-box-arrow-up-right'`, `'os-windows': 'bi bi-windows'`, `'os-macos': 'bi bi-apple'`, `'os-linux': 'bi bi-tux'`, `'os-android': 'bi bi-android2'`, `'os-ios': 'bi bi-apple'` (all in the installed Bootstrap Icons 1.13). |
| Web | `src/RnRomManager.Web/src/routes/collections/+page.svelte` | Systems tab branch: loads `/api/systems` (stale-guarded, retry), renders SystemList / SystemGames / B4 / B6 states. |
| Api tests | `tests/RnRomManager.Api.Tests/SystemEmulatorsCatalogTests.cs` (new) | B9 rules against `AppHost.AddSystemHandlers`, reading the JSON from the repo root like `SupportedSystemsTableTests`. |
| Docs | `docs/features.md` | Collections: "three tabs" (in § Publishers and genres) → four, with Systems named; new `### Systems` section right after § Publishers and genres (B1–B8, "links only, nothing hosted"). Any other mention of the tab count or tab list found by `grep -rn "three tabs\|tab=publishers\|Publishers and Genres" docs README.md` is updated too. |
| Docs | `docs/adding-a-system.md` | New step: add the system to `systemEmulators.json` (empty array if none are known), enforced by `SystemEmulatorsCatalogTests`. |
| E2E | `tests/e2e/specs/mvp.spec.ts` | New test `collections systems: browse a system's games and its emulator links` (step 6). |

No migrations, config keys or API contract changes. The README supported-systems table doesn't change (no storage, format, DAT or browser-play change).

## Risks
- **Link rot**, worst for the Switch forks. Mitigation: links are hand-checked when written; the list is one JSON file, easy to update; the modal says links go to third-party sites.
- **Legal sensitivity (Switch).** Linking to emulator project pages only, never to keys, firmware or ROMs (B10). The user chose to list the forks (§ Decisions).
- **New systems.** `SystemEmulatorsCatalogTests` fails until the catalog has the new id, so it can't be forgotten.
- **Tab helpers.** `discoveryState` must keep returning null for `systems`, or the page would render the discovery panes for it; covered by `discovery.test.ts`.

## Steps
- [x] **1. Emulator catalog**: `systemEmulators.json` with all 28 systems per § Catalog and B10a (each URL and OS list opened and checked by hand), `systemEmulators.ts` helpers, the .NET check, and the adding-a-system doc step. Data-heavy, so it may exceed 300 lines. Files: `src/RnRomManager.Web/src/lib/systemEmulators.json`, `src/RnRomManager.Web/src/lib/systemEmulators.ts`, `tests/RnRomManager.Api.Tests/SystemEmulatorsCatalogTests.cs`, `docs/adding-a-system.md`. Tests: `SystemEmulatorsCatalogTests` (keys == handler ids, each B9 rule including one URL per name, with failure messages naming the system), `systemEmulators.test.ts` (group order W→I, empty groups dropped, an emulator in several groups, unknown/empty system → `[]`, `linkHost`). Done when: both pass; removing a key or adding a stray one makes the .NET test fail with its name; § Decisions records the check date, the Switch forks chosen, and every entry dropped or changed from § Catalog (URL, OS list or core name).
- [x] **2. Systems tab URL state**: `discovery.ts` changes. Files: `src/RnRomManager.Web/src/lib/discovery.ts`, `src/RnRomManager.Web/src/lib/discovery.test.ts`. Tests: `discovery.test.ts` (`collectionsTab` → `systems`; `discoveryState` null for systems; `systemsState` parses `system`, empty/whitespace → null, keeps case as given, defaults `gamePage` to 1, drops bad pages; `systemsSearch` omits page 1 and `gamePage` without a system; `tabSearch('systems')`; `collectionsTabs` has four tabs in order). Done when: tests, `npm run lint` and `npm run check` pass.
- [ ] **3. Emulators modal**: standalone `SystemEmulatorsModal` (props `open` bindable, `system: SystemInfo`) and its icons. Files: `SystemEmulatorsModal.svelte`, `icons.ts`. Tests: `SystemEmulatorsModal.test.ts` (title and `aria-labelledby`, group order and only non-empty groups with their `aria-label`, link `href`/`target`/`rel` and hidden "(opens in a new tab)", note and host shown, B8 message for an empty system, the closing note, **Close** sets `open` false; Escape and the static backdrop are `ConfirmModal`'s and stay covered by `ConfirmModal.test.ts`). Done when: tests, lint and check pass.
- [ ] **4. System games panel**: `SystemGames` with the full B5 header (heading, count, Emulators button opening the modal) and games grid. Files: `SystemGames.svelte`. Tests: `SystemGames.test.ts` (requests `/api/games?system=…`, heading + count from `total`, empty-system and past-the-end messages, error + retry, page-size key `systemGames`, stale response ignored, Emulators button shows with and without games and opens the modal, the button is focused when the modal's `open` goes back to false). Done when: tests, lint and check pass.
- [ ] **5. System list and page wiring**: `SystemList` and the page's Systems branch (B1, B3, B4, B6 and the list-error case). Files: `SystemList.svelte`, `routes/collections/+page.svelte`. Tests: `SystemList.test.ts` (all systems incl. 0 games, counts, links to `?tab=systems&system=<id>`, active/aria-current), `collections-page.test.ts` (Systems tab link and active state, B4 prompt before the list loads, the list fetched once while selecting systems, B6 unknown and wrong-case system with no games request, systems load error shows the error with nothing on the right and no games request, a valid system renders `SystemGames`). Done when: tests, lint and check pass.
- [ ] **6. Docs and e2e**: `docs/features.md` Systems section plus any other tab mentions (grep in § Changes); a new Playwright test `collections systems: browse a system's games and its emulator links`, inserted in `tests/e2e/specs/mvp.spec.ts` directly after `collections discovery: browse publishers and genres across systems, share a selection` (a seeded Game Boy game exists by then; the Library tests already filter `system=gb`). Flow: Collections → Systems tab (URL `?tab=systems`, aria-current) → choose Game Boy (URL `system=gb`, grid shows the seeded `Orbit Blocks`) → Emulators → the dialog's `list` named `Windows` has the link "mGBA (opens in a new tab)" with an `https` `href` and `target="_blank"` → axe check → Close (focus back on the button). Files: `docs/features.md`, `tests/e2e/specs/mvp.spec.ts`. Tests: the new e2e test. Done when: docs describe B1–B8; `npx playwright test --list` in `tests/e2e` lists the new test (the file parses); the test passes in Finish's `pwsh scripts/test-e2e.ps1`.

## Decisions
- 2026-11-16 Where does the emulator list live? → A JSON file in the SPA, read directly; a .NET test checks its keys against the registered handlers. No API change. (user)
- 2026-11-16 Which OS groups? → Windows, macOS, Linux, Android, iOS; empty groups hidden. (user)
- 2026-11-16 List RetroArch? → Yes, per system with its core name in the note. (user)
- 2026-11-16 Systems with 0 games in the left list? → Listed, with their count. (user)
- 2026-11-16 Switch emulators (yuzu/Ryujinx taken down)? → List the current community forks' official project pages. (user)
- 2026-11-16 Automated link checks? → No; format checks in tests, URLs checked by hand. (user)
- 2026-11-16 System list paging? → None; ~30 systems fit one list. (code)
- 2026-11-16 Switch forks to list? → Only those whose official page is verified at implementation time; empty (B8) if none. (reviewer)
- 2026-11-16 Same emulator across systems? → One canonical URL per name, enforced by the .NET test. (reviewer)
- 2026-11-16 Step order? → Modal, then games panel, then list + page wiring, so each step is testable alone. (reviewer)
- 2026-11-16 How is the Emulators modal built, and does a backdrop click close it? → On `ConfirmModal` in info mode, `size="lg"`; static backdrop per house rule M1, so a backdrop click doesn't close it; the disclaimer moves to the end of the body. (code)
- 2026-11-16 Does focus return to the Emulators button by itself? → No, Bootstrap only restores focus for `data-bs-toggle` triggers; `SystemGames` focuses it when `open` goes false. (code)
- 2026-11-16 When is the system list fetched, and does B4 wait for it? → On entering the Systems tab and on retry, never on selection or paging; B4 shows at once. (reviewer)
- 2026-11-16 Group markup and link host? → `h3` + `ul.list-group` with `aria-label` = OS label; host is `new URL(url).hostname` unchanged. (reviewer)
- 2026-11-16 Icons? → `emulators` bi-joystick, `external` bi-box-arrow-up-right, OS: bi-windows, bi-apple (macOS, iOS), bi-tux, bi-android2; all in Bootstrap Icons 1.13. (code)
- 2026-11-16 Catalog OS lists checked on the web → SameBoy W M I (no Linux build), Mednafen W (Linux is source only), ares W M, DeSmuME W M, Dolphin W M A (no Linux builds on its site); RetroArch TG16 core is "Beetle PCE Fast". Still re-checked by hand at implementation (B10); Snes9x, DeSmuME, Ymir and the Switch forks couldn't be fully verified this pass. (reviewer)
- 2026-11-16 simple64 is archived (Feb 2025) → replace with Gopher64 (`https://github.com/gopher64/gopher64`, OS list confirmed at implementation). (user)
- 2026-11-16 Delta also plays GB, GBC and DS → add Delta (I) to `gb`, `gbc` and `nds`. (user)
- 2026-11-16 Catalog checked by hand (B10, each page opened; GitHub release assets read for OS lists) → changes from § Catalog: Gopher64 W M L A (macOS and Android builds exist); BigPEmu W L I (iOS App Store port); Dolphin W M L A (Linux AppImages now offered); Xenia Canary W L (official release repo ships a Linux build); Ymir URL `https://github.com/ymir-emu/Ymir` (repo moved); TG16 core named "Beetle PCE FAST" (libretro `display_name`). All other entries confirmed as listed. (implementation)
- 2026-11-16 Switch forks (B10a) → Ryubing (`https://ryujinx.app/`, domain verified by the Ryubing GitHub org; W M L) and Eden (`https://eden-emu.dev/`, project's own site; W M L A). Citron dropped: its site is parked and its successor couldn't be verified. (implementation)

## Kickoff prompt
```text
/spec implement 2026-11-16-systems-tab-emulators.md
```
Starts the spec, or resumes it at the first unticked step.
