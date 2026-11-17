-- User Management S6: per-owner save counts, so a Site user's library badge and Saves tab count only their own saves.
-- The library mirror fills a game's rows from its saves/saves.json whenever it rewrites the game row; nothing backfills
-- them, so a game's rows appear only when its ZIP is next written or found changed by a sync.
CREATE TABLE game_user_save_counts (
    game_id    CHAR(36)    NOT NULL,
    username   VARCHAR(64) NOT NULL,
    save_count INT         NOT NULL,
    PRIMARY KEY (game_id, username),
    CONSTRAINT fk_game_user_save_counts_game FOREIGN KEY (game_id) REFERENCES games (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
