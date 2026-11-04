-- Test-only migration: proves DbUp applies and journals embedded scripts.
CREATE TABLE migration_test (
    id INT NOT NULL PRIMARY KEY
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO migration_test (id) VALUES (1);
