import type { Job } from './Job.js';

/** `GET /api/integrity/status` (spec §23). */
export interface IntegrityStatus {
	lastQuick: Job | null;
	lastDeep: Job | null;
	/** UTC; null when the schedule is disabled. */
	nextScheduledAt: string | null;
}
