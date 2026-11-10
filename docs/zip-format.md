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
  n64/                  Nintendo 64 (always stored big-endian, as .z64)
  nes/                  Nintendo Entertainment System (headered .nes, header included)
  snes/                 Super Nintendo Entertainment System (stored as .sfc, copier header stripped)
  a2600/                Atari 2600 (.a26 or .bin, as the DAT names it)
  coleco/               ColecoVision (.col)
  gg/                   Game Gear (.gg, or .sms for SMS-mode games, as the DAT names it)
  sms/                  Master System (.sms)
  md/                   Mega Drive / Genesis (.md, or .bin for the one lock-on entry, as the DAT names it)
  s32x/                 32X (.32x)
  tg16/                 TurboGrafx-16 / PC Engine (.pce)
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
  notes/
    Tetris (World) (Rev 1).gb.md
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

## Release notes

A release can have one Markdown note, the entry `notes/<rom fileName>.md` (e.g. `notes/Tetris (World) (Rev 1).gb.md`): UTF-8 without a BOM, `\n` line endings, at most 64 KiB (65,536 bytes). A release has a note exactly when that entry exists (same ordinal name match as every entry lookup); notes aren't listed in `info.json`, so adding one needs no schema change.

- **Every write keeps notes:** for each ROM listed in the new `info.json`, its note is copied from the old ZIP unless the write replaces or removes it. A note whose ROM is no longer listed is dropped, so deleting a release (or the game) deletes its note. A copied note over 64 KiB fails the write's verification.
- **Notes follow their release:** moving a release to another or a new game, or merging a game, carries each note into the target under the release's final file name (after a ` (2)` clash rename) and drops it from the source. A release that arrives in a ZIP without a note (move, merge, ingest, review resolution) never adopts an orphan `notes/<its name>.md` already there: the orphan is removed.
- **Integrity:** the `EntryUnlisted` check ignores `notes/<f>.md` for every listed ROM `f`. Any other `notes/` entry is an orphan, reported as `EntryUnlisted` (Warn) and left alone.
- **Downgrades drop notes:** an app version older than this one copies only the entries it lists, so its next write of a ZIP silently drops `notes/`. Back up before downgrading.

## Writes

