-- B15: settings changed at runtime from the UI (backup schedule). A missing row means the default.
CREATE TABLE app_settings (
    name  VARCHAR(64)  NOT NULL,
    value VARCHAR(255) NOT NULL,
    CONSTRAINT pk_app_settings PRIMARY KEY (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
