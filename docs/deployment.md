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
| `/app/roms` | the library: one ZIP per game (see [ZIP format](zip-format.md)) |
| `/app/ingest` | drop ROMs (and `.zip`/`.7z` archives) into `ingest/gb/` or `ingest/gbc/` (Game Boy / Color) `ingest/gba/` (Game Boy Advance) or `ingest/n64/` (Nintendo 64: `.z64`, `.v64` or `.n64`); `_failed/`, `_duplicates/` and `_removed/` hold files taken out of the way |
| `/app/logs` | `rnrommanager-yyyy-MM-dd.log`, kept for `Logging:RetentionDays` |
| `/app/cache` | downloaded DATs and temporary work folders |
| `/app/backups` | the daily database backups (see [Backup and restore](#backup-and-restore)); keep it private |

The container runs as UID 1654 unless you set `user:`.

- **Named volumes** work as they are: the image's folders are already owned by 1654.
- **Bind mounts** (as above) must be writable by the container's user. Either set `user: "<uid>:<gid>"` to the owner of `./data` (find it with `id -u` / `id -g`), or `chown -R 1654:1654 ./data/{roms,ingest,logs,cache,backups}`.

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

Use `ASPNETCORE_HTTP_PORTS` to change the listen port, not `ASPNETCORE_URLS`: the container health check reads the first port from `ASPNETCORE_HTTP_PORTS`.

## Health checks

- `GET /health/live`: the process is up (used by the image's `HEALTHCHECK`).
- `GET /health/ready`: the database answers within 2 seconds and the five folders exist; 503 otherwise.

Both are anonymous and return no details.

## Backup and restore

### What to back up

- **The database**, which holds users, the Data Protection keys that keep sign-ins valid across restarts, job history, the review queue, integrity issues, the audit trail and settings. The app backs it up itself (below).
- **`/app/roms`**, the library itself, including release notes. Stop the app (or make sure no job is running) for a consistent copy.
- **`/app/backups`**, so the database backups survive the loss of the host. Copy it somewhere else.

The library ZIPs are the source of truth for games: if the database is lost but the ZIPs are not, a new database is rebuilt from them by the next ingestion or **Integrity → Run quick** (accounts and history are not). `/app/ingest` holds anything not yet ingested plus `_removed/`; back it up too if you keep files there. `/app/cache` can be re-downloaded with **Refresh DATs**.

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

After upgrading to a version that adds a system (such as the ones that add Game Boy Advance and Nintendo 64), ingestion refuses to start with `DatsMissing` until an Admin runs **Admin → DATs → Refresh DATs** to download the new system's DATs.

On `docker compose stop` a running job gets 30 seconds to finish its current item (`stop_grace_period: 35s` allows for that). A job that's cut off is marked `Aborted` at the next start and its leftovers are cleaned up; just start it again.

## Admin password recovery

If every Admin is locked out, reset a password from the host:

```bash
docker exec <container> /app/RnRomManager.Api --reset-password admin
```

It prints a new random password once, clears the lockout, re-enables the account, signs out its other sessions and requires a password change at the next sign-in. If no enabled Admin would otherwise remain, the account is also made an Admin. It works while the app is running. An unknown username exits with code 1.
