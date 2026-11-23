-- J4: the linked IGDB game id, mirrored from info.json v8 (null = not linked, 0 = Not on IGDB).
ALTER TABLE games ADD igdb_id INT NULL;
