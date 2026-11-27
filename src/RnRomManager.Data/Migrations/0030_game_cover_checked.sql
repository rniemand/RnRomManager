-- Admin actions AC2: mirrors info.json actions.coverImageChecked, the last time Pull Cover Images tried a game and IGDB
-- had no usable cover. Default pulls skip games with either time; existing rows start NULL.
ALTER TABLE game_actions ADD COLUMN cover_image_checked_at DATETIME(6) NULL;
