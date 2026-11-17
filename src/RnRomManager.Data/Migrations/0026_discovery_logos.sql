-- B13: publisher_logos becomes discovery_logos, one logo per (kind, key) for publishers, genres and systems.
-- Existing rows are publisher logos. A rename keeps constraint and index names, so the FK and its index are renamed too.
RENAME TABLE publisher_logos TO discovery_logos;

ALTER TABLE discovery_logos DROP FOREIGN KEY fk_publisher_logos_updated_by;

ALTER TABLE discovery_logos
    ADD COLUMN kind VARCHAR(16) NOT NULL DEFAULT 'publishers' FIRST,
    DROP PRIMARY KEY,
    ADD CONSTRAINT pk_discovery_logos PRIMARY KEY (kind, key_digest),
    RENAME INDEX fk_publisher_logos_updated_by TO fk_discovery_logos_updated_by;

ALTER TABLE discovery_logos
    ADD CONSTRAINT fk_discovery_logos_updated_by FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE SET NULL;
