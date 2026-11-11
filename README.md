# RnRomManager

A self-hosted web app for managing a personal backup collection of **Game Boy**, **Game Boy Color**, **Game Boy Advance**, **Nintendo 64**, **Nintendo Entertainment System**, **Super Nintendo Entertainment System**, **Atari 2600**, **ColecoVision**, **Game Gear**, **Master System**, **Mega Drive / Genesis**, **32X**, **TurboGrafx-16** and **Nintendo Switch** ROMs. Switch dumps are stored as files next to each game's ZIP, since they're too large to pack.

Drop your own ROM files (or `.zip`/`.7z` archives of them) into an ingest folder. RnRomManager identifies each one against the [No-Intro](https://no-intro.org/) DATs, names it consistently, groups every region and revision of a game together, fetches box art and screenshots, and stores each game as one self-describing ZIP in a tidy `roms/<system>/<letter>/` layout. A MariaDB database mirrors the library for fast browsing, and a scheduled integrity checker keeps the two in sync.

![Library](https://raw.githubusercontent.com/rniemand/RnRomManager/main/docs/images/library.png)

- **Library:** grid or list, filter by system, region and letter, search, sort by title, year or date added; primary ROM sizes; download a game's ZIP or a single ROM.
- **Collections:** private groups of games from any system with a cover and Markdown notes, filled from a game's page, the collection's page or a bulk selection in the Library, and found by the global search.
- **Ingestion:** identifies by hash, sorts dual-mode carts into Game Boy Color, files duplicates and broken files aside, and lets you import all pending files or a checked selection.
- **Review queue:** files the DATs don't know get name suggestions, a DAT search, or manual entry; filter the queue, search Google and Shift+click Delete for loose files.
- **Library edits (`rom-manager`):** edit metadata, move or merge releases between games, upload box art and screenshots and choose the cover, delete to a recoverable `_removed/` folder.
- **Reports:** library size treemap and system drill-down, missing cover images, and duplicate groups with merge/dismiss for ROM managers.
- **Game detail:** Releases/Images tabs, each release's original ingest name and a Markdown note per release (kept in the game's ZIP), a read-only info.json tree with copying, Google search and merge suggestions.
- **Auditing (Admin):** filterable file-action history, retained for 30 days, with user/job and relative source/target paths.
- **Integrity checks:** daily quick checks and on-demand deep (hash) checks, with automatic database repair.
- **Database backups (Admin):** daily backups to a volume with configurable time and retention, Back up now, and a restore at startup.
- **Accounts:** `admin` and `rom-manager` roles (or none, for browse-only users), a live log view, light/dark theme, rebindable keyboard shortcuts, and it works on a phone.

| Game detail | Ingestion |
|---|---|
| ![Game detail](https://raw.githubusercontent.com/rniemand/RnRomManager/main/docs/images/game-detail.png) | ![Ingestion](https://raw.githubusercontent.com/rniemand/RnRomManager/main/docs/images/ingestion.png) |

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
mkdir -p data/{roms,ingest,logs,cache,backups,db}
docker compose up -d
```

1. Open `http://<host>:8080`, sign in as `admin` with `ADMIN_PASSWORD`, and choose a new password.
2. **Admin → DATs → Refresh DATs** downloads the game databases (once, and whenever you want newer ones).
3. Copy your `.gb` / `.gbc` files into `data/ingest/gb/`, your `.gba` files into `data/ingest/gba/` your `.z64` / `.v64` / `.n64` files into `data/ingest/n64/` your headered `.nes` files into `data/ingest/nes/` your `.sfc` / `.smc` files into `data/ingest/snes/` your `.a26` / `.bin` files into `data/ingest/a2600/` your `.col` / `.rom` / `.bin` files into `data/ingest/coleco/` your `.gg` / `.sms` files into `data/ingest/gg/` your Master System `.sms` files into `data/ingest/sms/` and your Mega Drive / Genesis `.md` / `.bin` / `.gen` files into `data/ingest/md/` your 32X `.32x` files into `data/ingest/s32x/` your TurboGrafx-16 / PC Engine `.pce` files into `data/ingest/tg16/` and your Nintendo Switch `.nsp` / `.xci` / `.nsz` / `.xcz` files into `data/ingest/switch/`, then press **Ingestion → Import all**.

Upgrading from a version without Game Boy Advance, Nintendo 64, NES, SNES, Atari 2600, ColecoVision, Game Gear, Master System, Mega Drive / Genesis, 32X, TurboGrafx-16 or Nintendo Switch? Run **Admin → DATs → Refresh DATs** once, or ingestion won't start (`DatsMissing`): it downloads the new system's DATs (for Switch, the titledb file).

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
- The placeholder cartridge box art is made for this project. Admin → About lists the licence of every dependency.

## License

[MIT](https://github.com/rniemand/RnRomManager/blob/main/LICENSE) © Richard Niemand
