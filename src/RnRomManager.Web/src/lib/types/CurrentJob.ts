import type { JobStep } from './JobStep.js';

/** `GET /api/jobs/current` (spec §23, §3.1); 204 when idle. */
export interface CurrentJob {
	id: string;
	type:
		| 'Ingest'
		| 'Integrity'
		| 'DatRefresh'
		| 'MediaRetry'
		| 'ReviewResolve'
		| 'StorageMigration'
		| 'ZipRepack'
		| 'SaveFolders'
		| 'PullCovers'
		| 'IgdbSync';
	mode: 'Quick' | 'Deep' | 'Game' | null;
	status: string;
	current: number;
	total: number;
	currentItem: string | null;
	startedAt: string;
	/** The current file's streamed phase; null (or absent from an older server) when none runs. */
	step?: JobStep | null;
}
