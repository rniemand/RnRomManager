# RnRomManager

A self-hosted web app for managing a personal backup collection of Game Boy and Game Boy Color ROMs.

Implementation is in progress. The full API is in place: ingestion with a review queue, library browsing and downloads, Admin edits (metadata, delete, move, merge), media retry, the integrity checker with its daily schedule, and Admin settings, log download and About. See the [feature notes](https://github.com/rniemand/RnRomManager/blob/main/docs/features.md) for details. The web UI is the next milestone.

For a reproducible local collection, use the [synthetic Basic/Full seed sets](https://github.com/rniemand/RnRomManager/blob/main/docs/development.md). They include matching fixture DATs and require no real ROMs or network downloads.

## Attribution

- Game identification and metadata come from [libretro-database](https://github.com/libretro/libretro-database), licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).
- Box art and screenshots are downloaded from [libretro-thumbnails](https://github.com/libretro-thumbnails/libretro-thumbnails). They remain the copyright of their respective owners and are stored for personal use only.
- Admin → About (`GET /api/about`) lists the licence of every dependency.
