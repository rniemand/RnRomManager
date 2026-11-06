# Library ZIP format

Every game in the library is one ZIP file. The ZIP is the **source of truth**: the database only mirrors its contents for browsing and search, and the integrity checker rebuilds the database from the ZIPs. You can copy, back up or inspect the library with any ZIP tool.

## Layout

```
roms/
  gb/
    #/                  names that don't start with A–Z (digits, symbols, non-ASCII)
    A/ … Z/
      Tetris.zip
      Legend of Zelda, The - Link's Awakening.zip     (in L/)
  gbc/                  GBC-only and dual-mode carts
  gba/                  Game Boy Advance
```

Inside a game's ZIP:

```
Tetris.zip
  info.json
  roms/
    Tetris (World) (Rev 1).gb
    Tetris (Japan) (En).gb
  images/
    boxart-01.png
    snap-01.png
```

- The system folder comes from the ROM's header, not the ingest folder: a GBC cart dropped into `ingest/gb/` is stored under `roms/gbc/`.
- The letter folder is the first character of the ZIP file name, upper-cased if it's an ASCII letter (`seaQuest DSV.zip` → `S/`), otherwise `#`. ZIP names keep the No-Intro article form, so `Legend of Zelda, The - …` is in `L/`.
- File names compare case-insensitively everywhere (clash checks, integrity), so the library stays portable to Windows and macOS.

## Naming

| Item | Rule |
|---|---|
| Base title | The DAT game name up to its region group: the first ` (…)` group whose comma-separated parts are all known No-Intro regions. Earlier groups belong to the title (`Yelu Wangzi (Prince Yeh Rude) (Taiwan)` → `Yelu Wangzi (Prince Yeh Rude)`). With no region group, up to the first ` (`. Releases with the same system and base title are grouped into one game. |
| Tags | Regions, languages, revision and flags are parsed from the DAT **ROM name** (it can carry more tags than the game name, e.g. `(Aftermarket) (Unl)`). |
| Game `title` | The base title of the release that created the game. Admins can edit it (1–255 characters after trimming, must not sanitize to an empty name); it never changes on its own. |
| `displayTitle` | `title` with a trailing article moved to the front: `Maus, Die` → `Die Maus`, `Legend of Zelda, The - Link's Awakening` → `The Legend of Zelda - Link's Awakening`. Articles: The, A, An, Die, Der, Das, Le, La, Les, El, Il. |
| ZIP name | `Sanitize(title) + ".zip"`. A different game already at that path (case-insensitive) → ` (2)`, ` (3)`, … |
| ROM `fileName` | The DAT ROM name for DAT-matched releases; review resolutions build their own (see [features](features.md)). An entry-name clash inside a ZIP appends ` (2)`, … before the extension. |
| Images | `images/boxart-NN.<ext>` and `images/snap-NN.<ext>`: `NN` is the lowest free number 01–99 for that prefix (compared ignoring case and extension), `<ext>` is `png` or `jpg` from the file's magic bytes. Fetched art is `boxart-01.png` / `snap-01.png` unless taken. At most 20 images per game, each at most 8 MiB (libretro thumbnails are fetched only up to 5 MiB). |
| Sanitize | Unicode NFC; `\ / : * ? " < > \|` and control characters become `_`; leading/trailing spaces and trailing dots trimmed; at most 180 characters; Windows reserved names (`CON`, `NUL`, …) get a `_` prefix. |

All releases of a game (regions, revisions) **and** its hacks and translations live in the same ZIP. Hacks and translations are flagged (`Hack`, `Translated`) and are never the primary release.

**Primary release** (exactly one per game, recomputed on every write), ranked by: not Hack/Translated → no status flag (`Beta…`, `Proto…`, `Possible Proto`, `Demo`, `Sample`, `Pirate`, `Unverified`) → region `World` > `USA` > `Europe` > other with English > `Japan` > other → highest revision → `fileName` (ordinal).

**Default box art:** when no box art is found (or media fetch is disabled), each system's own 512×512 placeholder is stored as `boxart-01.png` with `isDefault: true`.

