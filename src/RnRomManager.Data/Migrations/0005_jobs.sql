-- Spec §14.1 jobs, §3.1. Users are disabled, never deleted (§9.1), so requested_by needs no ON DELETE rule.
CREATE TABLE jobs (
    id             CHAR(36)    NOT NULL,
    type           VARCHAR(16) NOT NULL,
    mode           VARCHAR(16) NULL,
    trigger_source VARCHAR(16) NOT NULL,
    status         VARCHAR(16) NOT NULL,
    requested_by   INT         NULL,
    started_at     DATETIME(6) NOT NULL,
    finished_at    DATETIME(6) NULL,
    summary        JSON        NULL,
    error          TEXT        NULL,
    CONSTRAINT pk_jobs PRIMARY KEY (id),
    CONSTRAINT fk_jobs_requested_by FOREIGN KEY (requested_by) REFERENCES users (id),
    INDEX ix_jobs_type_started_at (type, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
