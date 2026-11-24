# Deployment

RnRomManager ships as one Docker image, [`niemandr/rnrommanager`](https://hub.docker.com/r/niemandr/rnrommanager), for `linux/amd64` and `linux/arm64`. It serves the web UI and the API on port 8080, and needs a MariaDB (10.11+; MySQL 8.0+ on a best-effort basis) database.

Tags: `X.Y.Z`, `X.Y` and `latest` for releases; `edge` for the latest `main`. Pin a release tag in production.

## Docker Compose

Create a folder for the stack with a `.env` file holding the two secrets (keep it out of version control):

```dotenv
DB_PASSWORD=<a long random password>
ADMIN_PASSWORD=<at least 12 characters>
```

and this `docker-compose.yml`:

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
docker compose logs -f rnrommanager
```

Wait for the container to report `healthy`, open `http://<host>:8080`, sign in as `admin` with `ADMIN_PASSWORD` and choose a new password. Then go to **Admin → DATs → Refresh DATs** before the first ingestion. See [features](features.md) for the rest.

If `Admin__InitialPassword` is left empty, the first start prints a random `admin` password to the container console once (`docker compose logs rnrommanager`). It is never written to the log file. `ADMIN_PASSWORD` is only used to create the first account; changing it later has no effect.

The full list of settings is in [configuration](configuration.md).

## Using an existing MariaDB server

The app never creates its database. On a shared server, create it and a user that can only touch it:

```sql
CREATE DATABASE rnrommanager CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'rnrommanager'@'%' IDENTIFIED BY '<password>';
GRANT ALL ON rnrommanager.* TO 'rnrommanager'@'%';
```

Drop the `mariadb` service and `depends_on` from the compose file and point the connection string at your server, e.g. `Server=db.example.lan;Port=3306;Database=rnrommanager;User=rnrommanager;Password=${DB_PASSWORD}`. Don't add `DateTimeKind` or `GuidFormat`: the app sets them itself.

On start the app waits up to 60 seconds for the database, then applies its migrations. An unknown database or a denied login stops it with a `FATAL` log line naming the database. Run one app instance per database: the job lock lives in the process.

## Volumes and permissions

| Path | Contents |
|---|---|
| `/app/roms` | the library: one ZIP per game (see [ZIP format](zip-format.md)), plus the Nintendo Switch ROM files stored next to their ZIPs. A system's folder (`roms/<system>/`) and its letter folders are created when its first ROM is written, not at startup; at each start the app removes letter folders (`#`, `A`–`Z`) that are empty, then the system folder if that leaves it empty. Only plain, empty folders go: a folder holding anything (hidden files included), any other name, and a linked or unreadable folder (logged as a Warning) are left alone. |
| `/app/ingest` | drop ROMs (and `.zip`/`.7z` archives) into `ingest/gb/` or `ingest/gbc/` (Game Boy / Color) `ingest/gba/` (Game Boy Advance), `ingest/n64/` (Nintendo 64: `.z64`, `.v64`, `.n64`, `.bin` or `.u1`) `ingest/nds/` (Nintendo DS: `.nds`, stored next to the ZIP; dumps over 256 MiB must be dropped loose, since archives are capped at 256 MiB of ROM data) `ingest/3ds/` (Nintendo 3DS: `.3ds`, `.cci` or `.cia`, stored next to the ZIP) `ingest/nes/` (NES: headered `.nes` only) `ingest/fds/` (Famicom Disk System: `.fds`, fwNES header stripped) `ingest/snes/` (SNES: `.sfc` or `.smc`, copier header stripped) `ingest/satellaview/` (Satellaview: `.bs`) `ingest/sufami/` (Sufami Turbo: `.st`) `ingest/a2600/` (Atari 2600: `.a26` or `.bin`) `ingest/coleco/` (ColecoVision: `.col`, `.rom` or `.bin`) `ingest/gg/` (Game Gear: `.gg`, `.sms`, `.bin` or `.rom`) `ingest/sms/` (Master System: `.sms`, `.sg`, `.bin` or `.rom`) `ingest/md/` (Mega Drive / Genesis: `.md`, `.bin`, `.gen` or `.smd`, interleaved `.smd` deinterleaved) `ingest/s32x/` (32X: `.32x` or `.bin`) `ingest/tg16/` (TurboGrafx-16 / PC Engine: `.pce`, copier header stripped) `ingest/ngp/` or `ingest/ngpc/` (Neo Geo Pocket / Color: `.ngp`, `.ngc`, `.ngpc` or `.npc`; the header decides which) `ingest/switch/` (Nintendo Switch: `.nsp`, `.xci`, `.nsz`, `.xcz` or `.hdf`) `ingest/psx/` (PlayStation: a `.cue` with its `.bin` tracks, or `.bin`/`.iso`/`.chd`/`.img`/`.pbp`) `ingest/psp/` (PSP: `.iso`, `.cso`, `.pbp` or `.chd`) `ingest/gc/` (GameCube: `.iso` or `.gcm`) `ingest/wii/` (Wii: `.iso`) `ingest/x360/` (Xbox 360: `.iso`) `ingest/saturn/` (Saturn: a `.cue` with its `.bin` tracks, or `.nrg`/`.chd`/`.iso`) `ingest/segacd/` (Sega CD: a `.cue` with its `.bin` tracks, or `.bin`/`.iso`/`.chd`) `ingest/tgcd/` (TurboGrafx-CD / PC Engine CD: a `.cue` with its `.bin` tracks, or `.chd`) `ingest/3do/` (3DO: a `.cue` with its `.bin` tracks, or `.bin`/`.iso`/`.chd`) or `ingest/c64/` (Commodore 64: `.crt`, `.tap`, `.nib`, `.nbz`, `.d64`, `.g64`, `.d2m`, `.d4m`, `.t64`, `.prg`, `.tcrt` or `.bin`, or a gzipped disk image: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z` or `<name>.d64.gz`), `ingest/pet/` (Commodore PET: `.prg` or `.d64`, or a gzipped disk image: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z` or `<name>.d64.gz`), `ingest/vic20/` (Commodore VIC-20: `.a0`, `.20`, `.40`, `.60` or `.bin`, or a gzipped disk image: `.d6z`, `.d7z`, `.d8z`, `.g6z`, `.x6z` or `<name>.d64.gz`), `ingest/amiga/` (Commodore Amiga: `.adf` or gzipped `.adz`); Switch, Nintendo DS, Nintendo 3DS and the disc systems are stored as files next to the ZIP (see [External ROM storage](#external-rom-storage-nintendo-switch-nintendo-ds-nintendo-3ds-and-disc-systems)), and disc images must be dropped loose, not in archives; MS-DOS games go in `ingest/dos/` as one `.zip` (or `.dosz`) per game, which isn't unpacked and always goes to Review; arcade romsets go in `ingest/mame2003/` as one `.zip` per set, named by its MAME set name (`sf2.zip`), which isn't unpacked and is stored next to the ZIP; `_failed/`, `_duplicates/` and `_removed/` hold files taken out of the way (`_removed/` is managed on the Admin **Deleted ROMs** page: restore or delete for good) |
| `/app/logs` | `rnrommanager-yyyy-MM-dd.log`, kept for `Logging:RetentionDays` |
| `/app/cache` | downloaded DATs and temporary work folders |
| `/app/backups` | the daily database backups (see [Backup and restore](#backup-and-restore)); keep it private |
| `/app/collections` | collection cover images (`<collection id>.png` or `.jpg`) |
| `/app/bios` | emulator BIOS files you copy in for browser play (`colecovision.rom` for ColecoVision, required; the optional 512 KiB `scph5501.bin` for PlayStation, which otherwise uses its built-in HLE BIOS; at least one of the 128 KiB `bios_CD_U.bin`, `bios_CD_E.bin` or `bios_CD_J.bin` for Sega CD, required; at least one of the 1 MiB `panafz10.bin`, `panafz1.bin`, `panafz1j.bin`, `goldstar.bin` or `sanyotry.bin` for 3DO, required; a TurboGrafx-CD System Card, `syscard3.pce` (or `syscard3u.pce`, `syscard2.pce`, `syscard2u.pce`, `syscard1.pce`; the first found is used), required, plus `gexpress.pce` for Games Express discs, optional; up to 1 MiB each); any file here can be downloaded by a signed-in user, so keep only BIOS files in it |

An existing deployment that doesn't map `/app/collections` or `/app/bios` still starts (the folders are created), but their files then live in anonymous Docker volumes and are lost when the container is recreated: add the mappings when you upgrade (`mkdir -p data/bios`, then add `- ./data/bios:/app/bios`).

The container runs as UID 1654 unless you set `user:`.

- **Named volumes** work as they are: the image's folders are already owned by 1654.
- **Bind mounts** (as above) must be writable by the container's user. Either set `user: "<uid>:<gid>"` to the owner of `./data` (find it with `id -u` / `id -g`), or `chown -R 1654:1654 ./data/{roms,ingest,logs,cache,backups,collections,bios}`.

At startup the app writes and deletes a `.rnrm-probe` file in each of the seven folders. If that fails, it stops with a `FATAL` line naming the path and the UID/GID it runs as.

## Time zone

`TZ` sets the time zone for the daily quick integrity check (`Integrity:QuickCheckTime`, default `03:00`) and the daily database backup (**Admin → Backups**, default `03:30`). The image includes the time zone database. Times in the UI are always shown in the browser's own time zone.

## Reverse proxy

The app speaks plain HTTP on 8080. Behind a TLS reverse proxy (Caddy, Traefik, nginx, …), set:

```yaml
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
```

so the app sees the original scheme (for `Secure` cookies) and client address. **Only set it when port 8080 can't be reached except through the proxy** (e.g. remove the `ports:` mapping and put the proxy on the same Docker network). The setting trusts `X-Forwarded-For` from any sender, so with the port open anyone could fake their address and get around the login rate limit (10 attempts per minute per address).

The live log (**Admin → Logs**) is a long-lived server-sent event stream at `/api/admin/logs/stream`. The app turns off response buffering and sends a keep-alive every 15 seconds, but the proxy must not buffer it either and must allow an idle read of more than 15 seconds. nginx needs `proxy_buffering off;` and a `proxy_read_timeout` above 15 s on that location; Caddy and Traefik stream it as it is.

Moving or deleting a Switch release can copy a multi-GB file when `/app/ingest` and `/app/roms` are on different volumes (Review resolutions run as background jobs, so they aren't affected). The request stays open until that finishes, so raise the proxy's read timeout for `/api/` (nginx `proxy_read_timeout`, e.g. `600s`); otherwise the browser gets a gateway error while the action still completes in the background. Reload the page before trying again.

**PSP, MS-DOS and Nintendo 3DS play** each run in their own tab that must be cross-origin isolated: the app sends `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` on `/emulator/play-psp.html`, `/emulator/play-dos.html` and `/emulator/play-3ds.html` only. The proxy must pass those two headers through unchanged (Caddy, Traefik and nginx do by default; don't strip or override them). The browser also needs a secure origin: serve the app over HTTPS, or open it on `localhost`/`127.0.0.1`. Opened over plain HTTP at a LAN address, the PSP, DOS or 3DS tab says it needs a secure page; every other system plays as before. Nintendo 3DS also needs WebGL 2.

Use `ASPNETCORE_HTTP_PORTS` to change the listen port, not `ASPNETCORE_URLS`: the container health check reads the first port from `ASPNETCORE_HTTP_PORTS`.

## Outbound HTTP

Besides the DAT downloads (libretro-database and the [titledb](https://github.com/blawar/titledb) file for Switch, `Sources:TitleDbUrl`, about 90 MB and capped at 256 MiB) and the thumbnail downloads (libretro-thumbnails, and for Switch images from `img-eshop.cdn.nintendo.net` only, with redirects refused), **Upload Image → URL** makes the server fetch an image from an address a ROM manager types. That download connects directly (it ignores `HTTP_PROXY`/`HTTPS_PROXY`), only to public internet addresses (private, loopback, link-local and similar ranges are refused on every redirect hop), with a 15-second limit and at most 8 MiB. On a host that can only reach the internet through a mandatory egress proxy, the URL option fails with "couldn't connect"; uploading a file still works.

## Health checks

- `GET /health/live`: the process is up (used by the image's `HEALTHCHECK`).
- `GET /health/ready`: the database answers within 2 seconds and the seven folders exist; 503 otherwise.

Both are anonymous and return no details.

## Backup and restore

### What to back up

- **The database**, which holds users, the Data Protection keys that keep sign-ins valid across restarts, job history, the review queue, integrity issues, the audit trail, settings and the shared publisher, genre and system logos. The app backs it up itself (below).
- **`/app/roms`**, the library itself, including release notes and the Switch ROM files next to their ZIPs. Stop the app (or make sure no job is running) for a consistent copy.
- **`/app/backups`**, so the database backups survive the loss of the host. Copy it somewhere else.
- **`/app/collections`**, the collection cover images. Collections themselves are in the database (and its backups), but their cover files are not: copy this folder with the backups. After a restore without it, collections show a placeholder until a cover is set again.
- **`/app/bios`**, the emulator BIOS files you copied in. Database backups don't include them: keep your own copy.

The library ZIPs, together with the external ROM files next to them in `/app/roms`, are the source of truth for games: if the database is lost but they are not, a new database is rebuilt from them by the next ingestion or **Integrity → Run quick** (accounts and history are not). `/app/ingest` holds anything not yet ingested plus `_removed/` (the ROMs listed on **Deleted ROMs**, until restored or deleted there); back it up too if you keep files there. `/app/cache` can be re-downloaded with **Refresh DATs**.

### Database backups

Every day at **03:30** (container time, `TZ`) the app writes `rnrommanager-<yyyyMMdd-HHmmss>.zip` (UTC start time) to `Paths:Backups` and keeps the newest **30**. Change the time, the number kept, or turn it off on **Admin → Backups**; **Back up now** runs one at once. Missed runs (the app was down) aren't caught up.

- A backup is a ZIP with `manifest.json` (format version, backup id, time, app version, the migrations applied and each table's row count), `schema.sql` (`SHOW CREATE TABLE` for every table) and `data/<table>.jsonl` (one JSON object per row). It's read inside one consistent snapshot, so jobs and edits keep running.
- It's written to `<name>.zip.tmp` and renamed only when complete; leftover `.tmp` files from a crash are deleted before the next run. Two backups in the same second get ` (2)`, ` (3)`… before `.zip`.
- Only files named `rnrommanager-*.zip` are ever deleted. A backup that was restored at startup (it has a `.restored` file next to it) or is named by `Backup__RestoreFrom` is never deleted and doesn't count toward the number kept; neither does the backup a live restore is restoring while its safety backup is written. When a live restore fails, both that backup and its safety backup get a `.keep` file and are kept the same way until you delete the `.keep` file.
- **Backups contain password hashes and the Data Protection keys.** Keep the folder private; the app never serves backup files over HTTP.

`mariadb-dump rnrommanager` still works as an extra backup; it isn't needed for a restore.

### Restoring a backup

A restore replaces **every table** in the database with the backup's contents. It can't be undone. There are two ways: from **Admin → Backups** while the app runs, or at startup with `Backup__RestoreFrom`.

#### From Admin → Backups (live)

Choose **Restore** on a backup and confirm. The app then:

1. Refuses without changing anything if a job, a library edit or another backup is running (`409`), if the backup is from a newer app version, or if it can't be read.
2. Answers every other request (other users, other tabs, `/health/ready`) with `503 Maintenance` and `Retry-After: 5` until it's done, after waiting up to 10 seconds for requests already running (downloads and the live log aren't waited for). If they don't finish, the restore is refused (`409 Busy`).
3. Writes a **safety backup** of the current database (it counts toward the number kept, but the backup being restored is never deleted by it). If that fails, nothing is restored.
4. Drops every table, recreates them from the backup, applies newer migrations, marks jobs that were Running in the backup as Aborted and signs **everyone** out, you included. It records `BackupRestored` in the audit log.
5. Sends you to the sign-in page. Run **Integrity → Run quick** afterwards: the library ZIPs may be newer than the backup.

On a large library this can take a while; the rest of the app is unavailable meanwhile. **Don't stop or restart the container during a restore**: a shutdown between the drop and the inserts leaves a partial database. If that happens, or the restore reports `RestoreFailed`, restore the safety backup it names (live, or at startup as below). If the page lost its connection mid-restore, it says the restore may still be running: wait a minute, sign in again and check **Backups**.

Sign-in keys created after the backup was taken are gone from the database but may still be cached by the running app, so a sign-in right after a restore can need repeating after the next restart (or within about a day).

#### At startup (`Backup__RestoreFrom`)

1. If the database is still readable, take a fresh backup first (**Back up now**), so you can go back.
2. Stop the app: `docker compose stop rnrommanager`.
3. Make sure the backup is in `Paths:Backups` (`./data/backups/` in the compose file above). The database must exist, even if empty: with the compose file's `mariadb` service, `MARIADB_DATABASE` creates it; on your own server, create it as in [Using an existing MariaDB server](#using-an-existing-mariadb-server).
4. Add the file name (not a path) to the app's environment and start it:

   ```yaml
       environment:
         Backup__RestoreFrom: "rnrommanager-20261106-033000.zip"
   ```

   ```bash
   docker compose up -d rnrommanager
   docker compose logs -f rnrommanager
   ```

   At startup, after the database is reachable and before migrations, the app checks the backup (format version 1, and every migration it lists must be known to this app version: a backup from a newer version is refused), drops every table, recreates them from `schema.sql`, inserts the rows and checks the row counts. Then it writes `<file>.restored` (the time) next to the backup, logs a Warning with the backup id and row counts, and starts normally; migrations bring an older backup up to date. Any problem stops startup with a `FATAL` line. The checks run before anything is dropped, and a restore that fails half-way can simply be run again.
5. Run **Integrity → Run quick**: the library ZIPs may be newer than the backup, and the check brings the database back in line with them (a Warning at startup reminds you).
6. Remove `Backup__RestoreFrom` from the environment. Until you do, every start logs a Warning and skips the restore, because the `.restored` file is there.

## Outbound connections

- **DATs:** the daily automatic refresh (**Admin → DATs**, on by default at 04:00) and **Refresh DATs** download from `Sources:DatBaseUrl`, `Sources:TitleDbUrl` and `Sources:MameDatUrl` (GitHub by default).
- **Images by URL:** a game image, collection cover or publisher, genre or system logo added from a URL is downloaded by the server, from public addresses only (private, loopback and link-local addresses are refused, redirects included), with a 15-second limit.

## Upgrades

Back up the database (**Admin → Backups → Back up now**) and `roms/`, then pull the new tag and restart:

```bash
docker compose pull
docker compose up -d
```

Migrations run automatically on start. `info.json` files in older formats are upgraded in memory and rewritten in the new format the next time each game is written. There is no downgrade path: an older app refuses to touch ZIPs written in a newer format.

Upgrading to the release that keeps saves in per-user folders:

- **Saves move once, in the background.** After the first start the **Moving saves to user folders** job rewrites every game ZIP that has saves (each user's saves go to `saves/<username>/`; saves without an owner become `admin`'s). It holds the job lock, so edits, ingest and save uploads answer "Another job is running" until it ends; on embedded systems each rewrite copies the whole ZIP, so plan the time and the free space (one ZIP's size at a time). Games it couldn't move (see the job's `Error` items) keep the old layout and move on their next save write.
- **Downgrading** after the move: an older app reads the new `saves.json` as unreadable, keeps the files byte for byte and refuses the save routes; nothing is lost. Restore the pre-upgrade backup and `roms/` snapshot to go back fully.

Upgrading to 0.3.0:

- **Everyone is signed out once.** Roles change from a single Admin/User to the `admin` and `rom-manager` roles; every Admin gets both and every User none, and every session ends so the new roles take effect. Sign in again.
- **ZIPs move to `info.json` version 3** as each game is next written (cover flag, uploaded and JPEG images). An app older than 0.3.0 can still read those ZIPs but refuses to change them (`InfoJsonNewerSchema`), and the database can't be downgraded either: restore the backup to go back.

Upgrading to 0.4.0:

- **Refresh DATs after upgrading.** Nintendo Switch is added and identified through titledb; ingestion refuses to start with `DatsMissing` until **Admin → DATs → Refresh DATs** has downloaded its titledb file. `ingest/switch/` is created at startup.
- **Refresh DATs again for the disc systems.** PlayStation, PSP, GameCube, Wii, Xbox 360 and Saturn are identified against libretro's Redump DATs (`metadat/redump/`); ingestion refuses to start with `DatsMissing` until a refresh has downloaded them. Their `ingest/<id>/` folders are created at startup.
- **ZIPs move to `info.json` version 6** as each game is next written (`externalFileName`, `TitleDb`, `titledb` images, an update's `version`, and a disc set's `files`). Migration 0020 adds the `rom_files` table. Apps before 0.4.0 can read them but refuse to change them.
- **Migration 0019** adds `roms.version` and fills it for Switch updates already in the library from the `[x.y.z]` tag in their original file name, so the Game page's Updates tab orders them at once. Existing Switch files keep their names; only files written from now on drop the repeated title (`Orbit Expedition.[0100…][v0].nsp`, see [ZIP format](zip-format.md#external-rom-files)).
- **Plan disk space** for Switch dumps and disc images (often several GB each). Keep `/app/ingest` and `/app/roms` on the same volume where you can: placement is then an instant rename; across volumes each file is copied and needs free space for a second copy while it moves.
- **Add the BIOS folder** for ColecoVision browser play: `mkdir -p data/bios`, add `- ./data/bios:/app/bios` to the app's `volumes:` (and `chown` it like the others if you don't set `user:`), then copy `colecovision.rom`, `disksys.rom` (Famicom Disk System), `BS-X.bin` (Satellaview), `STBIOS.bin` (Sufami Turbo) (and, optionally, `scph5501.bin` for PlayStation) into it. Without the mapping the folder lives in an anonymous volume.
- **To be able to downgrade,** take a database backup (**Back up now**) and a filesystem snapshot of `/app/roms` (and `/app/ingest`) at the same moment before upgrading, and restore **both** to go back: the database alone doesn't know where external files were.

After upgrading to a version that adds a system (such as the ones that add Game Boy Advance, Nintendo 64, NES, SNES, Atari 2600, ColecoVision, Game Gear, Master System, Mega Drive / Genesis, 32X, TurboGrafx-16, Nintendo Switch, the disc systems, Sega CD, 3DO, Commodore 64 and TurboGrafx-CD), ingestion refuses to start with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs** to download the new system's DATs.

On `docker compose stop` a running job gets 30 seconds to finish its current item (`stop_grace_period: 35s` allows for that). A job that's cut off is marked `Aborted` at the next start and its leftovers are cleaned up; just start it again.

Upgrading to the release that adds Nintendo DS:

- **Refresh DATs after upgrading.** Nintendo DS is identified against libretro's No-Intro `Nintendo - Nintendo DS` DAT and its four metadat files. Until **Admin → DATs → Refresh DATs** has downloaded all five, every import is refused with `DatsMissing`, including one that selects only other systems' files. `ingest/nds/` is created at startup.

Upgrading to the release that adds save management (and stores Nintendo DS ROMs next to the ZIP):

- **Back up the whole library folder,** not only `*.zip`: Nintendo DS ROMs become `.nds` files next to their ZIPs, like Switch dumps. After the upgrade an older app version can't read the migrated DS games, and it drops every game's `saves/` on its next write: don't downgrade without restoring the backup.
- **The storage migration runs once, by itself.** Shortly after the first start, the **storage migration** job moves every DS game's ROMs out of its ZIP (one job item per game, `Migrated` or `Error`; an audit row `RomsExternalized` each). It needs, per game, free space for the game's ROMs plus its ZIP plus 64 MiB on the library volume, and for its largest ROM on the cache volume; a game without it fails with `InsufficientSpace` and the job goes on. If free space can't be read (some network shares), it goes ahead. A crash mid-game is safe: the ZIP is only replaced once every file is in place, and a file left behind is reused on the next run.
- **Until it ends, DS games are read-only and everything else waits.** A DS game still holding its ROMs plays, lists and downloads as before, but every edit to it answers `409` ("This game's ROMs are being moved by the storage job…"). The job holds the job lock, so meanwhile every edit and every job (ingest, integrity, DAT refresh, media retry, review resolve) is refused with `JobRunning`, the scheduled quick check is skipped, and new files wait in ingest until an import runs after it. On a large DS library this can take hours. A run that ends with `Error` items isn't retried by itself: fix the cause and use **Integrity → Resume ROM storage moves**, shown while DS games still wait to be moved.

Upgrading to the release that adds Sega CD:

- **Refresh DATs after upgrading.** Sega CD is identified against libretro's Redump `Sega - Mega-CD - Sega CD` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/segacd/` and `ingest/segacd/` are created at startup.
- **For Sega CD browser play,** set up the [Genesis Plus GX core](#genesis-plus-gx-core) (the same files as Mega Drive / Genesis) and copy at least one regional BIOS, `bios_CD_U.bin`, `bios_CD_E.bin` or `bios_CD_J.bin` (128 KiB each), into `/app/bios`. The app doesn't include any Sega CD BIOS; a disc whose region's BIOS is missing doesn't boot.

Upgrading to the release that adds 3DO:

- **Refresh DATs after upgrading.** 3DO is identified against libretro's Redump `The 3DO Company - 3DO` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/3do/` and `ingest/3do/` are created at startup.
- **For 3DO browser play,** set up the [Opera core](#opera-core) (`Emulator:OperaCorePath`) and copy at least one 3DO BIOS, `panafz10.bin`, `panafz1.bin`, `panafz1j.bin`, `goldstar.bin` or `sanyotry.bin` (1 MiB each), into `/app/bios`. The app doesn't include any 3DO BIOS.

Upgrading to the release that adds Commodore 64:

- **Refresh DATs after upgrading.** Commodore 64 is identified against libretro's No-Intro `Commodore - 64` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/c64/` and `ingest/c64/` are created at startup.
- **For Commodore 64 browser play,** set up the [VICE core](#vice-core) (`Emulator:ViceCorePath`).

Upgrading to the release that adds Commodore PET:

- **Refresh DATs after upgrading.** Commodore PET is identified against libretro's TOSEC `Commodore - PET` DAT (`metadat/tosec/`, one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/pet/` and `ingest/pet/` are created at startup.
- **For Commodore PET browser play,** add the `vice_xpet` files to the [VICE core](#vice-core) folder.

Upgrading to the release that adds Commodore VIC-20:

- **Refresh DATs after upgrading.** Commodore VIC-20 is identified against libretro's No-Intro `Commodore - VIC-20` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/vic20/` and `ingest/vic20/` are created at startup.
- **For Commodore VIC-20 browser play,** add the `vice_xvic` files to the [VICE core](#vice-core) folder.

Upgrading to the release that adds Commodore Amiga:

- **Refresh DATs after upgrading.** Commodore Amiga is identified against libretro's TOSEC `Commodore - Amiga` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/amiga/` and `ingest/amiga/` are created at startup.
- Browser play needs nothing extra: the PUAE core ships in the image and boots its built-in AROS Kickstart.

Upgrading to the release that adds Nintendo 3DS:

- **Refresh DATs after upgrading.** Nintendo 3DS is identified against libretro's No-Intro `Nintendo - Nintendo 3DS` DAT and its developer, publisher and genre metadat (libretro has no 3DS release-year file). Until **Admin → DATs → Refresh DATs** has downloaded all four, every import is refused with `DatsMissing`. `ingest/3ds/` is created at startup; dumps are stored next to the ZIP.
- There is no browser play: EmulatorJS has no 3DS core.

Upgrading to the release that adds Satellaview and Sufami Turbo:

- **Refresh DATs after upgrading.** They are identified against libretro's No-Intro `Nintendo - Satellaview` and `Nintendo - Sufami Turbo` DATs and their developer, publisher, genre and release-year metadat. Until **Admin → DATs → Refresh DATs** has downloaded them, every import is refused with `DatsMissing`. `ingest/satellaview/` and `ingest/sufami/` are created at startup.
- **For browser play,** set up the Snes9x core files (as for SNES) and copy the 1 MiB `BS-X.bin` (Satellaview) and the 256 KiB `STBIOS.bin` (Sufami Turbo) into `/app/bios`. The app doesn't include them.

Upgrading to the release that adds Famicom Disk System:

- **Refresh DATs after upgrading.** Famicom Disk System is identified against libretro's No-Intro `Nintendo - Family Computer Disk System` DAT and its developer, publisher, genre and release-year metadat. Until **Admin → DATs → Refresh DATs** has downloaded them, every import is refused with `DatsMissing`. `ingest/fds/` is created at startup.
- **For Famicom Disk System browser play,** copy the 8 KiB `disksys.rom` into `/app/bios`. The app doesn't include it.

Upgrading to the release that adds TurboGrafx-CD / PC Engine CD:

- **Refresh DATs after upgrading.** TurboGrafx-CD is identified against libretro's Redump `NEC - PC Engine CD - TurboGrafx-CD` DAT (one file, no metadat). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `roms/tgcd/` and `ingest/tgcd/` are created at startup.
- **For TurboGrafx-CD browser play,** copy a System Card BIOS into `/app/bios`: `syscard3.pce` (or `syscard3u.pce`, `syscard2.pce`, `syscard2u.pce` or `syscard1.pce`; the first found in that order is used), and optionally `gexpress.pce` for Games Express discs. The Beetle PCE core already ships in the image; the app doesn't include any System Card.

Upgrading to the release that adds Neo Geo Pocket and Neo Geo Pocket Color:

- **Refresh DATs after upgrading.** They are identified against libretro's No-Intro `SNK - Neo Geo Pocket` and `SNK - Neo Geo Pocket Color` DATs and their developer, publisher, genre and release-year metadat. Until **Admin → DATs → Refresh DATs** has downloaded them, every import is refused with `DatsMissing`. `ingest/ngp/` and `ingest/ngpc/` are created at startup.
- Browser play needs nothing extra: the Beetle NeoPop core ships in the image and needs no BIOS.

Upgrading to the release that adds Nintendo 64 browser play:

- **For Nintendo 64 browser play,** set up the [Mupen64Plus-Next core](#mupen64plus-next-core) (`Emulator:Mupen64PlusCorePath`). Without it, N64 games show Play, which explains that the core needs setting up.

Upgrading to the release that adds Arcade (MAME 2003-Plus):

- **Refresh DATs after upgrading.** Arcade sets are identified against the MAME 2003-Plus DAT (`Sources:MameDatUrl`). Until **Admin → DATs → Refresh DATs** has downloaded it, every import is refused with `DatsMissing`. `ingest/mame2003/` is created at startup.
- **For Arcade browser play,** set up the [MAME 2003-Plus core](#mame-2003-plus-core) (`Emulator:Mame2003PlusCorePath`). Without it, arcade games show Play, which explains that the core needs setting up.

Upgrading to the release that adds the footer, group logos, live restore and the DAT schedule:

- **DATs refresh daily.** A new **Automatic refresh** (on by default, 04:00 container time) downloads the DATs from GitHub every day. On a host without outbound access each run fails and logs a failed job; turn it off on **Admin → DATs**.
- **Library folders are created on demand.** `roms/<system>/` and its letter folders are no longer created at startup, and empty ones left by earlier versions are removed at the next start (only empty, plain folders; see [volumes](#volumes-and-permissions)). `ingest/<system>/` is still created at startup.
- **`publisher_logos` becomes `discovery_logos`** (migration `0026`): existing publisher logos keep working. Older backups still restore; migrations bring them up to date.
- **About moved** from the Admin tabs to the footer, for every user.

Upgrading to the release that adds user management (Admin and Site user accounts):

- **Most users, normally every Admin, are signed out once.** The `rom-manager` role is folded into `admin` (migration `0024`): every account that had `rom-manager` is signed out, which includes every Admin from before roles were split and the first account. Accounts with only `rom-manager` become **Site users** and lose library edits; accounts with only `admin` gain them. Release notes, ratings and favourites, and publisher logos become Admin-only.
- **Saves become private.** Each save belongs to its uploader; a Site user sees only their own, an Admin every save. Existing saves keep their uploader as owner; saves with no uploader are visible to Admins only. The game ZIP (download and Zip Contents) still holds every save.
- **A Site user's save counts start at 0.** Per-user counts (migration `0025`) fill in when a game's ZIP is next written by the app, or found changed by a sync; until then a Site user sees no save badge and `Saves (0)` on that game, though its Saves tab lists their saves. Admin counts are unaffected.
- **Sign-ins and account changes are audited** from now on (see [features](features.md#file-action-auditing)).

## Nintendo DS: memory, disk and time

**Where DS ROMs are stored is an Admin choice** (**Admin → ROM Zip Config**): next to each game's ZIP (the default) or inside it. Switching starts the storage job, which rewrites every DS game one at a time: on a large library that takes hours, uses about one game's ROMs plus its ZIP (plus 64 MiB) of free space on the library volume at a time, and holds the job lock, so meanwhile every edit and every other job (ingest, integrity, DAT refresh, media retry, review resolve, the scheduled quick check) is refused or skipped. Cancel it from the Jobs page and resume it later (**Admin → ROM Zip Config → Resume moves** or **Integrity → Resume ROM storage moves**); games it hasn't reached stay readable but read-only, and after a restart it resumes by itself. Inside the ZIP, every save or edit of a DS game rewrites the whole ZIP including its ROM (up to 512 MiB), so they're slower. The choice is stored in the database (`app_settings`, `storage.embedRoms.nds`): restoring a database backup that disagrees with the ZIPs on disk just makes those games wait for a resume. Versions since the release that added save management read DS games either way; an older one can't read DS ROMs next to the ZIP, so don't downgrade without restoring a matching library backup.

**Nintendo 3DS works the same way** (`storage.embedRoms.3ds`), with larger dumps (up to 4 GiB): inside the ZIP, every edit rewrites the whole ROM, an import holds the whole ROM in memory (give the container memory to match) and dumps over about 2 GiB are refused as too large; the storage job streams, so it moves any size. Older app versions expect 3DS ROMs beside the ZIP, so move them back before downgrading.


DS dumps reach 512 MiB. They are stored as files next to the game's ZIP (see [External ROM storage](#external-rom-storage-nintendo-switch-nintendo-ds-nintendo-3ds-and-disc-systems)), so imports stream them like Switch dumps and edits, notes and saves rewrite only the small metadata ZIP. The figures below were measured on 2026-11-14, when DS ROMs were still stored inside the ZIP, with the e2e stack (Docker Desktop on Windows 11, a WSL 2 VM with 16 GiB and 16 CPUs) and two synthetic 512 MiB ROMs of random, incompressible bytes; they are the upper bound for a game the [storage migration](#upgrades) hasn't moved yet, and for the migration itself, which copies each ROM once:

| Operation | Peak memory above idle | Time | Extra library disk at the lowest point |
|---|---:|---:|---:|
| Import a 512 MiB DS ROM (DAT match) | about 550 MiB | 12–15 s | about 540 MiB (the new ZIP) |
| Import a 512 MiB DS ROM that goes to Review | about 0–510 MiB | 4 s | none |
| Resolve that Review item (**Manual**) | up to about 510 MiB | 11–12 s | about 540 MiB |
| Save a note on a game with one 512 MiB release | none | 14–15 s | about 540 MiB (the rewritten ZIP) |
| Delete a game with one 512 MiB release | none | 4 s | none (its `_removed/` copy goes to the ingest volume) |

- **Memory.** The app idled at about 145 MiB. A maximum-size import or resolution needs about 550 MiB more. Without a container memory limit, .NET keeps the freed buffers, so the process stayed at about 1.7 GiB after three large operations. With a 1 GiB limit, every operation finished with the process at or below about 700 MiB. If you store maximum-size DS dumps, give the container at least 1 GiB and set the limit when the container starts (`mem_limit` in Compose): .NET reads it at startup, and a limit lowered on a running container (`docker update`) got the process killed.
- **Disk.** Every ZIP write builds the new ZIP beside the old one, so the library volume needs free space at least the size of the game's new ZIP, here about 540 MiB per 512 MiB release (random data doesn't compress). Deleting a release or game also needs space for its `_removed/` copy on the ingest volume.
- **Time.** A ZIP write re-compresses every entry and re-reads each ROM inside it to verify it, and holds the job lock while it does. For a migrated DS game that is only the metadata ZIP; the migration itself copies each ROM out once and rewrites the ZIP once. ROM entries above 128 MiB are written at Deflate's fastest level to keep this down ([ZIP format](zip-format.md)).

## ZIP compression repack

Changing a system's ZIP compression on **Admin → ROM Zip Config** (or **Repack again**) starts the `ZipRepack` job, which rewrites every game ZIP of that system, one at a time: each ROM is read, re-compressed, verified (size and SHA-1) and the ZIP replaced atomically. Plan for it like a storage move:

- **Time:** it's bound by disk speed and CPU; a DS library of several hundred games at **High** can take hours, **Store** is the fastest. The job holds the job lock throughout, so ingest, integrity, DAT refresh, edits and save uploads wait (or get "Another job is running") until it ends; cancel it from the Jobs page if needed, and use **Repack again** later.
- **Disk:** each game needs free space on the library volume of about its uncompressed ROMs plus its current ZIP plus 64 MiB while it's rewritten (a game without it is skipped with `InsufficientSpace`). Going to **Store** also grows the library by roughly what compression saved.
- **Interrupted runs:** a cancel, crash or restart leaves some ZIPs at the old level; all stay readable. A leftover `*.zip.tmp` is removed at the next start.

## External ROM storage (Nintendo Switch, Nintendo DS, Nintendo 3DS and disc systems)

Switch ROMs, Nintendo DS ROMs, Nintendo 3DS ROMs and disc images (PlayStation, PSP, GameCube, Wii, Xbox 360, Saturn, Sega CD, 3DO and TurboGrafx-CD) are files next to their game's ZIP in `/app/roms/<system>/<letter>/` (see [ZIP format](zip-format.md#external-rom-files)); a disc set's cue and tracks sit there under their exact names. The app never overwrites a file it didn't create and never follows a link: every path is checked below its configured root, and a linked or special file or folder is reported and left alone.

**Requirements**

- **No-overwrite publication.** Switch imports and repairs can use a no-replace rename (`RENAME_NOREPLACE` on Linux) or hard links to publish a ROM or new ZIP and to set aside or restore an existing ROM during repair. If both operations fail, publication returns `IoError`. For a ROM move, that leaves the source in place. The same publication requirement applies to the ingest filesystem if a ROM needs to be moved back after a ZIP write fails.
- **procfs on Linux.** Checked reads open each file through `/proc/self/fd`, so `/proc` must be mounted (it is in every normal container). Without it external reads fail with an IO error instead of falling back to an unchecked open.
- **Database.** The ZIP-path conflict check uses a session temporary table; the `GRANT ALL` in [Using an existing MariaDB server](#using-an-existing-mariadb-server) already covers it.

**What the app leaves behind, and what to do**

- **Staging files.** Every move into place goes through an exclusively created staging name. If the initial rename crosses volumes or the filesystem rejects no-replace rename, the app copies to `<name>.tmp`, verifies the copy (length, and SHA1 when known), and publishes it under the final name before removing the source. A crash can leave a staging file, or it can leave both copies if it happens after publication and before source removal. Integrity reports unreferenced files in the library as `OrphanFile`. Check whether the original is still in `ingest/switch/` before recovering a staged copy.
- **Failed rollbacks.** When a ZIP write fails, the file is moved back to its ingest path, or to a ` (2)`-style sibling when that path is taken; the item's details name the actual surviving path. A rollback copy left directly in `ingest/<system>/` is picked up by the next ingestion, which moves it to `_failed/` with an `.error.txt` (bytes kept); rename it back and ingest it again. If the Review item couldn't be pointed at the new name, the response is `DbWriteFailed` naming both paths: move the copy back to its original path once that is free.
- **Repair backups.** A repair keeps the original ROM under a reserved `.repair.tmp` name until the ZIP and database updates succeed. If the ZIP write fails, the app attempts to restore the original without overwriting anything at its former path. If restoration is blocked or the database update fails, the item details identify the verified original's surviving path.
- **Archived members.** A Switch file from an archive is extracted to `cache/work/` and moved from there. If the ZIP write then fails and the archive has changed or vanished meanwhile, the verified copy is kept in the library folder as an unlisted file (`OrphanFile`) instead of being deleted. Move it to `ingest/switch/` to ingest it again.
- **Pointer and path conflicts.** If two games list the same file in one folder, or two ZIP paths count as equal in the database (case or accent differences), integrity reports `EntryMissing` / `ZipUnreadable` with the conflict in the details, skips those games and changes nothing. Fix the ZIPs' `externalFileName` pointers (or rename one ZIP) by hand, then run **Integrity → Run quick**.
- **Interrupted disc sets.** A crash while a bin/cue set is being placed can leave the members already moved next to the ZIP with no ZIP listing them; integrity reports them as `OrphanFile`. Move them back to `ingest/<system>/` beside the rest of the set and ingest again.
- **Interrupted edits.** If a title edit, move or merge is cut off, both the old and new ZIP can survive: integrity reports `DuplicateGameId` or `DuplicateSha1` and keeps the existing rows, and a file named by the losing ZIP can show as `OrphanFile`. Stop writes, back up both ZIPs and their files, keep the ZIP whose pointers match the files on disk (for a completed move or merge: the target, after checking it lists the moved releases), move the obsolete ZIP outside `roms/`, and run integrity. Keep orphan bytes until both ZIPs have been checked.
- **Multi-release moves.** Before moving one release out of a game with several, the app writes a verified copy of the source ZIP as `<name>.move-backup.tmp` plus a recovery record `<name>.move-backup.tmp.json` (format version 1: backup SHA256, both game IDs and ZIP paths, the moved SHA1 and both pointers), and removes them when the move finishes. If they survive a crash, use the pair to restore whichever releases are missing from the source or target ZIP, after checking the record matches the backup; a backup's presence alone doesn't mean the move failed. A `.partial` file is an interrupted copy: never use it, and delete it once nothing is running.
- **DAT publication.** A DAT refresh builds and checks the whole new set in `cache/work/<job>/` before swapping it in through a fixed backup, `cache/dats.previous/`. If the app stops mid-swap, startup puts the previous set back (or keeps the new one if the swap had finished) and removes the backup. A linked or non-folder `cache/dats` or `cache/dats.previous` is never moved or deleted; fix it by hand. The DAT manifest is read only up to 1 MiB and the titledb file up to 256 MiB, through the same checked reads.

## Genesis Plus GX core

Genesis and Sega CD play need three Genesis Plus GX files you supply (Sega CD also needs a regional BIOS in `/app/bios`, see [volumes](#volumes-and-permissions)) ([configuration](configuration.md#genesis-plus-gx-core) lists their sizes and SHA-256). They come from the npm package `@emulatorjs/core-genesis_plus_gx@4.2.3`: the two `.data` payloads are at the package root and the report is `reports/genesis_plus_gx.json`; copy all three to one folder, the report at its root too. Follow the core's own licence terms. Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - ./data/genesis:/app/genesis:ro
    environment:
      Emulator__GenesisCorePath: /app/genesis
```

The app runs as UID 1654, which needs read access to the folder and files (e.g. `chmod -R a+rX data/genesis`). Keep the folder outside anything the app or its users can write to: EmulatorJS runs the JavaScript inside these files in the app's origin, and the hash check only accepts the exact 4.2.3 files. No restart is needed to add or replace files once the setting is there. Without the setting (the default) the API serves no Genesis files, and Play on a Genesis game explains that the core needs setting up.

## PicoDrive core

32X play needs three PicoDrive files you supply ([configuration](configuration.md#picodrive-core) lists their sizes and SHA-256). They come from the npm package `@emulatorjs/core-picodrive@4.2.3`: the two `.data` payloads are at the package root and the report is `reports/picodrive.json`; copy all three to one folder, the report at its root too. Follow the core's own licence terms (its payloads' `license.txt` forbids selling it or using it commercially). Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - ./data/picodrive:/app/picodrive:ro
    environment:
      Emulator__PicodriveCorePath: /app/picodrive
```

The same advice as for Genesis Plus GX applies: the app's UID 1654 needs read access (e.g. `chmod -R a+rX data/picodrive`), and the folder must stay outside anything the app or its users can write to, since EmulatorJS runs the JavaScript inside these files in the app's origin. You may also put the PicoDrive files in the Genesis folder and point both settings at it. Without the setting, Play on a 32X game explains that the core needs setting up.

## Snes9x core

SNES play needs three Snes9x files you supply ([configuration](configuration.md#snes9x-core) lists their sizes and SHA-256). Follow the core's own licence terms: both payloads' `license.txt` is the Snes9x licence, which forbids commercial use ([Snes9x licence](https://github.com/snes9xgit/snes9x/blob/master/LICENSE)). Get the exact archive in a scratch folder outside the app and the repository (this downloads it without installing anything):

```bash
mkdir -p ~/snes9x-core && cd ~/snes9x-core
npm pack @emulatorjs/core-snes9x@4.2.3
tar -xzf emulatorjs-core-snes9x-4.2.3.tgz
```

Copy the two payloads from `package/` and the report from `package/reports/` to the root of the folder you'll mount, then check all three hashes before setting the path:

```bash
mkdir -p /srv/rnrommanager/snes9x
cp package/snes9x-wasm.data package/snes9x-legacy-wasm.data package/reports/snes9x.json /srv/rnrommanager/snes9x/
cd /srv/rnrommanager/snes9x && sha256sum snes9x-wasm.data snes9x-legacy-wasm.data snes9x.json
```

The output must be exactly:

```text
eaa0bcfce67673809886e50387a80a616b719502175db64c090d04c9d75958ee  snes9x-wasm.data
7d427a575cefad98ff400493fa1d7e892da63fe7bab68979babd9cea0bfaaf3b  snes9x-legacy-wasm.data
dc7ac963eb7935a7ac78956235ac0b8912ec785c57026336825aa2ed8031b3ad  snes9x.json
```

Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - /srv/rnrommanager/snes9x:/app/snes9x:ro
    environment:
      Emulator__Snes9xCorePath: /app/snes9x
```

The same advice as for Genesis Plus GX applies: the app's UID 1654 needs read access (e.g. `chmod -R a+rX /srv/rnrommanager/snes9x`), and the folder must stay outside anything the app or its users can write to (not under `data/`'s library, ingest, cache or backup folders), since EmulatorJS runs the JavaScript inside these files in the app's origin. You may also put the Snes9x files in the Genesis or PicoDrive folder and point the settings at it. Without the setting, Play on a SNES game explains that the core needs setting up.

## Opera core

3DO play needs three Opera files you supply ([configuration](configuration.md#opera-core) lists their sizes and SHA-256). Follow the core's own licence terms: Opera's libretro core info gives its licence as "LGPL/Non-commercial" ([core info](https://github.com/libretro/libretro-core-info/blob/master/opera_libretro.info)); the payloads carry no `license.txt` of their own. Get the exact archive in a scratch folder outside the app and the repository (this downloads it without installing anything):

```bash
mkdir -p ~/opera-core && cd ~/opera-core
npm pack @emulatorjs/core-opera@4.2.3
tar -xzf emulatorjs-core-opera-4.2.3.tgz
```

Copy the two non-threaded payloads from `package/` and the report from `package/reports/` to the root of the folder you'll mount (the archive's `-thread` payloads aren't used), then check all three hashes before setting the path:

```bash
mkdir -p /srv/rnrommanager/opera
cp package/opera-wasm.data package/opera-legacy-wasm.data package/reports/opera.json /srv/rnrommanager/opera/
cd /srv/rnrommanager/opera && sha256sum opera-wasm.data opera-legacy-wasm.data opera.json
```

The output must be exactly:

```text
3e737f4f739814c12c017a5f26cb1e43bdfe3ac6f2d3bf6e8972633df49e33d4  opera-wasm.data
41d3a7336cd304693c86974b50df5f7a90d9557077b01e0a1e928b3a38fe4f86  opera-legacy-wasm.data
0245b492b516ef4b1c0490e3f05dbeb1d74074ea767cd54fc34747e31b2a0203  opera.json
```

Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - /srv/rnrommanager/opera:/app/opera:ro
    environment:
      Emulator__OperaCorePath: /app/opera
```

The same advice as for Genesis Plus GX applies: the app's UID 1654 needs read access (e.g. `chmod -R a+rX /srv/rnrommanager/opera`), and the folder must stay outside anything the app or its users can write to (not under `data/`'s library, ingest, cache or backup folders), since EmulatorJS runs the JavaScript inside these files in the app's origin. You may also put the Opera files in another operator core's folder and point the settings at it. 3DO also needs at least one BIOS in `/app/bios` ([volumes](#volumes-and-permissions)); none is included. Without the setting, Play on a 3DO game explains that the core needs setting up.

## VICE core

Commodore 64 play needs three VICE files you supply ([configuration](configuration.md#vice-core) lists their sizes and SHA-256). VICE is GPL-2.0-or-later (each payload carries its `license.txt`), and its binary embeds Commodore's copyrighted system ROMs, which is why the image doesn't include it: follow both sets of terms. Get the exact archive in a scratch folder outside the app and the repository (this downloads it without installing anything):

```bash
mkdir -p ~/vice-core && cd ~/vice-core
npm pack @emulatorjs/core-vice_x64sc@4.2.3
tar -xzf emulatorjs-core-vice_x64sc-4.2.3.tgz
```

Copy the two non-threaded payloads from `package/` and the report from `package/reports/` to the root of the folder you'll mount (the archive's `-thread` payloads aren't used), then check all three hashes before setting the path:

```bash
mkdir -p /srv/rnrommanager/vice
cp package/vice_x64sc-wasm.data package/vice_x64sc-legacy-wasm.data package/reports/vice_x64sc.json /srv/rnrommanager/vice/
cd /srv/rnrommanager/vice && sha256sum vice_x64sc-wasm.data vice_x64sc-legacy-wasm.data vice_x64sc.json
```

The output must be exactly:

```text
77f58884c81b58721cbc4754ffc5574838219258e9390630c352a494a3f335ab  vice_x64sc-wasm.data
8a22bc81c0dd7104cd23ceec5117a9f47ca9503e280e4fbc9d0346f51b75253e  vice_x64sc-legacy-wasm.data
5c50bc4ff5546a2c6fc4bb7949efd200ea0ac6b4723713e742fb659228e9a3aa  vice_x64sc.json
```

For Commodore PET, do the same with `@emulatorjs/core-vice_xpet@4.2.3` (`vice_xpet-wasm.data`, `vice_xpet-legacy-wasm.data`, `reports/vice_xpet.json`) into the same folder; the hashes must match [configuration](configuration.md#vice-core).

For Commodore VIC-20, likewise with `@emulatorjs/core-vice_xvic@4.2.3` (`vice_xvic-wasm.data`, `vice_xvic-legacy-wasm.data`, `reports/vice_xvic.json`).

Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - /srv/rnrommanager/vice:/app/vice:ro
    environment:
      Emulator__ViceCorePath: /app/vice
```

The same advice as for Genesis Plus GX applies: the app's UID 1654 needs read access (e.g. `chmod -R a+rX /srv/rnrommanager/vice`), and the folder must stay outside anything the app or its users can write to (not under `data/`'s library, ingest, cache or backup folders), since EmulatorJS runs the JavaScript inside these files in the app's origin. You may also put the VICE files in another operator core's folder and point the settings at it. No BIOS is needed. Without the setting, Play on a Commodore 64 game explains that the core needs setting up.

## Mupen64Plus-Next core

Nintendo 64 play needs three Mupen64Plus-Next files you supply ([configuration](configuration.md#mupen64plus-next-core) lists their sizes and SHA-256 and why the image doesn't include them). Each payload carries its `license.txt` (GPL-2.0). Get the exact archive in a scratch folder outside the app and the repository:

```bash
mkdir -p ~/n64-core && cd ~/n64-core
npm pack @emulatorjs/core-mupen64plus_next@4.2.3
tar -xzf emulatorjs-core-mupen64plus_next-4.2.3.tgz
mkdir -p /srv/rnrommanager/n64
cp package/mupen64plus_next-wasm.data package/mupen64plus_next-legacy-wasm.data package/reports/mupen64plus_next.json /srv/rnrommanager/n64/
cd /srv/rnrommanager/n64 && sha256sum mupen64plus_next-wasm.data mupen64plus_next-legacy-wasm.data mupen64plus_next.json
```

The output must be exactly:

```
2da1cbce9fda395e3ae83ca5787353baa159142d45ef3ea90f108b92524f76cc  mupen64plus_next-wasm.data
425072f4bf94eec02633cbe9b84f47d6803dc0ec1b3c3b8de3ebbd2eb5617c3f  mupen64plus_next-legacy-wasm.data
270105553cec57fd1058c50e0541b8b89dbd30b2323fd70682036f6919b805cc  mupen64plus_next.json
```

Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - /srv/rnrommanager/n64:/app/n64:ro
    environment:
      Emulator__Mupen64PlusCorePath: /app/n64
```

The same advice as for Genesis Plus GX applies (read access for UID 1654, a folder nobody can write to). You may also put the files in another operator core's folder. No BIOS is needed.

## MAME 2003-Plus core

Arcade (MAME 2003-Plus) play needs three MAME 2003-Plus files you supply ([configuration](configuration.md#mame-2003-plus-core) lists their sizes and SHA-256 and why the image doesn't include them). Get the exact archive in a scratch folder outside the app and the repository:

```bash
mkdir -p ~/mame-core && cd ~/mame-core
npm pack @emulatorjs/core-mame2003_plus@4.2.3
tar -xzf emulatorjs-core-mame2003_plus-4.2.3.tgz
mkdir -p /srv/rnrommanager/mame
cp package/mame2003_plus-wasm.data package/mame2003_plus-legacy-wasm.data package/reports/mame2003_plus.json /srv/rnrommanager/mame/
cd /srv/rnrommanager/mame && sha256sum mame2003_plus-wasm.data mame2003_plus-legacy-wasm.data mame2003_plus.json
```

The output must be exactly:

```
cb6d9c80a88b65d1579d16d02128a678f8d1cd3f51de1479e647cea27b13247b  mame2003_plus-wasm.data
a01286082b7b2b83cc10e702afaf730f5effedb49e583a6ca73f59bc28b0c12a  mame2003_plus-legacy-wasm.data
bda548626942b8878beb6de3487043eea80e633bd53dd69e7bae7457373a48a1  mame2003_plus.json
```

Then mount the folder read-only and point the setting at it:

```yaml
    volumes:
      - /srv/rnrommanager/mame:/app/mame:ro
    environment:
      Emulator__Mame2003PlusCorePath: /app/mame
```

The same advice as for Genesis Plus GX applies (read access for UID 1654, a folder nobody can write to). You may also put the files in another operator core's folder. No BIOS goes in `Paths:Bios`: import BIOS sets such as `neogeo.zip` into `ingest/mame2003/` like any other set.

## Admin password recovery

If every Admin is locked out, reset a password from the host:

```bash
docker exec <container> /app/RnRomManager.Api --reset-password admin
```

It prints a new random password once, clears the lockout, re-enables the account, signs out its other sessions and requires a password change at the next sign-in. If no enabled Admin would otherwise remain, the account is also made an Admin. It works while the app is running. An unknown username exits with code 1.
