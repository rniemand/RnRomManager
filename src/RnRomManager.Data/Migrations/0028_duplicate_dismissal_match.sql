-- Duplicates D2: dismissals are kept per match type. Existing rows are title dismissals; an IGDB dismissal stores the
-- IGDB id in decimal as its base_title.
ALTER TABLE duplicate_dismissals
    ADD COLUMN match_kind VARCHAR(8) NOT NULL DEFAULT 'title' FIRST,
    DROP PRIMARY KEY,
    ADD CONSTRAINT pk_duplicate_dismissals PRIMARY KEY (match_kind, system_id, base_title, games_key);
