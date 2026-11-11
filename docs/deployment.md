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
| `/app/roms` | the library: one ZIP per game (see [ZIP format](zip-format.md)), plus the Nintendo Switch ROM files stored next to their ZIPs |
| `/app/ingest` | drop ROMs (and `.zip`/`.7z` archives) into `ingest/gb/` or `ingest/gbc/` (Game Boy / Color) `ingest/gba/` (Game Boy Advance), `ingest/n64/` (Nintendo 64: `.z64`, `.v64` or `.n64`) `ingest/nes/` (NES: headered `.nes` only) `ingest/snes/` (SNES: `.sfc` or `.smc`, copier header stripped) `ingest/a2600/` (Atari 2600: `.a26` or `.bin`) `ingest/coleco/` (ColecoVision: `.col`, `.rom` or `.bin`) `ingest/gg/` (Game Gear: `.gg` or `.sms`) `ingest/sms/` (Master System: `.sms`) `ingest/md/` (Mega Drive / Genesis: `.md`, `.bin` or `.gen`) `ingest/s32x/` (32X: `.32x`) `ingest/tg16/` (TurboGrafx-16 / PC Engine: `.pce`, copier header stripped) or `ingest/switch/` (Nintendo Switch: `.nsp`, `.xci`, `.nsz` or `.xcz`, stored as files next to the ZIP; see [External ROM storage](#external-rom-storage-nintendo-switch)); `_failed/`, `_duplicates/` and `_removed/` hold files taken out of the way |
| `/app/logs` | `rnrommanager-yyyy-MM-dd.log`, kept for `Logging:RetentionDays` |
| `/app/cache` | downloaded DATs and temporary work folders |
| `/app/backups` | the daily database backups (see [Backup and restore](#backup-and-restore)); keep it private |
| `/app/collections` | collection cover images (`<collection id>.png` or `.jpg`) |

An existing deployment that doesn't map `/app/collections` still starts (the folder is created), but covers then live in an anonymous Docker volume and are lost when the container is recreated: add the mapping when you upgrade.

The container runs as UID 1654 unless you set `user:`.

- **Named volumes** work as they are: the image's folders are already owned by 1654.
- **Bind mounts** (as above) must be writable by the container's user. Either set `user: "<uid>:<gid>"` to the owner of `./data` (find it with `id -u` / `id -g`), or `chown -R 1654:1654 ./data/{roms,ingest,logs,cache,backups,collections}`.

At startup the app writes and deletes a `.rnrm-probe` file in each of the five folders. If that fails, it stops with a `FATAL` line naming the path and the UID/GID it runs as.

## Time zone

`TZ` sets the time zone for the daily quick integrity check (`Integrity:QuickCheckTime`, default `03:00`) and the daily database backup (**Admin → Backups**, default `03:30`). The image includes the time zone database. Times in the UI are always shown in the browser's own time zone.

## Reverse proxy

The app speaks plain HTTP on 8080. Behind a TLS reverse proxy (Caddy, Traefik, nginx, …), set:

```yaml
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
```

so the app sees the original scheme (for `Secure` cookies) and client address. **Only set it when port 8080 can't be reached except through the proxy** (e.g. remove the `ports:` mapping and put the proxy on the same Docker network). The setting trusts `X-Forwarded-For` from any sender, so with the port open anyone could fake their address and get around the login rate limit (10 attempts per minute per address).

The live log (**Admin → Logs**) is a long-lived server-sent event stream at `/api/admin/logs/stream`. The app turns off response buffering and sends a keep-alive every 15 seconds, but the proxy must not buffer it either and must allow an idle read of more than 15 seconds. nginx needs `proxy_buffering off;` and a `proxy_read_timeout` above 15 s on that location; Caddy and Traefik stream it as it is.

Resolving a Switch Review item, or moving or deleting a Switch release, can copy a multi-GB file when `/app/ingest` and `/app/roms` are on different volumes. The request stays open until that finishes, so raise the proxy's read timeout for `/api/` (nginx `proxy_read_timeout`, e.g. `600s`); otherwise the browser gets a gateway error while the action still completes in the background. Reload the page before trying again.

Use `ASPNETCORE_HTTP_PORTS` to change the listen port, not `ASPNETCORE_URLS`: the container health check reads the first port from `ASPNETCORE_HTTP_PORTS`.

## Outbound HTTP

Besides the DAT downloads (libretro-database and the [titledb](https://github.com/blawar/titledb) file for Switch, `Sources:TitleDbUrl`, about 90 MB and capped at 256 MiB) and the thumbnail downloads (libretro-thumbnails, and for Switch images from `img-eshop.cdn.nintendo.net` only, with redirects refused), **Upload image → URL** makes the server fetch an image from an address a ROM manager types. That download connects directly (it ignores `HTTP_PROXY`/`HTTPS_PROXY`), only to public internet addresses (private, loopback, link-local and similar ranges are refused on every redirect hop), with a 15-second limit and at most 8 MiB. On a host that can only reach the internet through a mandatory egress proxy, the URL option fails with "couldn't connect"; uploading a file still works.

## Health checks

- `GET /health/live`: the process is up (used by the image's `HEALTHCHECK`).
- `GET /health/ready`: the database answers within 2 seconds and the five folders exist; 503 otherwise.

Both are anonymous and return no details.

## Backup and restore

### What to back up

- **The database**, which holds users, the Data Protection keys that keep sign-ins valid across restarts, job history, the review queue, integrity issues, the audit trail and settings. The app backs it up itself (below).
- **`/app/roms`**, the library itself, including release notes and the Switch ROM files next to their ZIPs. Stop the app (or make sure no job is running) for a consistent copy.
- **`/app/backups`**, so the database backups survive the loss of the host. Copy it somewhere else.
- **`/app/collections`**, the collection cover images. Collections themselves are in the database (and its backups), but their cover files are not: copy this folder with the backups. After a restore without it, collections show a placeholder until a cover is set again.

The library ZIPs, together with the external ROM files next to them in `/app/roms`, are the source of truth for games: if the database is lost but they are not, a new database is rebuilt from them by the next ingestion or **Integrity → Run quick** (accounts and history are not). `/app/ingest` holds anything not yet ingested plus `_removed/`; back it up too if you keep files there. `/app/cache` can be re-downloaded with **Refresh DATs**.

### Database backups

Every day at **03:30** (container time, `TZ`) the app writes `rnrommanager-<yyyyMMdd-HHmmss>.zip` (UTC start time) to `Paths:Backups` and keeps the newest **30**. Change the time, the number kept, or turn it off on **Admin → Backups**; **Back up now** runs one at once. Missed runs (the app was down) aren't caught up.

- A backup is a ZIP with `manifest.json` (format version, backup id, time, app version, the migrations applied and each table's row count), `schema.sql` (`SHOW CREATE TABLE` for every table) and `data/<table>.jsonl` (one JSON object per row). It's read inside one consistent snapshot, so jobs and edits keep running.
- It's written to `<name>.zip.tmp` and renamed only when complete; leftover `.tmp` files from a crash are deleted before the next run. Two backups in the same second get ` (2)`, ` (3)`… before `.zip`.
- Only files named `rnrommanager-*.zip` are ever deleted. A backup that was restored (it has a `.restored` file next to it) or is named by `Backup__RestoreFrom` is never deleted and doesn't count toward the number kept.
- **Backups contain password hashes and the Data Protection keys.** Keep the folder private; the app never serves backup files over HTTP.

`mariadb-dump rnrommanager` still works as an extra backup; it isn't needed for a restore.

### Restoring a backup

A restore replaces **every table** in the database with the backup's contents. It can't be undone.

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

## Upgrades

Back up the database (**Admin → Backups → Back up now**) and `roms/`, then pull the new tag and restart:

```bash
docker compose pull
docker compose up -d
```

Migrations run automatically on start. `info.json` files in older formats are upgraded in memory and rewritten in the new format the next time each game is written. There is no downgrade path: an older app refuses to touch ZIPs written in a newer format.

Upgrading to 0.3.0:

- **Everyone is signed out once.** Roles change from a single Admin/User to the `admin` and `rom-manager` roles; every Admin gets both and every User none, and every session ends so the new roles take effect. Sign in again.
- **ZIPs move to `info.json` version 3** as each game is next written (cover flag, uploaded and JPEG images). An app older than 0.3.0 can still read those ZIPs but refuses to change them (`InfoJsonNewerSchema`), and the database can't be downgraded either: restore the backup to go back.

Upgrading to 0.4.0:

- **Refresh DATs after upgrading.** Nintendo Switch is added and identified through titledb; ingestion refuses to start with `DatsMissing` until **Admin → DATs → Refresh DATs** has downloaded its titledb file. `ingest/switch/` is created at startup.
- **ZIPs move to `info.json` version 4** as each game is next written (`externalFileName`, `TitleDb`, `titledb` images). Apps before 0.4.0 can read them but refuse to change them.
- **Plan disk space** for Switch dumps (often several GB each). Keep `/app/ingest` and `/app/roms` on the same volume where you can: placement is then an instant rename; across volumes each file is copied and needs free space for a second copy while it moves.
- **To be able to downgrade,** take a database backup (**Back up now**) and a filesystem snapshot of `/app/roms` (and `/app/ingest`) at the same moment before upgrading, and restore **both** to go back: the database alone doesn't know where external files were.

After upgrading to a version that adds a system (such as the ones that add Game Boy Advance, Nintendo 64, NES, SNES, Atari 2600, ColecoVision, Game Gear, Master System, Mega Drive / Genesis, 32X, TurboGrafx-16 and Nintendo Switch), ingestion refuses to start with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs** to download the new system's DATs.

On `docker compose stop` a running job gets 30 seconds to finish its current item (`stop_grace_period: 35s` allows for that). A job that's cut off is marked `Aborted` at the next start and its leftovers are cleaned up; just start it again.

## External ROM storage (Nintendo Switch)

Switch ROMs are files next to their game's ZIP in `/app/roms/switch/<letter>/` (see [ZIP format](zip-format.md#external-rom-files)). The app never overwrites a file it didn't create and never follows a link: every path is checked below its configured root, and a linked or special file or folder is reported and left alone.

**Requirements**

- **No-overwrite publication.** Switch imports can publish a ROM or a new ZIP on a filesystem that supports either a no-replace rename (`RENAME_NOREPLACE` on Linux) or hard links. If both operations fail, publication returns `IoError`. For a ROM move, that leaves the source in place. The same publication requirement applies to the ingest filesystem if a ROM needs to be moved back after a ZIP write fails. Operations that set aside an existing ROM for repair still require a no-replace rename.
- **procfs on Linux.** Checked reads open each file through `/proc/self/fd`, so `/proc` must be mounted (it is in every normal container). Without it external reads fail with an IO error instead of falling back to an unchecked open.
- **Database.** The ZIP-path conflict check uses a session temporary table; the `GRANT ALL` in [Using an existing MariaDB server](#using-an-existing-mariadb-server) already covers it.

**What the app leaves behind, and what to do**

- **Staging files.** Every move into place goes through an exclusively created staging name. If the initial rename crosses volumes or the filesystem rejects no-replace rename, the app copies to `<name>.tmp`, verifies the copy (length, and SHA1 when known), and publishes it under the final name before removing the source. A crash can leave a staging file, or it can leave both copies if it happens after publication and before source removal. Integrity reports unreferenced files in the library as `OrphanFile`. Check whether the original is still in `ingest/switch/` before recovering a staged copy.
- **Failed rollbacks.** When a ZIP write fails, the file is moved back to its ingest path, or to a ` (2)`-style sibling when that path is taken; the item's details name the actual surviving path. A rollback copy left directly in `ingest/<system>/` is picked up by the next ingestion, which moves it to `_failed/` with an `.error.txt` (bytes kept); rename it back and ingest it again. If the Review item couldn't be pointed at the new name, the response is `DbWriteFailed` naming both paths: move the copy back to its original path once that is free.
- **Archived members.** A Switch file from an archive is extracted to `cache/work/` and moved from there. If the ZIP write then fails and the archive has changed or vanished meanwhile, the verified copy is kept in the library folder as an unlisted file (`OrphanFile`) instead of being deleted. Move it to `ingest/switch/` to ingest it again.
- **Pointer and path conflicts.** If two games list the same file in one folder, or two ZIP paths count as equal in the database (case or accent differences), integrity reports `EntryMissing` / `ZipUnreadable` with the conflict in the details, skips those games and changes nothing. Fix the ZIPs' `externalFileName` pointers (or rename one ZIP) by hand, then run **Integrity → Run quick**.
- **Interrupted edits.** If a title edit, move or merge is cut off, both the old and new ZIP can survive: integrity reports `DuplicateGameId` or `DuplicateSha1` and keeps the existing rows, and a file named by the losing ZIP can show as `OrphanFile`. Stop writes, back up both ZIPs and their files, keep the ZIP whose pointers match the files on disk (for a completed move or merge: the target, after checking it lists the moved releases), move the obsolete ZIP outside `roms/`, and run integrity. Keep orphan bytes until both ZIPs have been checked.
- **Multi-release moves.** Before moving one release out of a game with several, the app writes a verified copy of the source ZIP as `<name>.move-backup.tmp` plus a recovery record `<name>.move-backup.tmp.json` (format version 1: backup SHA256, both game IDs and ZIP paths, the moved SHA1 and both pointers), and removes them when the move finishes. If they survive a crash, use the pair to restore whichever releases are missing from the source or target ZIP, after checking the record matches the backup; a backup's presence alone doesn't mean the move failed. A `.partial` file is an interrupted copy: never use it, and delete it once nothing is running.
- **DAT publication.** A DAT refresh builds and checks the whole new set in `cache/work/<job>/` before swapping it in through a fixed backup, `cache/dats.previous/`. If the app stops mid-swap, startup puts the previous set back (or keeps the new one if the swap had finished) and removes the backup. A linked or non-folder `cache/dats` or `cache/dats.previous` is never moved or deleted; fix it by hand. The DAT manifest is read only up to 1 MiB and the titledb file up to 256 MiB, through the same checked reads.

## Admin password recovery

If every Admin is locked out, reset a password from the host:

```bash
docker exec <container> /app/RnRomManager.Api --reset-password admin
```

It prints a new random password once, clears the lockout, re-enables the account, signs out its other sessions and requires a password change at the next sign-in. If no enabled Admin would otherwise remain, the account is also made an Admin. It works while the app is running. An unknown username exits with code 1.
