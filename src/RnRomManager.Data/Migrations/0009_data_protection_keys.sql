-- Spec §14.1 data_protection_keys, §9.2 (ASP.NET Core Data Protection key ring).
CREATE TABLE data_protection_keys (
    id            INT          NOT NULL AUTO_INCREMENT,
    friendly_name VARCHAR(255) NOT NULL,
    xml           TEXT         NOT NULL,
    created_at    DATETIME(6)  NOT NULL,
    CONSTRAINT pk_data_protection_keys PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
