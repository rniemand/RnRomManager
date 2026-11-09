-- Game collections (B1–B2): a user's named groups of games. Names are unique per user, case-insensitive but
-- accent-sensitive like games.zip_path. collection_games.game_id has no FK on purpose (B17): the library mirror
-- deletes and re-inserts a game's row on every edit, which would cascade the memberships away.
CREATE TABLE collections (
    id               CHAR(36)     NOT NULL,
    user_id          INT          NOT NULL,
    name             VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci NOT NULL,
    notes            TEXT         NULL,
    cover_format     VARCHAR(8)   NULL,
    cover_updated_at DATETIME(6)  NULL,
    created_at       DATETIME(6)  NOT NULL,
    updated_at       DATETIME(6)  NOT NULL,
    CONSTRAINT pk_collections PRIMARY KEY (id),
    CONSTRAINT ux_collections_user_name UNIQUE (user_id, name),
    CONSTRAINT fk_collections_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE collection_games (
    collection_id CHAR(36)    NOT NULL,
    game_id       CHAR(36)    NOT NULL,
    added_at      DATETIME(6) NOT NULL,
    CONSTRAINT pk_collection_games PRIMARY KEY (collection_id, game_id),
    CONSTRAINT fk_collection_games_collection FOREIGN KEY (collection_id) REFERENCES collections (id) ON DELETE CASCADE,
    INDEX ix_collection_games_game (game_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
