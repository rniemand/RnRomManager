-- U2: rom-manager folds into admin. Rotate the stamps of everyone who had it first (the delete loses who that was),
-- so their cookies stop validating and they sign in again with the new rights; rom-manager-only users become Site users.
UPDATE users SET security_stamp = REPLACE(UUID(), '-', '')
WHERE id IN (SELECT user_id FROM user_roles WHERE role = 'rom-manager');

DELETE FROM user_roles WHERE role = 'rom-manager';
