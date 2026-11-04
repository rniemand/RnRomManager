-- Spec §14.1 integrity_issues, §12.
CREATE TABLE integrity_issues (
    id                BIGINT       NOT NULL AUTO_INCREMENT,
    type              VARCHAR(32)  NOT NULL,
    severity          VARCHAR(8)   NOT NULL,
    system_id         VARCHAR(16)  NULL,
    zip_path          VARCHAR(512) NULL,
    game_id           CHAR(36)     NULL,
    entry_name        VARCHAR(512) NULL,
    details           TEXT         NULL,
    status            VARCHAR(16)  NOT NULL,
    first_seen_job_id CHAR(36)     NOT NULL,
    last_seen_job_id  CHAR(36)     NOT NULL,
    detected_at       DATETIME(6)  NOT NULL,
    resolved_at       DATETIME(6)  NULL,
    CONSTRAINT pk_integrity_issues PRIMARY KEY (id),
    INDEX ix_integrity_issues_status_type (status, type),
    INDEX ix_integrity_issues_zip_path (zip_path)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
