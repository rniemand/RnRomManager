---
title: IGDB image suggestions
status: in-progress # draft | ready | in-progress | completed
created: 2026-11-20
---

# IGDB image suggestions

## Summary
Admins fill in missing or poor art today by opening **Google Images**, copying an image URL and pasting it into **Upload Image**. A new **Suggest Images** button on the Game page's Images tab asks IGDB (Twitch's game database) for up to five box-art or screenshot candidates for the game and adds the one the Admin clicks to the game's ZIP with the chosen type, through the existing `POST /api/games/{id}/images/from-url` path.

## Out of scope
- Other sources (SteamGridDB, web image search, scraping) and Title screen / Other image types (the upload and `from-url` APIs only take `BoxArt` and `Snap`).
- Editing the IGDB credentials in the UI: they are configuration, like every other `Sources:*` key.
- Using IGDB for metadata (year, publisher, genres) or during ingestion / Retry media.
- A server-side proxy for the previews: the browser loads them from `images.igdb.com` (S9).
- Caching suggestions between requests.

## Behaviour

### Configuration
- **S1** Two new keys in `SourcesOptions`: `Sources:IgdbClientId` and `Sources:IgdbClientSecret` (env `Sources__IgdbClientId`, `Sources__IgdbClientSecret`), both default empty. A key is *set* when it is non-empty after trimming (the validator and `IImageSuggestions.Configured` use this same rule, and the trimmed values are what's sent). Both unset = suggestions off. `SourcesOptionsValidator` fails startup when exactly one is set: `Sources:IgdbClientId and Sources:IgdbClientSecret must both be set or both be empty.` No format checks beyond that.
- **S2** The secret is never shown or logged: `EffectiveSettings` (startup log, **Admin → Settings**, `settings.json` in the logs ZIP) reports `sources.igdbClientSecret` as `***` when set and `""` when empty, as `adminInitialPassword` does. `sources.igdbClientId` is shown as is.

### IGDB client (Core)
- **S3** Access token: Twitch client-credentials grant, `POST https://id.twitch.tv/oauth2/token` with a form body (`client_id`, `client_secret`, `grant_type=client_credentials`); never in the query string, because `RequestLoggingHandler` logs request URLs. The token is cached in memory (singleton) until `expires_in` minus 1 hour (minus half its lifetime for a token that lives an hour or less), and only one token request runs at a time (`SemaphoreSlim`). A `401` from IGDB drops the cached token and retries the IGDB request once with a fresh token.
- **S4** Search: `POST https://api.igdb.com/v4/games` with headers `Client-ID: <id>` and `Authorization: Bearer <token>`, body (Apicalypse):
  `search "<title>"; fields name,cover.image_id,cover.width,cover.height,screenshots.image_id,screenshots.width,screenshots.height; where platforms = (<platform>); limit 5;`
  `<title>` is the game's `GameRow.DisplayTitle` (the form Google Images searches on, not the sort-form `Title`) with `"` and `\` removed. If that returns no games, the same search runs once more without the `where` clause. IGDB's own ranking is kept.
- **S3a** Errors and cancellation, the rule `MediaService` already uses: when `ct.IsCancellationRequested` (the browser aborted: dialog closed or type changed), cancellation propagates and nothing is reported or logged as a failure. Any other exception from the token or search request (Core has no Polly reference, so the shared policy's timeout arrives as an arbitrary exception) is caught with `catch (Exception) when (!ct.IsCancellationRequested)` and becomes `IGDB couldn't be reached.`, logged at `Warn` with the exception type and message only. That general catch covers sending and receiving only. Parsing the token or search response is handled first: a `JsonException`, a token response without a string `access_token` and a positive integer `expires_in`, or a search response that isn't a JSON array becomes `IGDB returned an unreadable response.`
- **S5** Platform: `IgdbPlatforms.ById` (new, `Core/Media/IgdbPlatforms.cs`) maps every registered system id to its IGDB platform id. The implementer checks the values below against IGDB's public platform list (api-docs.igdb.com / igdb.com platform pages), or with a one-off `/v4/platforms` query if the user provides credentials, and fixes any that are wrong in step 1a's commit. If neither check is possible, the table stands and the step report says so:

  | System id | IGDB | System id | IGDB | System id | IGDB |
  |---|---|---|---|---|---|
  | `a2600` | 59 | `gba` | 24 | `s32x` | 30 |
  | `a5200` | 66 | `gc` | 21 | `saturn` | 32 |
  | `a7800` | 60 | `gg` | 35 | `segacd` | 78 |
  | `amiga` | 16 | `jaguar` | 62 | `sms` | 64 |
  | `c64` | 15 | `lynx` | 61 | `snes` | 19 |
  | `coleco` | 68 | `md` | 29 | `switch` | 130 |
  | `fds` | 51 | `n64` | 4 | `tg16` | 86 |
  | `gb` | 33 | `nds` | 20 | `vb` | 87 |
  | `gbc` | 22 | `nes` | 18 | `vic20` | 71 |
  | `3ds` | 37 | `pet` | 90 | `wii` | 5 |
  | `3do` | 50 | `psp` | 38 | `x360` | 12 |
  | `psx` | 7 | | | | |

  A system missing from the map searches without the `where` clause; a test (S16) keeps the map complete.
- **S6** Suggestions, at most 5, in IGDB's order:
  - `BoxArt`: the `cover` of each returned game that has one (one per game).
  - `Snap`: the `screenshots` of the first returned game that has any, in IGDB's order, first 5.
  Each suggestion is `{ previewUrl, imageUrl, gameName, width, height }`:
  - `previewUrl`: `https://images.igdb.com/igdb/image/upload/t_cover_big/<image_id>.jpg` (box art) or `…/t_screenshot_med/<image_id>.jpg` (screenshot).
  - `imageUrl`: `https://images.igdb.com/igdb/image/upload/t_1080p/<image_id>.jpg` (JPEG, well under the 8 MiB limit).
  - `gameName`: the IGDB game's `name`; `width`/`height`: the size of the `t_1080p` image that will be stored, worked out from IGDB's original dimensions as `round(w × s)` × `round(h × s)` with `s = min(1, 1920 / w, 1080 / h)` (`t_1080p` scales down to fit 1920×1080 and never up); both `null` when IGDB gives no dimensions.
  An `image_id` that isn't `[A-Za-z0-9]+` is skipped, so nothing from IGDB is put into a URL unchecked.
- **S7** Outbound HTTP: a named client `HttpClients.Igdb = "igdb"` (new) registered with `Configure` (User-Agent, request logging, 20 s per attempt, 3 retries on transient errors) as a typed client for `IgdbClient`. It never follows redirects (`SocketsHttpHandler { AllowAutoRedirect = false }`, as `TitleDbImageClient` does), and talks to `id.twitch.tv` and `api.igdb.com` only (hard-coded constants, not configuration).

### API
- **S8** `GET /api/games/{id:guid}/image-suggestions?type=BoxArt|Snap` (Admin) on the `games` group in `LibraryEndpoints.cs`. Read-only: no job lock, no audit row, no antiforgery header (a GET). In order:
  1. `400 ValidationFailed` on `type` when it isn't exactly `BoxArt` or `Snap`: `Choose BoxArt or Snap.` (the upload's message).
  2. `404` for an unknown game.
  3. `409 IgdbNotConfigured`, title `Image suggestions need Sources:IgdbClientId and Sources:IgdbClientSecret.`, when the keys are empty (nothing is sent).
  4. `502 IgdbFailed` when the token or search fails, title by cause: `IGDB couldn't be reached.` (connection error or timeout after retries), `IGDB refused the Client ID or secret.` (token request `400`/`401`/`403`, or IGDB `401` after the fresh-token retry), `IGDB is busy; try again in a moment.` (`429`), `IGDB returned HTTP <code>.` (anything else non-2xx), `IGDB returned an unreadable response.` (JSON that doesn't parse).
  5. `200 { suggestions: [...] }` (S6), possibly empty.
- **S9** CSP: the SPA policy's `img-src` becomes `'self' data: blob: https://images.igdb.com`. The emulator policies are unchanged. `Referrer-Policy: no-referrer` already keeps the game page URL from IGDB.
- **S10** Adding a suggestion is the existing `POST /api/games/{id}/images/from-url` with `{ url: imageUrl, type }`, unchanged: same validation, SSRF checks, `DuplicateImage`, `TooManyImages`, `JobRunning`, placeholder replacement, audit row (`<fileName> from <url>`) and `sourceUrl`.

### SPA (Admin only)
- **S11** Button: in `GameImages.svelte`'s top row, a new `<button>` **Suggest Images** with `<Icon name="suggest-images" />` (new icon, `bi bi-magic`), class `btn btn-outline-secondary btn-sm ms-auto`, placed before **Google Images**. `ms-auto` moves from Google Images to it, so the row is `[Suggest Images, Google Images, Upload Image]`, right-aligned. Site users don't see it.
- **S12** Dialog: a `ConfirmModal` titled **Suggest Images**, `size="lg"`, with no `onconfirm` (only its **Close** button). Inside, in order:
  1. A Box art / Screenshot radio group (`aria-label="Image type"`, same markup as the upload dialog's, ids `suggest-type-boxart` / `suggest-type-snap`), Box art checked on every open.
  2. The results area, a `<div aria-live="polite">`, so arriving results, the empty text and errors are announced. Opening the dialog loads Box art; changing the type loads that type. Each load gets a sequence number, and a load response that isn't from the latest load (an earlier type, or an earlier opening of the dialog) is ignored; the load request is aborted (`AbortController`) when the dialog closes or the type changes.
     - Loading: the `Loading` component.
     - Results: a `row row-cols-2 row-cols-md-5 g-2` grid; each tile sits in a `<div class="col">`, as in the existing image grid. Each tile is a `<button type="button" class="btn p-0 border card h-100 w-100">` with `aria-label="Add <gameName> <box art|screenshot> <n>"` (n = 1-based position), holding the preview `<img>` (`alt=""`, `loading="lazy"`, `object-fit-contain`, height 8rem) and under it, `small`, the game name (one line, ellipsis, `title` = full name) and `<width>×<height>` when known.
     - Empty: `IGDB has no box art for this game.` / `IGDB has no screenshots for this game.`
     - `409 IgdbNotConfigured`: an `alert alert-info` with `Image suggestions need an IGDB Client ID and secret. Set Sources:IgdbClientId and Sources:IgdbClientSecret and restart.`
     - Any other error: `ErrorAlert` with **Retry** reloading the current type.
  3. `form-text`: `Suggestions from IGDB.com. Click one to add it to the game.`
- **S13** Clicking a tile posts `from-url` (S10) with that tile's `imageUrl` and the dialog's current type. While it runs every tile and the type radios are disabled and the clicked tile shows a small spinner (`spinner-border spinner-border-sm`). `busy` is *not* passed to the `ConfirmModal`, so **Close** and × stay enabled; the add isn't aborted on close (the server finishes it), and its result still calls `onchanged` and toasts, but it only marks a tile when the dialog is still on the same opening and type. On success: `onchanged(detail)`, toast `Image added from IGDB.` (success), and the tile stays visible but disabled with a `badge text-bg-success` **Added** over its top right, its `aria-label` becoming `Added <gameName> <box art|screenshot> <n>`; the dialog stays open. On failure the existing `failed()` handles `JobRunning`, `DuplicateImage`, `TooManyImages`; `ImageDownloadFailed` / `ImageUrlBlocked` / `ValidationFailed` toast their `title` / first `url` error (danger). Added marks reset when the type changes or the dialog reopens.

### Docs and tests
- **S14** `docs/configuration.md`: two `Sources:Igdb*` rows (both-or-neither; how to get a Twitch app: dev.twitch.tv console, client type Confidential), and the secret listed as masked under Logs. `docs/features.md`: the Images tab paragraph (line ~161) gets Suggest Images; Edit the library through the API (section at ~529, next to the `from-url` paragraph at ~545) gets the endpoint and its errors; Admin settings (~634) notes `sources.igdbClientSecret` is masked.
- **S15** Existing tests that change: `game-images.test.ts` "Google Images and Upload Image sit right-aligned…" expects `[suggest, google, upload]` with `ms-auto` on Suggest Images only; `SecurityHeadersTests` SPA policy string gains `https://images.igdb.com`; `EffectiveSettingsTests` gains the secret masking; `AdminEndpointsTests`' secret-leak checks (settings endpoint, line ~51, and the logs ZIP's `settings.json`, ~105) add `Sources:IgdbClientSecret` to the factory settings and assert the value never appears and `igdbClientSecret` is `"***"`.
- **S16** `IgdbPlatformsTests` (new, Api.Tests next to `SupportedSystemsTableTests`, since Core.Tests doesn't load the system handlers) fails when a registered system has no entry or an entry names an unregistered system.

## Changes
| Area | File(s) | Change |
|---|---|---|
| Core | `src/RnRomManager.Core/Configuration/SourcesOptions.cs`, `SourcesOptionsValidator.cs` | S1 keys and both-or-neither rule |
| Core | `src/RnRomManager.Core/Media/IgdbPlatforms.cs` (new) | S5 map |
| Core | `src/RnRomManager.Core/Media/IgdbClient.cs` (new) | `IImageSuggestions` (new interface: `bool Configured`, `Task<ImageSuggestionResult> SuggestAsync(string title, string systemId, ImageType type, CancellationToken)`), `IgdbClient` implementation (S3–S6), `ImageSuggestion` record, `ImageSuggestionResult(IReadOnlyList<ImageSuggestion>? Suggestions, string? FailedTitle)` |
| Api | `src/RnRomManager.Api/Http/HttpClients.cs` | S7 client; `IImageSuggestions` → `IgdbClient` (token cache in a singleton `IgdbTokenCache` (new, same file as `IgdbClient`), since typed clients are transient) |
| Api | `src/RnRomManager.Api/Configuration/EffectiveSettings.cs` | S2: pass a copy of `SourcesOptions` with the secret masked |
| Api | `src/RnRomManager.Api/Endpoints/LibraryEndpoints.cs` | S8 route |
| Api | `src/RnRomManager.Api/Security/SecurityHeaders.cs` | S9 |
| Web | `src/RnRomManager.Web/src/lib/api/types.ts` | `ImageSuggestion`, `ImageSuggestions` types |
| Web | `src/RnRomManager.Web/src/lib/components/icons.ts` | `'suggest-images': 'bi bi-magic'` |
| Web | `src/RnRomManager.Web/src/lib/components/GameImages.svelte` | S11–S13 |
| Docs | `docs/configuration.md`, `docs/features.md` | S14 |
| e2e | `tests/e2e/specs/mvp.spec.ts` | S17 below |

No migration and no data change. New config keys default empty, so existing installs see no difference except the new button (which explains the missing keys). API contract: one new route; nothing existing changes.

- **S17** e2e (the e2e stack has no IGDB keys): in the existing image flow of `mvp.spec.ts`, click **Suggest Images**, expect the dialog named **Suggest Images**, the not-configured alert text (S12), `expectAccessible(page, "suggest images modal")`, then **Close**.

## Risks
- **Secret leakage:** the secret travels only in the token request body (S3), is masked in `EffectiveSettings` (S2), and is never in an error title or log message. `IgdbClientTests` asserts the token request URL has no query string.
- **CSP widening:** `img-src` gains one HTTPS host; images can't run script, and the previews load only after an Admin opens the dialog.
- **SSRF:** suggestion URLs are built from checked `image_id`s on a fixed host (S6), and adding still goes through `ImageUrlDownloader`'s public-address checks.
- **IGDB terms/rate limits:** 4 requests/s per app; one Admin clicking stays far below it. A `429` is reported, not retried beyond the shared policy. The dialog credits IGDB (S12).
- **Wrong matches:** IGDB search is fuzzy; the Admin sees the game name on each tile before adding.

## Steps
- [x] **1a. Config, masking and platform map**: S1, S2, S5. Files: `SourcesOptions.cs`, `SourcesOptionsValidator.cs`, `Media/IgdbPlatforms.cs` (new), `Api/Configuration/EffectiveSettings.cs`. Tests: `OptionsValidatorTests` (only id set, only secret set, whitespace-only counts as unset → S1 message or ok as appropriate; both/neither → ok), `OptionsRegistrationTests` (a `[TestCase("Sources:IgdbClientId", "abc")]`-style startup-failure case), `EffectiveSettingsTests` (secret `***`/`""`, id shown), `AdminEndpointsTests` (S15), `IgdbPlatformsTests` (new, Api.Tests). Done when: those pass and the platform-id check (S5) is reported.
- [x] **1b. IGDB client**: S3, S3a, S4, S6, S7. Files: `Media/IgdbClient.cs` (new), `Api/Http/HttpClients.cs`. Tests: `IgdbClientTests` (new, Core.Tests, fake `HttpMessageHandler`, so no test reaches the network: token form body and no query string, token reused until expiry, concurrent calls share one token request, 401 → one refresh and retry, Apicalypse body with the display title stripped of `"`/`\` and the platform, no-result fallback without `where`, BoxArt one cover per game, Snap first game's screenshots, cap of 5, bad `image_id` skipped, `t_1080p` size maths, each S8 failure title, a non-JSON body and a wrong-shape JSON body (token and search) → `unreadable response`, a thrown exception → `couldn't be reached`, a cancelled token → `OperationCanceledException` and no failure result), `HttpClientsTests` (the igdb client resolves and has no auto-redirect). Done when: those pass.
- [x] **2. Suggestions endpoint and CSP**: S8, S9. Files: `LibraryEndpoints.cs`, `SecurityHeaders.cs`. Tests: `ImageSuggestionEndpointsTests` (new, Integration, `IImageSuggestions` replaced through `ApiFactory.TestServices` with NSubstitute: 400/404/409/502/200 in S8 order, 404 sends nothing, 409 sends nothing), a row `["GET /api/games/{id:guid}/image-suggestions"] = Role.Admin` in `EndpointRoleMatrixTests`, `SecurityHeadersTests` (S15). Done when: those pass.
- [ ] **3. Suggest Images dialog**: S11–S13. Files: `types.ts`, `icons.ts`, `GameImages.svelte`. Tests: `src/routes/games/game-images.test.ts` (S15 layout change; Site user sees no Suggest Images; open loads `?type=BoxArt`; tiles with labels, name, dimensions; type switch loads Snap and ignores a late BoxArt response; close aborts the load and a late response after reopen is ignored; results area is `aria-live="polite"`; empty texts; not-configured alert; ErrorAlert retry; click posts `from-url` with the tile's `imageUrl` and type, calls `onchanged`, toasts, marks Added (label `Added …`) and keeps the dialog open; tiles and radios disabled while adding but **Close** enabled; an add finishing after close/reopen still calls `onchanged` and toasts but marks no tile; Added marks reset on type change and reopen; `DuplicateImage` toast). Done when: those pass with `npm run lint` and `npm run check`.
- [ ] **4. Docs and e2e**: S14, S17. Files: `docs/configuration.md`, `docs/features.md`, `tests/e2e/specs/mvp.spec.ts`. Tests: the e2e flow (Finish). Done when: `configuration.md` has the two `Sources:Igdb*` rows and the masked secret under Logs (line ~124); `features.md` has Suggest Images in the Images tab paragraph, the S8 endpoint with every error in Edit the library through the API, and the masked secret in Admin settings; the e2e flow passes in `scripts/test-e2e.ps1`.

## Decisions
- 2026-11-20 Image source → IGDB, with Twitch developer credentials (user)
- 2026-11-20 Process → spec first (user)
- 2026-11-20 Where credentials live → `Sources:IgdbClientId` / `Sources:IgdbClientSecret` config, secret masked, not in the DB (user)
- 2026-11-20 Which five images → Box art: covers of the 5 best matches; Screenshot: up to 5 screenshots of the best match (user)
- 2026-11-20 How previews load → allow `https://images.igdb.com` in the SPA `img-src` (user)
- 2026-11-20 After a pick → dialog stays open, tile marked Added (user)
- 2026-11-20 Image types offered → Box art and Screenshot only, as the upload/`from-url` APIs accept (code)
- 2026-11-20 Review round 1: search on `DisplayTitle`; MediaService-style error/cancellation rule; step 1 split into 1a/1b; "set" = non-empty after trim; tile shows the stored `t_1080p` size; Close stays enabled during an add, stale responses ignored, Added label; live region and `col` wrappers; AdminEndpointsTests leak checks; concrete doc checklist (reviewer)
- 2026-11-20 Review round 2: parse failures → `unreadable response` before the general catch; step 3 tests cover the round-1 dialog rules (reviewer)
- 2026-11-20 Token margin when `expires_in` is an hour or less → half the lifetime instead of an hour, so such a token is still reused (code)

## Kickoff prompt
```text
/spec implement 2026-11-20-igdb-image-suggestions.md
```
Starts the spec, or resumes it at the first unticked step.
