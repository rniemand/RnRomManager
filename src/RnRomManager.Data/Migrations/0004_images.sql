-- Spec §14.1 images. The (game_id, file_name) unique key also serves the FK lookup.
CREATE TABLE images (
    id          BIGINT        NOT NULL AUTO_INCREMENT,
    game_id     CHAR(36)      NOT NULL,
    file_name   VARCHAR(255)  NOT NULL,
    type        VARCHAR(16)   NOT NULL,
    source      VARCHAR(32)   NOT NULL,
    source_url  VARCHAR(1024) NULL,
    is_default  TINYINT(1)    NOT NULL DEFAULT 0,
    size_bytes  BIGINT        NOT NULL,
    sha1        CHAR(40)      NOT NULL,
    width       INT           NOT NULL,
    height      INT           NOT NULL,
    date_added  DATETIME(6)   NOT NULL,
    CONSTRAINT pk_images PRIMARY KEY (id),
    CONSTRAINT ux_images_game_file UNIQUE (game_id, file_name),
    CONSTRAINT fk_images_game FOREIGN KEY (game_id) REFERENCES games (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