**Cover:** exactly one image of a game that has images is its cover (`isCover: true`): what the library and game page show. Any image can be the cover. A new game's cover is its box art (or placeholder); the first image added to a game with no images becomes the cover; ingestion and media retry only ever replace the placeholder (keeping its cover status) or add a missing snap, never touching other images. A merge keeps the target's cover, copies the source's non-default images in under new names, drops the source's placeholder, removes the target's placeholder when the source brings a real box art (that box art becomes the cover if the placeholder was), and is refused (`409 TooManyImages`) above 20 images.

## Writes

- Every write starts from the ZIP's own `info.json` and entries, never from database rows.
- Writes are atomic: the new ZIP is streamed to `<name>.zip.tmp` next to the target, re-opened, every ROM entry's SHA1 checked against `info.json`, then moved over the old file. A ZIP is never edited in place. When the path changes (a title edit), the new ZIP is written and the database committed before the old ZIP is deleted.
- Compression: Deflate, optimal level. `info.json` is UTF-8 without a BOM, indented, camelCase, with nulls written.

## `info.json` (schema version 2)

Hashes are lowercase hex everywhere. Times are ISO-8601 UTC.

### Game

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | int | ✓ | `3` |
| `gameId` | GUID string | ✓ | stable; the database's primary key |
| `system` | string | ✓ | system id (`gb`, `gbc`, `gba`) |
| `title` | string | ✓ | see Naming |
| `displayTitle` | string | ✓ | see Naming |
| `releaseYear` | int? | | from the DAT metadata |
| `developers` | string[] | ✓ | may be empty; distinct, in DAT order |
| `publishers` | string[] | ✓ | as above |
| `genres` | string[] | ✓ | may be empty |
| `description` | string? | | Admin-edited only |
| `rating` | int? | ✓ | shared 1–5 star rating; `null` = not rated (the key must be present) |
| `favourite` | bool | ✓ | shared favourite flag |
| `roms` | Rom[] | ✓ | at least one |
| `images` | Image[] | ✓ | at most 20; exactly one `isCover` when not empty |
| `metadata.createdAt` / `updatedAt` | string | ✓ | first / latest write of the ZIP |
| `metadata.appVersion` | string | ✓ | app version that last wrote the file |

### Rom