- Every write starts from the ZIP's own `info.json` and entries, never from database rows.
- Writes are atomic: the new ZIP is streamed to `<name>.zip.tmp` next to the target, re-opened, every ROM entry's SHA1 checked against `info.json`, then moved over the old file. A ZIP is never edited in place. When the path changes (a title edit), the new ZIP is written and the database committed before the old ZIP is deleted.
- Release notes of listed ROMs are carried into the new ZIP (see [Release notes](#release-notes)).
- Entry modified times are stored as ZIP (DOS) times: the writer's local time at 2-second precision, with no time zone. The Zip Contents tab and `GET /api/games/{id}/zip-entries` read them as server local time, so they're exact only while the server's time zone is unchanged.
- Compression: Deflate, optimal level. `info.json` is UTF-8 without a BOM, indented, camelCase, with nulls written.

## `info.json` (schema version 2)

Hashes are lowercase hex everywhere. Times are ISO-8601 UTC.

### Game

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | int | ✓ | `3` |
| `gameId` | GUID string | ✓ | stable; the database's primary key |
| `system` | string | ✓ | system id (`gb`, `gbc`, `gba`, `n64`, `nes`, `snes`, `a2600`, `coleco`, `gg`, `sms`, `md`, `s32x`, `tg16`) |
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

Nintendo 64 `header`, read after the ROM is converted to big-endian (so it can't tell which byte order the dump came in): `title` (20 chars at 0x20), `gameCode` (4 chars at 0x3B, null unless A–Z/0–9), `mediaType` (from the game code's 1st char: `Cartridge`, `Cartridge (expandable)`, `64DD disk`, `64DD expansion`, `Aleck64` or `Unknown (X)`), `destination` (from its 4th char: `Asia`, `Brazil`, `China`, `Germany`, `USA`, `France`, `Netherlands`, `Italy`, `Japan`, `Korea`, `Canada`, `Europe`, `Spain`, `Australia`, `Scandinavia` or `Unknown (X)`), `version` (0x3F), `crc1` and `crc2` (0x10 and 0x14) and `bootAddress` (0x08) as `0xHHHHHHHH`, `headerValid` (byte 0 is 0x80), `romSizeBytes` (the file length).

Nintendo Entertainment System `header`, from the 16-byte iNES / NES 2.0 header (the ROM is stored and hashed with it): `headerValid` (bytes 0–3 are `NES` + 0x1A), `format` (`NES 2.0` when byte 7 bits 2–3 are `10`, else `iNES`), `mapper` (byte 6 high nibble, byte 7 high nibble, and for NES 2.0 byte 8's low nibble as bits 8–11), `prgRomSizeBytes` (byte 4 × 16 KiB; NES 2.0 adds byte 9's low nibble as the high byte, null in the exponent form), `chrRomSizeBytes` (byte 5 × 8 KiB; NES 2.0 adds byte 9's high nibble, null in the exponent form), `mirroring` (`Four-screen`, `Vertical` or `Horizontal`), `battery`, `trainer`, `tvSystem` (iNES: `NTSC` or `PAL` from byte 9; NES 2.0: `NTSC`, `PAL`, `Multi-region` or `Dendy` from byte 12), `romSizeBytes` (the file length). Without the magic only `headerValid` (false) and `romSizeBytes` are set; the rest are null. iNES has no title.

Super Nintendo Entertainment System `header`, read after a copier header is stripped (so it doesn't say whether the dump had one): the internal header is looked for as LoROM at 0x7FC0, HiROM at 0xFFC0 and ExHiROM at 0x40FFC0 (ExHiROM first for ROMs over 4 MiB); the first location with a valid checksum/complement pair wins, else the first with a printable 21-byte title and a map mode that fits the location. `headerValid` (a location was chosen), `title` (21 chars, ASCII up to the first NUL, other bytes as `?`), `mapping` (`LoROM`, `HiROM` or `ExHiROM`), `fastRom` (map mode bit 0x10), `romType` (the chipset byte, as a number), `romSizeKiB` (2^byte, null when 0 or over 13), `sramSizeKiB` (0, or 2^byte up to 10, else null), `region` (`Japan`, `USA`, `Europe`, `Sweden`, `Finland`, `Denmark`, `France`, `Netherlands`, `Spain`, `Germany`, `Italy`, `China`, `Indonesia`, `Korea`, `Canada`, `Brazil`, `Australia`, else null), `version`, `checksum` (4 upper-case hex digits), `checksumValid`, `romSizeBytes` (the stored length). With no location chosen only `headerValid` (false) and `romSizeBytes` are set; the rest are null.

Atari 2600 cartridges have no header: `header` is always null.

ColecoVision `header`: `magic`, the first two bytes as upper-case hex (`AA55` shows the title screen, `55AA` skips it; anything else is not a valid header, empty for an empty file).

Game Gear `header`, from the optional `TMR SEGA` header (looked for at 0x7FF0, 0x3FF0, then 0x1FF0; without it `header` is null): `offset` (`7FF0`, `3FF0` or `1FF0`), `checksum` (bytes +10–11, little-endian, 4 upper-case hex digits), `productCode` (the BCD product code as hex digits, leading zeros trimmed), `version` (low nibble of +14), `region` (`SMS Japan`, `SMS Export`, `GG Japan`, `GG Export`, `GG International`, or the nibble in hex) and `sizeCode` (low nibble of +15). Master System `header` has the same fields, read the same way.

Mega Drive / Genesis `header`, present only when the 16-byte field at 0x100 starts with `SEGA` (without it `header` is null). Text fields are ASCII with every byte outside 0x20–0x7E turned into a space, runs of spaces collapsed and trimmed (empty → null): `systemType` (0x100, 16 bytes), `copyright` (0x110, 16), `domesticTitle` (0x120, 48), `overseasTitle` (0x150, 48), `serial` (0x180, 14), `region` (0x1F0, 3); `checksum` is bytes 0x18E–0x18F, big-endian, 4 upper-case hex digits (stored, not verified).

32X `header`: the same fields and rules as Mega Drive / Genesis (the `SEGA` header at 0x100, system type such as `SEGA 32X`); present only when that field starts with `SEGA`.

TurboGrafx-16 `header`: always null (HuCards have no header).

### Image

| Field | Type | Required | Notes |
|---|---|---|---|
| `fileName` | string | ✓ | entry name under `images/` |
| `type` | `BoxArt` \| `Snap` \| `TitleScreen` \| `Other` | ✓ | only BoxArt and Snap are fetched |
| `source` | `libretro-thumbnails` \| `default` \| `upload` | ✓ | `upload` from v3 |
| `sourceUrl` | string? | | the URL a `libretro-thumbnails` image came from, or the URL an `upload` was downloaded from (Upload image → URL); null for a file upload and the placeholder |
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
