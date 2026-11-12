-- Release versions (game updates B5): an update's dotted version, info.json v5 `version`. Existing updates are
-- backfilled from their original file name with the ReleaseVersion.From rule (first [x.y(.z(.w))] tag), since DB sync
-- skips unchanged ZIPs and would never mirror the upgraded value. REGEXP_SUBSTR gives '' on no match, hence NULLIF;
-- string literals need doubled backslashes.
ALTER TABLE roms ADD version VARCHAR(40) NULL;

UPDATE roms
SET version = NULLIF(SUBSTRING(REGEXP_SUBSTR(original_file_name, '\\[[0-9]{1,9}(\\.[0-9]{1,9}){1,3}\\]'), 2,
              CHAR_LENGTH(REGEXP_SUBSTR(original_file_name, '\\[[0-9]{1,9}(\\.[0-9]{1,9}){1,3}\\]')) - 2), '')
WHERE JSON_CONTAINS(flags, '"Update"');
