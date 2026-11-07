-- B23: a user's hotkey overrides as a JSON object (action id → combination or null). NULL = all defaults, so
-- existing users keep the default bindings.
ALTER TABLE users ADD COLUMN hotkeys TEXT NULL;
