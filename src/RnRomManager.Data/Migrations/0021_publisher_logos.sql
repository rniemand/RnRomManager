-- Collections discovery B5: one shared logo override per publisher, keyed by the SHA-256 of the publisher's normalized
-- name (B2), so it survives the publisher leaving the index for a while. The bytes live here so backups cover them;
-- the 512 KiB cap is checked in code.
CREATE TABLE publisher_logos (
    key_digest BINARY(32)  NOT NULL,
    media_type VARCHAR(16) NOT NULL,
    bytes      MEDIUMBLOB  NOT NULL,
    updated_by INT         NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT pk_publisher_logos PRIMARY KEY (key_digest),
    CONSTRAINT fk_publisher_logos_updated_by FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
