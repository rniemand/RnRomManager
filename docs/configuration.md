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
| `Paths:Bios` | `Paths__Bios` | `/app/bios` | as above. A dedicated folder for emulator BIOS files the browser play page needs (`colecovision.rom` for ColecoVision, required; `scph5501.bin` for PlayStation, optional: exactly 512 KiB, otherwise the core's HLE BIOS is used; for 3DO, required, at least one of `panafz10.bin`, `panafz1.bin`, `panafz1j.bin`, `goldstar.bin` or `sanyotry.bin`, each exactly 1 MiB); you copy them in. Names are matched ignoring case. Any validly named file in it can be downloaded by a signed-in user, so keep only BIOS files there. Not included in database backups: keep your own copy. |
| `Admin:InitialPassword` | `Admin__InitialPassword` | *(empty)* | empty, or 12–128 characters and not `admin`. Used only when the first `admin` user is created; if empty, a random password is printed to the console once. |
| `Integrity:QuickCheckTime` | `Integrity__QuickCheckTime` | `03:00` | `HH:mm` (24 h, container local time from `TZ`) or empty to disable the daily quick check. Missed runs are not caught up. On a daylight-saving change, a time that is skipped runs at the first valid minute after it, and a time that happens twice runs once. If another job is running at that moment, the check is skipped (logged at `Info`). |
| `Logging:MinLevel` | `Logging__MinLevel` | `Debug` | `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal` or `Off` (case-insensitive). Applies to the app's own loggers; framework loggers log `Warn` and above. |
| `Logging:RetentionDays` | `Logging__RetentionDays` | `14` | 1–365. `rnrommanager-*.log` files for UTC days more than this many days before today are deleted at startup and daily at 00:05 UTC. |
| `Sources:DatBaseUrl` | `Sources__DatBaseUrl` | `https://raw.githubusercontent.com/libretro/libretro-database/master/metadat` | absolute http(s) URL |
| `Sources:TitleDbUrl` | `Sources__TitleDbUrl` | `https://raw.githubusercontent.com/blawar/titledb/master/US.en.json` | absolute http(s) URL; the [blawar/titledb](https://github.com/blawar/titledb) file a DAT refresh downloads for systems identified by title ID (Nintendo Switch) |
| `Sources:ThumbnailBaseUrl` | `Sources__ThumbnailBaseUrl` | `https://thumbnails.libretro.com` | absolute http(s) URL, or empty to disable all media downloads, Switch's titledb images included (default box art only) |
| `Emulator:GenesisCorePath` | `Emulator__GenesisCorePath` | *(empty)* | empty (Genesis and Sega CD play off), or an absolute path to a read-only folder holding the three EmulatorJS 4.2.3 Genesis Plus GX files ([below](#genesis-plus-gx-core)). A relative path fails startup; a path that doesn't exist just means Genesis play isn't set up. Not created or probed, and not one of the paths that must differ. Shown on **Admin → Settings** as `emulator.genesisCorePath`. |
| `Emulator:PicodriveCorePath` | `Emulator__PicodriveCorePath` | *(empty)* | empty (32X play off), or an absolute path to a read-only folder holding the three EmulatorJS 4.2.3 PicoDrive files ([below](#picodrive-core)). It may be the same folder as `Emulator:GenesisCorePath`, since the file names differ. Validated and shown like `Emulator:GenesisCorePath` (`emulator.picodriveCorePath`). |
| `Emulator:Snes9xCorePath` | `Emulator__Snes9xCorePath` | *(empty)* | empty (SNES play off), or an absolute path to a read-only folder holding the three EmulatorJS 4.2.3 Snes9x files ([below](#snes9x-core)). It may be the same folder as the other operator cores, since the file names differ. Validated and shown like `Emulator:GenesisCorePath` (`emulator.snes9xCorePath`). |
| `Emulator:OperaCorePath` | `Emulator__OperaCorePath` | *(empty)* | empty (3DO play off), or an absolute path to a read-only folder holding the three EmulatorJS 4.2.3 Opera files ([below](#opera-core)). It may be the same folder as the other operator cores, since the file names differ. Validated and shown like `Emulator:GenesisCorePath` (`emulator.operaCorePath`). |
| `Emulator:ViceCorePath` | `Emulator__ViceCorePath` | *(empty)* | empty (Commodore 64 and PET play off), or an absolute path to a read-only folder holding the EmulatorJS 4.2.3 VICE files ([below](#vice-core)). It may be the same folder as the other operator cores, since the file names differ. Validated and shown like `Emulator:GenesisCorePath` (`emulator.viceCorePath`). |
| `Backup:RestoreFrom` | `Backup__RestoreFrom` | *(empty)* | empty, or the bare file name of a backup in `Paths:Backups` to restore at startup (no `/`, `\` or `..`). See [restoring a backup](deployment.md#restoring-a-backup). Remove it once the restore is done. |

## Paths
- The seven paths must be different, and none may be inside another (compared case-insensitively).
- At startup the app creates each folder and writes and deletes a `.rnrm-probe` file in it. If that fails, the error names the path and the user the process runs as (UID/GID on Linux), so you can fix the ownership of a bind mount.

## Genesis Plus GX core
Mega Drive / Genesis and Sega CD play with Genesis Plus GX, whose licence forbids commercial use, so the image doesn't contain it. To turn Genesis and Sega CD play on, put these three files from the EmulatorJS 4.2.3 build at the root of a folder and set `Emulator:GenesisCorePath` to it ([deployment](deployment.md#genesis-plus-gx-core) shows the mount):

| File | Bytes | SHA-256 |
|---|---|---|
| `genesis_plus_gx-wasm.data` | 1,203,661 | `190297a6f86757405090f1a2266f67dfe1a570a528c583434ed3641a5664f768` |
| `genesis_plus_gx-legacy-wasm.data` | 1,204,803 | `2a1edeb68d7ec882149ed3cd5d3aa95a6ac231ebabaf82cdb56175a8b62967cc` |
| `genesis_plus_gx.json` | 129 | `5936a8ce8d7f010d5bfdd8c3bba2b2414f103b3a703121e56f2724b24dbe7ff3` |

The app checks each file's exact size and SHA-256 before it serves any byte (`GET /api/emulator/cores/genesis_plus_gx/<file>`, or the older `/api/emulator/genesis/<file>`; signed-in users only), so a corrupt, altered or other-version file is refused with `409 GenesisCoreMismatch` and the play page says the files *don't match EmulatorJS 4.2.3*; the server log names the file. Replace that file with the right one (no restart needed). Missing, empty or unsafe files (links, names that differ only in case) count as not set up.

## PicoDrive core
32X plays with PicoDrive, whose licence also forbids commercial use, so the image doesn't contain it either. To turn 32X play on, put these three files from the EmulatorJS 4.2.3 build at the root of a folder and set `Emulator:PicodriveCorePath` to it ([deployment](deployment.md#picodrive-core) shows the mount):

| File | Bytes | SHA-256 |
|---|---|---|
| `picodrive-wasm.data` | 1,034,483 | `bb5d50b8b88111b583977d2f7a16d01a822b3deda9205048d99ecaad2c56d861` |
| `picodrive-legacy-wasm.data` | 1,035,609 | `cc42b0c95e9edfd0e5c689211e8e85e45f4525be2d814af8ca9331ef90e0620b` |
| `picodrive.json` | 123 | `2b09f29e095a003c15abf8e5bdfb0eaca96d6c6121183b3bd92ed85e860918d3` |

The checks are the same as for Genesis Plus GX (`GET /api/emulator/cores/picodrive/<file>`); a file that doesn't match is refused with `409 PicodriveCoreMismatch`. Mega Drive / Genesis keeps Genesis Plus GX: PicoDrive is never used for it.

## Snes9x core
SNES plays with Snes9x, whose licence forbids commercial use ([Snes9x licence](https://github.com/snes9xgit/snes9x/blob/master/LICENSE)), so the image doesn't contain it either. To turn SNES play on, put these three files from the [`@emulatorjs/core-snes9x@4.2.3`](https://registry.npmjs.org/@emulatorjs/core-snes9x/-/core-snes9x-4.2.3.tgz) npm archive at the root of a folder and set `Emulator:Snes9xCorePath` to it ([deployment](deployment.md#snes9x-core) shows how to get them and mount the folder):

| File | Bytes | SHA-256 |
|---|---|---|
| `snes9x-wasm.data` | 1,093,765 | `eaa0bcfce67673809886e50387a80a616b719502175db64c090d04c9d75958ee` |
| `snes9x-legacy-wasm.data` | 1,092,437 | `7d427a575cefad98ff400493fa1d7e892da63fe7bab68979babd9cea0bfaaf3b` |
| `snes9x.json` | 120 | `dc7ac963eb7935a7ac78956235ac0b8912ec785c57026336825aa2ed8031b3ad` |

The checks are the same as for Genesis Plus GX (`GET /api/emulator/cores/snes9x/<file>`); a file that doesn't match is refused with `409 Snes9xCoreMismatch`, and the play page says *The Snes9x core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:Snes9xCorePath).* Without the files it says *SNES games need the Snes9x core files: ask an Admin to set them up (Emulator:Snes9xCorePath).*

## Opera core
3DO plays with Opera, whose licence forbids commercial use ("LGPL/Non-commercial" in its [libretro core info](https://github.com/libretro/libretro-core-info/blob/master/opera_libretro.info)), so the image doesn't contain it either. To turn 3DO play on, put these three files from the [`@emulatorjs/core-opera@4.2.3`](https://registry.npmjs.org/@emulatorjs/core-opera/-/core-opera-4.2.3.tgz) npm archive at the root of a folder and set `Emulator:OperaCorePath` to it ([deployment](deployment.md#opera-core) shows how to get them and mount the folder):

| File | Bytes | SHA-256 |
|---|---|---|
| `opera-wasm.data` | 854,147 | `3e737f4f739814c12c017a5f26cb1e43bdfe3ac6f2d3bf6e8972633df49e33d4` |
| `opera-legacy-wasm.data` | 853,889 | `41d3a7336cd304693c86974b50df5f7a90d9557077b01e0a1e928b3a38fe4f86` |
| `opera.json` | 119 | `0245b492b516ef4b1c0490e3f05dbeb1d74074ea767cd54fc34747e31b2a0203` |

The archive's two `-thread` payloads aren't used. The checks are the same as for Genesis Plus GX (`GET /api/emulator/cores/opera/<file>`); a file that doesn't match is refused with `409 OperaCoreMismatch`, and the play page says *The Opera core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:OperaCorePath).* Without the files it says *3DO games need the Opera core files: ask an Admin to set them up (Emulator:OperaCorePath).* 3DO also needs a BIOS in `Paths:Bios` (above); none is included.

## VICE core
Commodore 64 plays with VICE (x64sc). Its licence is GPL-2.0-or-later, but its binary embeds Commodore's copyrighted system ROMs (KERNAL, BASIC, character set, drive DOS and printer ROMs), and the app ships no console BIOS, so the image doesn't contain it. To turn Commodore 64 play on, put these three files from the [`@emulatorjs/core-vice_x64sc@4.2.3`](https://registry.npmjs.org/@emulatorjs/core-vice_x64sc/-/core-vice_x64sc-4.2.3.tgz) npm archive at the root of a folder and set `Emulator:ViceCorePath` to it ([deployment](deployment.md#vice-core) shows how to get them and mount the folder):

| File | Bytes | SHA-256 |
|---|---|---|
| `vice_x64sc-wasm.data` | 1,523,457 | `77f58884c81b58721cbc4754ffc5574838219258e9390630c352a494a3f335ab` |
| `vice_x64sc-legacy-wasm.data` | 1,523,390 | `8a22bc81c0dd7104cd23ceec5117a9f47ca9503e280e4fbc9d0346f51b75253e` |
| `vice_x64sc.json` | 147 | `5c50bc4ff5546a2c6fc4bb7949efd200ea0ac6b4723713e742fb659228e9a3aa` |

Commodore PET plays with VICE (xpet), which embeds the PET's ROMs for the same reason. Its three files from [`@emulatorjs/core-vice_xpet@4.2.3`](https://registry.npmjs.org/@emulatorjs/core-vice_xpet/-/core-vice_xpet-4.2.3.tgz) go in the **same** `Emulator:ViceCorePath` folder (`GET /api/emulator/cores/vice_xpet/<file>`, the same `409 ViceCoreMismatch`); either set works without the other:

| File | Bytes | SHA-256 |
|---|---|---|
| `vice_xpet-wasm.data` | 1,368,677 | `eef0d83f7c1af43d51c3384a07db4196ea7c2bbe1292e9e52fade3d3107513c5` |
| `vice_xpet-legacy-wasm.data` | 1,368,450 | `98ac6fbb90824a77ae6480a431c465d41e8cecce39394e036648d57e86297361` |
| `vice_xpet.json` | 123 | `9bf5ec80ab1f58dc7cdccd73f0141385b030f262ce20aaae4403369dd7061301` |

The archive's two `-thread` payloads aren't used. The checks are the same as for Genesis Plus GX (`GET /api/emulator/cores/vice_x64sc/<file>`); a file that doesn't match is refused with `409 ViceCoreMismatch`, and the play page says *The VICE core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:ViceCorePath).* Without the files it says *Commodore 64 games need the VICE core files: ask an Admin to set them up (Emulator:ViceCorePath).* No BIOS is needed in `Paths:Bios`.

## Other settings
- **Port:** the standard `ASPNETCORE_HTTP_PORTS` (image default `8080`). The container health check reads the first port from it, so don't use `ASPNETCORE_URLS` instead.
- **Reverse proxy:** set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` only if the app's port can't be reached except through the proxy (see [deployment](deployment.md#reverse-proxy)).

## Backups
The backup schedule (on/off, daily time, how many to keep) isn't configuration: Admins set it on **Admin → Backups**, and it's stored in the database.

## Logs
Logs go to the console and to `rnrommanager-yyyy-MM-dd.log` in `Paths:Logs` (one file per UTC day). The effective configuration is logged at startup with secrets removed: the connection string's password is dropped and `Admin:InitialPassword` is shown as `***`.
