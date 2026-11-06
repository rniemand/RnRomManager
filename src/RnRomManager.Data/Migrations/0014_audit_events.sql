-- AU1: one row per file action, kept 30 days (AU5). No foreign keys: deleting a user or game keeps its history, and
-- username is a snapshot taken when the row is written. Paths are relative (roms/…, ingest/…), never absolute.
CREATE TABLE audit_events (
    id          BIGINT        NOT NULL AUTO_INCREMENT,
    occurred_at DATETIME(6)   NOT NULL,
    action      VARCHAR(32)   NOT NULL,
    system_id   VARCHAR(16)   NULL,
    game_id     CHAR(36)      NULL,
    user_id     INT           NULL,
    username    VARCHAR(64)   NULL,
    job_id      CHAR(36)      NULL,
    path        VARCHAR(1024) NULL,
    target_path VARCHAR(1024) NULL,
    details     VARCHAR(1024) NULL,
    CONSTRAINT pk_audit_events PRIMARY KEY (id),
    INDEX ix_audit_events_occurred_at (occurred_at),
    INDEX ix_audit_events_action_occurred_at (action, occurred_at),
    INDEX ix_audit_events_system_occurred_at (system_id, occurred_at),
    INDEX ix_audit_events_user_occurred_at (user_id, occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
