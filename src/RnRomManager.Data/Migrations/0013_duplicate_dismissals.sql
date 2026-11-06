-- RP7: a dismissed duplicate group, keyed by its exact membership (games_key = SHA-256 of the sorted game ids), so a
-- game joining or leaving the group shows it again. No FK on dismissed_by: deleting a user keeps the history.
CREATE TABLE duplicate_dismissals (
    system_id    VARCHAR(16)  NOT NULL,
    base_title   VARCHAR(255) COLLATE utf8mb4_unicode_ci NOT NULL,
    games_key    CHAR(64)     NOT NULL,
    dismissed_by INT          NULL,
    dismissed_at DATETIME(6)  NOT NULL,
    CONSTRAINT pk_duplicate_dismissals PRIMARY KEY (system_id, base_title, games_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
