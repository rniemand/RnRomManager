-- Spec §14.1 job_items. game_id is a plain reference (no FK): items outlive deleted games.
CREATE TABLE job_items (
    id            BIGINT        NOT NULL AUTO_INCREMENT,
    job_id        CHAR(36)      NOT NULL,
    file_id       CHAR(36)      NOT NULL,
    system_id     VARCHAR(16)   NULL,
    source_path   VARCHAR(1024) NOT NULL,
    archive_entry VARCHAR(512)  NULL,
    outcome       VARCHAR(16)   NOT NULL,
    reason        VARCHAR(64)   NULL,
    details       TEXT          NULL,
    game_id       CHAR(36)      NULL,
    sha1          CHAR(40)      NULL,
    created_at    DATETIME(6)   NOT NULL,
    CONSTRAINT pk_job_items PRIMARY KEY (id),
    CONSTRAINT fk_job_items_job FOREIGN KEY (job_id) REFERENCES jobs (id) ON DELETE CASCADE,
    INDEX ix_job_items_job_outcome (job_id, outcome)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
