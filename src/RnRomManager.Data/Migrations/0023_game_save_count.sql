-- Saves: how many saves the game's ZIP holds (its saves/ entries), mirrored so the library and game page can show it
-- without opening ZIPs. Existing rows start at 0 and are corrected the next time their ZIP is written or re-synced.
ALTER TABLE games ADD COLUMN save_count INT NOT NULL DEFAULT 0;
