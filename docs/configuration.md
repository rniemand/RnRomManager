# Configuration

All settings come from `appsettings.json` and can be overridden with environment variables (`:` becomes `__`). They are validated at startup: any invalid value logs a `FATAL` line naming the key and the app exits with code 1.

| Key | Env var | Default | Rules |
|---|---|---|---|
| `ConnectionStrings:Default` | `ConnectionStrings__Default` | *(required)* | non-empty. The app forces `DateTimeKind=Utc` and `GuidFormat=Char36`, so don't add them. The database must already exist. |
| `Paths:Roms` | `Paths__Roms` | `/app/roms` | absolute path; created if missing; must be writable |
| `Paths:Ingest` | `Paths__Ingest` | `/app/ingest` | as above |
| `Paths:Logs` | `Paths__Logs` | `/app/logs` | as above |
| `Paths:Cache` | `Paths__Cache` | `/app/cache` | as above |
| `Paths:Backups` | `Paths__Backups` | `/app/backups` | as above. Holds the database backups ([deployment](deployment.md#backup-and-restore)); keep it private, they contain password hashes and keys. |
| `Paths:Collections` | `Paths__Collections` | `/app/collections` | as above. Holds collection cover images (`<collection id>.png` or `.jpg`); not included in database backups. |
| `Paths:Bios` | `Paths__Bios` | `/app/bios` | as above. A dedicated folder for emulator BIOS files the browser play page needs (`colecovision.rom` for ColecoVision); you copy them in. Names are matched ignoring case. Any validly named file in it can be downloaded by a signed-in user, so keep only BIOS files there. Not included in database backups: keep your own copy. |
| `Admin:InitialPassword` | `Admin__InitialPassword` | *(empty)* | empty, or 12–128 characters and not `admin`. Used only when the first `admin` user is created; if empty, a random password is printed to the console once. |
| `Integrity:QuickCheckTime` | `Integrity__QuickCheckTime` | `03:00` | `HH:mm` (24 h, container local time from `TZ`) or empty to disable the daily quick check. Missed runs are not caught up. On a daylight-saving change, a time that is skipped runs at the first valid minute after it, and a time that happens twice runs once. If another job is running at that moment, the check is skipped (logged at `Info`). |
| `Logging:MinLevel` | `Logging__MinLevel` | `Debug` | `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal` or `Off` (case-insensitive). Applies to the app's own loggers; framework loggers log `Warn` and above. |
| `Logging:RetentionDays` | `Logging__RetentionDays` | `14` | 1–365. `rnrommanager-*.log` files for UTC days more than this many days before today are deleted at startup and daily at 00:05 UTC. |
| `Sources:DatBaseUrl` | `Sources__DatBaseUrl` | `https://raw.githubusercontent.com/libretro/libretro-database/master/metadat` | absolute http(s) URL |
| `Sources:TitleDbUrl` | `Sources__TitleDbUrl` | `https://raw.githubusercontent.com/blawar/titledb/master/US.en.json` | absolute http(s) URL; the [blawar/titledb](https://github.com/blawar/titledb) file a DAT refresh downloads for systems identified by title ID (Nintendo Switch) |
| `Sources:ThumbnailBaseUrl` | `Sources__ThumbnailBaseUrl` | `https://thumbnails.libretro.com` | absolute http(s) URL, or empty to disable all media downloads, Switch's titledb images included (default box art only) |
| `Backup:RestoreFrom` | `Backup__RestoreFrom` | *(empty)* | empty, or the bare file name of a backup in `Paths:Backups` to restore at startup (no `/`, `\` or `..`). See [restoring a backup](deployment.md#restoring-a-backup). Remove it once the restore is done. |

## Paths
- The seven paths must be different, and none may be inside another (compared case-insensitively).
- At startup the app creates each folder and writes and deletes a `.rnrm-probe` file in it. If that fails, the error names the path and the user the process runs as (UID/GID on Linux), so you can fix the ownership of a bind mount.

## Other settings
- **Port:** the standard `ASPNETCORE_HTTP_PORTS` (image default `8080`). The container health check reads the first port from it, so don't use `ASPNETCORE_URLS` instead.
- **Reverse proxy:** set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` only if the app's port can't be reached except through the proxy (see [deployment](deployment.md#reverse-proxy)).

## Backups
The backup schedule (on/off, daily time, how many to keep) isn't configuration: Admins set it on **Admin → Backups**, and it's stored in the database.

## Logs
Logs go to the console and to `rnrommanager-yyyy-MM-dd.log` in `Paths:Logs` (one file per UTC day). The effective configuration is logged at startup with secrets removed: the connection string's password is dropped and `Admin:InitialPassword` is shown as `***`.
