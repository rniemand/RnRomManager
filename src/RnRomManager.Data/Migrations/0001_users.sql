-- Spec §14.1 users, §9.1. Username uniqueness is case-insensitive via the table collation.
CREATE TABLE users (
    id                   INT          NOT NULL AUTO_INCREMENT,
    username             VARCHAR(64)  NOT NULL,
    password_hash        VARCHAR(255) NOT NULL,
    role                 VARCHAR(16)  NOT NULL,
    is_disabled          TINYINT(1)   NOT NULL DEFAULT 0,
    must_change_password TINYINT(1)   NOT NULL DEFAULT 0,
    failed_login_count   INT          NOT NULL,
    lockout_until        DATETIME(6)  NULL,
    security_stamp       CHAR(32)     NOT NULL,
    created_at           DATETIME(6)  NOT NULL,
    updated_at           DATETIME(6)  NOT NULL,
    last_login_at        DATETIME(6)  NULL,
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT ux_users_username UNIQUE (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
