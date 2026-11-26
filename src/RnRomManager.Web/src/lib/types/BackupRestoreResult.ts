/** B27: `POST /api/admin/backups/{fileName}/restore`. */
export interface BackupRestoreResult {
	restored: string;
	safetyBackup: string;
}
