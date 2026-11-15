-- Bundle Creator B2–B4: a user's named, ordered snapshots of ROM file identities. Names are unique per user,
-- case-insensitive but accent-sensitive like collections.name. entries is the validated JSON array (relaxed escaping);
-- item_count mirrors its length so the list never parses it. revision starts at 1 and grows on each replace.
CREATE TABLE bundle_queues (
    id         CHAR(36)     NOT NULL,
    user_id    INT          NOT NULL,
    name       VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci NOT NULL,
    revision   INT          NOT NULL,
    item_count INT          NOT NULL,
    entries    MEDIUMTEXT   NOT NULL,
    created_at DATETIME(6)  NOT NULL,
    updated_at DATETIME(6)  NOT NULL,
    CONSTRAINT pk_bundle_queues PRIMARY KEY (id),
    CONSTRAINT ux_bundle_queues_user_name UNIQUE (user_id, name),
    CONSTRAINT fk_bundle_queues_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
