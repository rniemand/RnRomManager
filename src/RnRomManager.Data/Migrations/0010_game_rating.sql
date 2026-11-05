-- Shared per-game rating (1-5, null = not rated) and favourite flag, mirrored from info.json v2.
ALTER TABLE games
    ADD rating    TINYINT    NULL,
    ADD favourite TINYINT(1) NOT NULL DEFAULT 0;
