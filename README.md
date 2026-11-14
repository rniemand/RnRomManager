# RnRomManager

A self-hosted web app for managing a personal backup collection of ROMs across [supported systems](#supported-systems). Switch dumps and disc images are stored as files next to each game's ZIP, since they're too large to pack; a PlayStation or Saturn bin/cue set is kept together as one release.

Drop your own ROM files (or `.zip`/`.7z` archives of them) into an ingest folder. RnRomManager identifies each one against the [No-Intro](https://no-intro.org/) DATs (cartridge systems) or the [Redump](http://redump.org/) DATs (disc systems), names it consistently, groups every region and revision of a game together, fetches box art and screenshots, and stores each game as one self-describing ZIP in a tidy `roms/<system>/<letter>/` layout. A MariaDB database mirrors the library for fast browsing, and a scheduled integrity checker keeps the two in sync.

![Library](https://raw.githubusercontent.com/rniemand/RnRomManager/main/docs/images/library.png)

- **Library:** grid or list, filter by system, region and letter, search, sort by title, year or date added; primary ROM sizes; download a game's ZIP or a single ROM.
- **Collections:** private groups of games from any system with a cover and Markdown notes, filled from a game's page, the collection's page or a bulk selection in the Library, and found by the global search.
- **Ingestion:** identifies by hash, sorts dual-mode carts into Game Boy Color, files duplicates and broken files aside, and lets you import all pending files or a checked selection.
- **Review queue:** files the DATs don't know get name suggestions, a DAT search, or manual entry; filter the queue, search Google and Shift+click Delete for loose files.
- **Library edits (`rom-manager`):** edit metadata, move or merge releases between games, upload box art and screenshots and choose the cover, delete to a recoverable `_removed/` folder, which Admins restore from or empty on the **Deleted ROMs** page.
- **Reports:** library size treemap and system drill-down, missing cover images, and duplicate groups with merge/dismiss for ROM managers.
- **Game detail:** Releases/Images tabs, a **Play** button that runs Game Boy, Game Boy Color, Game Boy Advance, Virtual Boy, Atari 2600, Atari 5200, Atari 7800, Atari Lynx, Atari Jaguar, ColecoVision, Game Gear and Master System releases in the browser (ColecoVision needs its BIOS, `colecovision.rom`, copied into `data/bios/`), each release's original ingest name and a Markdown note per release (kept in the game's ZIP), a read-only info.json tree with copying, Google search and merge suggestions.
- **Auditing (Admin):** filterable file-action history, retained for 30 days, with user/job and relative source/target paths.
- **Integrity checks:** daily quick checks and on-demand deep (hash) checks, with automatic database repair.
- **Database backups (Admin):** daily backups to a volume with configurable time and retention, Back up now, and a restore at startup.
- **Accounts:** `admin` and `rom-manager` roles (or none, for browse-only users), a live log view, light/dark theme, rebindable keyboard shortcuts, and it works on a phone.

| Game detail | Ingestion |
|---|---|
| ![Game detail](https://raw.githubusercontent.com/rniemand/RnRomManager/main/docs/images/game-detail.png) | ![Ingestion](https://raw.githubusercontent.com/rniemand/RnRomManager/main/docs/images/ingestion.png) |

## Supported systems

<!-- systems:start -->
| System | Id | ROMs stored | Formats | DAT | Play in browser |
|---|---|---|---|---|---|
| 32X | `s32x` | In ZIP | `.32x` | No-Intro | — |
| Atari 2600 | `a2600` | In ZIP | `.a26`, `.bin` | No-Intro | `stella2014` |
| Atari 5200 | `a5200` | In ZIP | `.a52`, `.bin` | No-Intro | `a5200` |
| Atari 7800 | `a7800` | In ZIP | `.a78`, `.bin` | No-Intro | `prosystem` |
| Atari Jaguar | `jaguar` | In ZIP | `.j64`, `.jag`, `.rom`, `.abs`, `.cof` | No-Intro | `virtualjaguar` |
| Atari Lynx | `lynx` | In ZIP | `.lnx`, `.lyx` | No-Intro | `handy` |
| ColecoVision | `coleco` | In ZIP | `.col`, `.rom`, `.bin` | No-Intro | `gearcoleco` |
| Game Boy | `gb` | In ZIP | `.gb` | No-Intro | `gambatte` |
| Game Boy Advance | `gba` | In ZIP | `.gba` | No-Intro | `mgba` |
| Game Boy Color | `gbc` | In ZIP | `.gbc` | No-Intro | `gambatte` |
| Game Gear | `gg` | In ZIP | `.gg`, `.sms` | No-Intro | `smsplus` |
| GameCube | `gc` | Next to ZIP | `.iso` | Redump | — |
| Master System | `sms` | In ZIP | `.sms` | No-Intro | `smsplus` |
| Mega Drive / Genesis | `md` | In ZIP | `.md`, `.bin`, `.gen` | No-Intro | — |
| Nintendo 64 | `n64` | In ZIP | `.z64`, `.v64`, `.n64` | No-Intro | — |
| Nintendo Entertainment System | `nes` | In ZIP | `.nes` | No-Intro | — |
| Nintendo Switch | `switch` | Next to ZIP | `.nsp`, `.xci`, `.nsz`, `.xcz` | titledb | — |
| PlayStation | `psx` | Next to ZIP | `.bin`, `.cue`, `.iso` | Redump | — |
| PlayStation Portable | `psp` | Next to ZIP | `.iso` | Redump | — |
| Saturn | `saturn` | Next to ZIP | `.bin`, `.cue`, `.nrg` | Redump | — |
| Super Nintendo Entertainment System | `snes` | In ZIP | `.sfc`, `.smc` | No-Intro | — |
| TurboGrafx-16 | `tg16` | In ZIP | `.pce` | No-Intro | — |
| Virtual Boy | `vb` | In ZIP | `.vb` | No-Intro | `beetle_vb` |
| Wii | `wii` | Next to ZIP | `.iso`, `.wbfs` | Redump | — |
| Xbox 360 | `x360` | Next to ZIP | `.iso` | Redump | — |
<!-- systems:end -->

## Quick start

You need Docker with Compose. Create a folder with a `.env` file:

```dotenv
DB_PASSWORD=<a long random password>
ADMIN_PASSWORD=<at least 12 characters>
```

and a `docker-compose.yml`:

```yaml
services:
  rnrommanager:
    image: niemandr/rnrommanager:latest
    restart: unless-stopped
    ports: ["8080:8080"]
    environment:
      ConnectionStrings__Default: "Server=mariadb;Database=rnrommanager;User=rnrommanager;Password=${DB_PASSWORD}"
      Admin__InitialPassword: "${ADMIN_PASSWORD}"
      TZ: "Europe/London"
    volumes:
      - ./data/roms:/app/roms
      - ./data/ingest:/app/ingest
      - ./data/logs:/app/logs
      - ./data/cache:/app/cache
      - ./data/backups:/app/backups
      - ./data/collections:/app/collections
      - ./data/bios:/app/bios
    user: "1000:1000"   # owner of ./data
    stop_grace_period: 35s   # lets a running job stop cleanly
    depends_on:
      mariadb: { condition: service_healthy }
  mariadb:
    image: mariadb:11.4
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "healthcheck.sh", "--connect", "--innodb_initialized"]
      interval: 10s
      retries: 10
    environment:
      MARIADB_DATABASE: rnrommanager
      MARIADB_USER: rnrommanager
      MARIADB_PASSWORD: "${DB_PASSWORD}"
      MARIADB_RANDOM_ROOT_PASSWORD: "1"
    volumes: ["./data/db:/var/lib/mysql"]
```

Then:

```bash
mkdir -p data/{roms,ingest,logs,cache,backups,collections,bios,db}
docker compose up -d
```

1. Open `http://<host>:8080`, sign in as `admin` with `ADMIN_PASSWORD`, and choose a new password.
2. **Admin → DATs → Refresh DATs** downloads the game databases (once, and whenever you want newer ones).
3. Copy each system's files (formats in [Supported systems](#supported-systems)) into `data/ingest/<Id>/` (disc images loose, not in archives), then press **Ingestion → Import all**.
4. To play ColecoVision games in the browser, copy the console's BIOS as `data/bios/colecovision.rom` (the app can't ship it).

Upgrading from a version without Game Boy Advance, Nintendo 64, NES, SNES, Virtual Boy, Atari 2600, Atari 5200, Atari 7800, Atari Lynx, Atari Jaguar, ColecoVision, Game Gear, Master System, Mega Drive / Genesis, 32X, TurboGrafx-16, Nintendo Switch, PlayStation, PSP, GameCube, Wii, Xbox 360 or Saturn? Run **Admin → DATs → Refresh DATs** once, or ingestion won't start (`DatsMissing`): it downloads the new system's DATs (for Switch, the titledb file; for the disc systems, the Redump DAT).

Set `user:` to the owner of `./data` (`id -u`:`id -g`). An existing MariaDB server, reverse proxies, backups, upgrades and password recovery are covered in the [deployment guide](https://github.com/rniemand/RnRomManager/blob/main/docs/deployment.md).

## Documentation

- [Deployment](https://github.com/rniemand/RnRomManager/blob/main/docs/deployment.md): compose, database, volumes, reverse proxy, backups, upgrades, password recovery
- [Configuration](https://github.com/rniemand/RnRomManager/blob/main/docs/configuration.md): every setting and its environment variable
- [Features](https://github.com/rniemand/RnRomManager/blob/main/docs/features.md): a guide to every page, plus the API behind it
- [ZIP format](https://github.com/rniemand/RnRomManager/blob/main/docs/zip-format.md): the library layout and `info.json`
- [Adding a system](https://github.com/rniemand/RnRomManager/blob/main/docs/adding-a-system.md): the `ISystemHandler` contract
- [Development](https://github.com/rniemand/RnRomManager/blob/main/docs/development.md) and [contributing](https://github.com/rniemand/RnRomManager/blob/main/CONTRIBUTING.md)

## Disclaimer

RnRomManager manages ROM files **you already own**, for personal backup. It does not download, include or distribute ROMs, and nothing in this project should be used to obtain games you don't own. Respect the copyright laws where you live.

## Attribution

- Game identification and metadata come from [libretro-database](https://github.com/libretro/libretro-database), licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).
- Box art and screenshots are downloaded from [libretro-thumbnails](https://github.com/libretro-thumbnails/libretro-thumbnails). They remain the copyright of their respective owners and are stored for personal use only.
- Nintendo Switch titles are identified with [blawar/titledb](https://github.com/blawar/titledb), and their box art and screenshots are downloaded from the image URLs it lists on Nintendo's eShop image server (`img-eshop.cdn.nintendo.net`). Those images remain the copyright of their owners and are stored for personal use only.
- The in-browser emulator is [EmulatorJS](https://github.com/EmulatorJS/EmulatorJS) ([GPL-3.0](https://github.com/EmulatorJS/EmulatorJS/blob/main/LICENSE)) with the [Gambatte](https://github.com/EmulatorJS/gambatte-libretro) (GPL-2.0), [mGBA](https://github.com/EmulatorJS/mgba) (MPL-2.0), [Handy](https://github.com/EmulatorJS/libretro-handy) (Zlib), [Virtual Jaguar](https://github.com/EmulatorJS/virtualjaguar-libretro) (GPL-3.0), [Beetle VB](https://github.com/EmulatorJS/beetle-vb-libretro) (GPL-2.0), [a5200](https://github.com/EmulatorJS/a5200) (GPL-2.0), [ProSystem](https://github.com/EmulatorJS/prosystem-libretro) (GPL-2.0), Stella 2014, Gearcoleco, SMS Plus GX, FCEUmm and PCSX ReARMed cores, shipped unmodified as separate files with their licences. One change is made to EmulatorJS's `emulator.js` when it's vendored: an explicit `smsplus`, `fceumm` or `pcsx_rearmed` core is kept even if another core was saved in its settings ([development](https://github.com/rniemand/RnRomManager/blob/main/docs/development.md#browser-emulators)).
- The Stella 2014, Gearcoleco, SMS Plus GX, FCEUmm and PCSX ReARMed cores (EmulatorJS 4.2.3 packages, built 2025-06-14) each link RetroArch [`6dd4353`](https://github.com/EmulatorJS/RetroArch/commit/6dd4353937ef48b6ec0bfbdbb15d1c5992d86927) (GPL-3.0-or-later) and were built by [EmulatorJS/build `b24e5b5`](https://github.com/EmulatorJS/build/tree/b24e5b5) with Emscripten 4.0.8. Their corresponding source:
  - [Stella 2014 `1f578c3`](https://github.com/EmulatorJS/stella2014-libretro/tree/1f578c36382bb5a86a0fe245973fa557f369738b) (Atari 2600): GPL v2, whose file headers give no "only" or "or later" qualifier; it's distributed in the GPL-3.0-or-later RetroArch binary as EmulatorJS publishes it.
  - [Gearcoleco `f80af5d`](https://github.com/EmulatorJS/Gearcoleco/tree/f80af5d6f0f78b0228e060e158e7f2500e0caa6d) (ColecoVision): GPL-3.0-or-later.
  - [SMS Plus GX `c642bbd`](https://github.com/EmulatorJS/smsplus-gx/tree/c642bbd0680b5959180a420036108893d0aec961) (Game Gear and Master System): GPL-2.0-or-later. It includes MAME's Z80 core (BSD-3-Clause, Juergen Buchmueller), MAME's YM2413 with CrabEmu changes (GPL-2.0-or-later), Shay Green's sms_ntsc (LGPL-2.1-or-later) and Maxim's SN76489 PSG emulation. Maxim's terms for that file ask to be told when it's distributed and not to pass it off as your own work. **Anyone who publishes an image containing this core should let Maxim know.**
  - [FCEUmm `d9d7e14`](https://github.com/EmulatorJS/libretro-fceumm/tree/d9d7e141274d07186b6a871ca2919b6f13cfb2f9) (NES): GPL-2.0-or-later. It includes Shay Green's nes_ntsc (LGPL-2.1-or-later), MAME's PIC16C5x core (BSD-3-Clause, Tony La Porta) and Mitsutaka Okazaki's emu2413 (MIT).
  - [PCSX ReARMed `f29871d`](https://github.com/EmulatorJS/pcsx_rearmed/tree/f29871dde1742cafb0ab56ba33636a8600bbbd80) (PlayStation): GPL-2.0-or-later, with the P.E.Op.S. GPU and SPU (GPL-2.0-or-later). It includes MAME's GTE divider (BSD-3-Clause, smf) and libchdr (BSD-3-Clause) with the LZMA SDK (public domain), Zstandard (BSD) and dr_flac (public domain or MIT-0). No PlayStation BIOS is included.
  - The notices and licence texts are in [`src/RnRomManager.Web/emulator-licenses/`](https://github.com/rniemand/RnRomManager/tree/main/src/RnRomManager.Web/emulator-licenses) and are copied next to the emulator in the image (`/emulator/vendor/licenses/`).
- The placeholder cartridge box art is made for this project. Admin → About lists the licence of every dependency.

## License

[MIT](https://github.com/rniemand/RnRomManager/blob/main/LICENSE) © Richard Niemand
