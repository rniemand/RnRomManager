-- Roles become a set (admin, rom-manager) instead of one users.role value. Admin gets both roles, User none.
-- Every stamp rotates so old cookies (carrying the old role claim) stop validating and everyone signs in again.
CREATE TABLE user_roles (
    user_id INT         NOT NULL,
    role    VARCHAR(32) NOT NULL,
    CONSTRAINT pk_user_roles PRIMARY KEY (user_id, role),
    CONSTRAINT fk_user_roles_user FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO user_roles (user_id, role)
SELECT id, 'admin' FROM users WHERE role = 'Admin'
UNION ALL
SELECT id, 'rom-manager' FROM users WHERE role = 'Admin';

UPDATE users SET security_stamp = REPLACE(UUID(), '-', '');

ALTER TABLE users DROP COLUMN role;
