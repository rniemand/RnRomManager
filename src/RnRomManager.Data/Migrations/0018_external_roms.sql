-- External ROM storage (E4): a ROM of a system that keeps its ROMs outside the game ZIP is a file next to the ZIP;
-- this is that file's name (info.json externalFileName), resolved in the ZIP's folder. NULL for a ROM embedded in
-- the ZIP, which is every existing row, so there is nothing to backfill.
ALTER TABLE roms ADD external_file_name VARCHAR(255) NULL;