| Field | Type | Required | Notes |
|---|---|---|---|
| `fileName` | string | ✓ | entry name under `roms/` |
| `originalFileName` | string | ✓ | as found in ingest (the archive entry name if it came from an archive) |
| `baseTitle` | string | ✓ | grouping key (see Naming) |
| `regions` | string[] | ✓ | may be empty |
| `languages` | string[] | ✓ | may be empty |
| `revision` | string? | | `Rev 1`, `v1.1`, … |
| `flags` | string[] | ✓ | DAT tags (`SGB Enhanced`, `Proto`, `Unl`, …) plus `Hack` / `Translated` / `Unverified` |
| `isPrimary` | bool | ✓ | |
| `identification.method` | `Dat` \| `Manual` | ✓ | `Dat` only for an exact SHA1 match |
| `identification.datName` | string? | | DAT header name, e.g. `Nintendo - Game Boy`; also set for an `Unverified` release (the entry it was based on) |
| `identification.datVersion` | string? | | DAT header version, e.g. `2026.10.07` |
| `identification.datGameName` | string? | | DAT `game.name` (the thumbnail key; can differ from the ROM name) |
| `header` | object? | | system-specific header fields (below); null when unreadable |
| `metadata.sizeBytes` | long | ✓ | |
| `metadata.crc32` / `md5` / `sha1` | string | ✓ | |
| `metadata.dateAdded` / `dateModified` | string | ✓ | `dateModified` = the last time the entry's bytes were (re)written |
| `metadata.sourcePath` | string | ✓ | ingest path at ingestion (the archive's path for an archive entry) |

Game Boy / Game Boy Color `header`: `title`, `manufacturerCode`, `cgbFlag` and `sgbFlag` (hex strings), `cartridgeType`, `romSizeBytes` (null if the header can't express it), `ramSizeBytes`, `destination` (`Japan` or `Overseas`), `licensee`, `version`, `logoValid`, `headerChecksumValid`, `globalChecksumValid`, `sizeMatchesHeader`, `isMbc1Multicart`.

Game Boy Advance `header`: `title` (12 chars at 0xA0), `gameCode` (4 chars at 0xAC, null unless A–Z/0–9), `destination` (from the game code's 4th char: `Japan`, `USA`, `Europe`, `Germany`, `France`, `Italy`, `Spain`, `Netherlands`, `Korea` or `Unknown (X)`), `makerCode` (2 chars at 0xB0, null unless A–Z/0–9), `maker` (the GB new-licensee name, or `Unknown (0xHHHH)`), `unitCode` and `deviceType` (hex strings), `version`, `fixedValueValid` (0xB2 is 0x96), `logoValid`, `headerChecksumValid` (the 0xBD complement check), `romSizeBytes` (the file length; GBA headers store no size).

### Image

| Field | Type | Required | Notes |
|---|---|---|---|
| `fileName` | string | ✓ | entry name under `images/` |
| `type` | `BoxArt` \| `Snap` \| `TitleScreen` \| `Other` | ✓ | only BoxArt and Snap are fetched |
| `source` | `libretro-thumbnails` \| `default` \| `upload` | ✓ | `upload` from v3 |
| `sourceUrl` | string? | | |
| `isDefault` | bool | ✓ | the system placeholder; at most one, and it must be a `BoxArt` |
| `isCover` | bool | ✓ | v3; exactly one per game with images |
| `sizeBytes` | long | ✓ | entry size |
| `sha1` | string | ✓ | |
| `width` / `height` | int | ✓ | read from the PNG header or JPEG frame header |
| `dateAdded` | string | ✓ | |

### Validation

Checked on every read (after upgrading an older version): valid UTF-8 JSON of at most 1 MiB; every required field present with the right type; `gameId` a GUID; `system` a registered system; `rating` null or 1–5; at least one ROM and exactly one `isPrimary`; file names unique (case-insensitively) within `roms` and within `images`, and free of `/`, `\` and `..`; hashes lowercase hex of the right length; `source` one of the three values; at most 20 images, exactly one of them `isCover` (a game with no images is valid and reported as `MissingBoxArt`); at most one `isDefault` image, and it is a `BoxArt`. Unknown properties are ignored on read and dropped on the next write. A failing file is reported by the integrity checker as `InfoJsonInvalid`, naming the first rule it breaks, and is left alone.

## Versions

Readers accept any `schemaVersion` up to the current one and upgrade older files in memory through a chain of upgraders (v*N* → v*N*+1); the file is rewritten in the new version on its next normal write. A file with a **newer** `schemaVersion` than the app knows is read-only: integrity reports `InfoJsonNewerSchema`, and ingestion and edits refuse to touch that ZIP (`409 InfoJsonNewerSchema`).

A schema change bumps `schemaVersion`, adds an upgrader and adds a row here.

| Version | App version | Changes |
|---|---|---|
| 1 | 0.1.0 | Initial format. |
| 2 | 0.2.0 | Adds `rating` (int?, null) and `favourite` (bool, false); the v1→v2 upgrader adds both with those defaults. App 0.1.0 treats a v2 file as `InfoJsonNewerSchema` (read-only). |
| 3 | 0.3.0 | Adds `isCover` (bool) to every image and `upload` as an image `source`; image names may end in `.jpg`; drops the one-`BoxArt` / one-`Snap` limits (at most 20 images). The v2→v3 upgrader makes the `BoxArt` image the cover, else the first image, else none. Integrity's `MissingBoxArt` / `DefaultBoxArt` now mean "no cover" / "the cover is the placeholder". Apps before 0.3.0 treat a v3 file as `InfoJsonNewerSchema` (read-only). |
