-- Admin actions A3: one row per game that has any value. cover_image_pulled_at mirrors info.json actions.coverImagePulled;
-- the IGDB columns live only here. No foreign key: the mirror deletes and re-inserts games rows on every write, which
-- would cascade the database-only IGDB columns away. The mirror and integrity remove rows of deleted games instead.
CREATE TABLE game_actions (
    game_id               CHAR(36)    NOT NULL PRIMARY KEY,
    cover_image_pulled_at DATETIME(6) NULL,
    last_igdb_sync_at     DATETIME(6) NULL,
    igdb_rating           DOUBLE      NULL,
    igdb_rating_count     INT         NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
