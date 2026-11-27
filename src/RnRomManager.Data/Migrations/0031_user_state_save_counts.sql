-- Resume SV3: the owner's save states per game, so Pick up where you left off shows only for a state save. NULL for rows
-- mirrored before this migration (callers fall back to save_count) until the game's next write.
ALTER TABLE game_user_save_counts ADD COLUMN state_count INT NULL;
