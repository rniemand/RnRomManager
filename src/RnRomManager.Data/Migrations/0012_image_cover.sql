-- info.json v3 isCover, mirrored. Backfill like the v2→v3 upgrader: the BoxArt row (at most one per game before v3),
-- else the game's first image (MIN(id), the order rows were written in). ZIPs are rewritten as v3 on their next write.
ALTER TABLE images ADD is_cover TINYINT(1) NOT NULL DEFAULT 0;

UPDATE images SET is_cover = 1 WHERE type = 'BoxArt';

UPDATE images i
JOIN (SELECT MIN(id) AS id FROM images GROUP BY game_id HAVING SUM(is_cover) = 0) first_image ON first_image.id = i.id
SET i.is_cover = 1;
