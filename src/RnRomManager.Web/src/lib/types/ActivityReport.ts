import type { ActivityAuditItem } from './ActivityAuditItem.js';
import type { ActivityRom } from './ActivityRom.js';

/** `GET /api/reports/activity` (Admin, B21); each list is newest first, at most 20. */
export interface ActivityReport {
	lastSignIn: { username: string; at: string } | null;
	addedRoms: ActivityRom[];
	signIns: ActivityAuditItem[];
	plays: ActivityAuditItem[];
	saves: ActivityAuditItem[];
	edits: ActivityAuditItem[];
	deletions: ActivityAuditItem[];
}
