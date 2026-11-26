import type { BackupFile } from './BackupFile.js';

/** `GET /api/admin/backups` and `PUT /api/admin/backups/settings` (B17). */
export interface BackupStatus {
	enabled: boolean;
	/** `HH:mm` in the server's time zone. */
	time: string;
	retention: number;
	/** The container's `TZ` id. */
	timeZone: string;
	nextRunAt: string | null;
	running: boolean;
	lastResult: { at: string; ok: boolean; fileName: string | null; error: string | null } | null;
	/** Newest first. */
	backups: BackupFile[];
}
