import type { JobStatus } from '#lib/enums/index.js';
import type { CurrentJob } from './CurrentJob.js';

/** `GET /api/jobs/{id}` and the items of `GET /api/jobs` (spec §23, §14.1 `jobs`). */
export interface Job {
	id: string;
	type: CurrentJob['type'];
	mode: 'Quick' | 'Deep' | 'Game' | null;
	triggerSource: 'Manual' | 'Schedule' | 'Startup';
	status: JobStatus;
	requestedBy: number | null;
	startedAt: string;
	finishedAt: string | null;
	/** Counts, e.g. `{ Ingested: 3, Review: 1 }`; a `ReviewResolve` job's is a {@link ReviewResolveSummary}. */
	summary: Record<string, unknown> | null;
	error: string | null;
}
