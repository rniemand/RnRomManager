-- Disc systems B12: the other files of a release on a FileSets system (the cue and the non-data tracks), info.json v6
-- `files`, mirrored in the same transaction as `roms`. sha1 isn't unique: audio tracks repeat across discs. Existing
-- releases have no files, so nothing is backfilled.
CREATE TABLE rom_files (
    id                 BIGINT        NOT NULL AUTO_INCREMENT,
    rom_id             BIGINT        NOT NULL,
    game_id            CHAR(36)      NOT NULL,
    ordinal            INT           NOT NULL,
    file_name          VARCHAR(255)  NOT NULL,
    external_file_name VARCHAR(255)  NOT NULL,
    original_file_name VARCHAR(255)  NOT NULL,
    size_bytes         BIGINT        NOT NULL,
    crc32              CHAR(8)       NOT NULL,
    md5                CHAR(32)      NOT NULL,
    sha1               CHAR(40)      NOT NULL,
    CONSTRAINT pk_rom_files PRIMARY KEY (id),
    CONSTRAINT fk_rom_files_rom FOREIGN KEY (rom_id) REFERENCES roms (id) ON DELETE CASCADE,
    CONSTRAINT fk_rom_files_game FOREIGN KEY (game_id) REFERENCES games (id) ON DELETE CASCADE,
    INDEX ix_rom_files_sha1 (sha1),
    INDEX ix_rom_files_rom_id (rom_id),
    INDEX ix_rom_files_game_id (game_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
