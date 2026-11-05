/** `GET /api/auth/me` (spec §23). */
export interface Me {
	id: number;
	username: string;
	role: 'Admin' | 'User';
	mustChangePassword: boolean;
}

/** `GET /api/jobs/current` (spec §23, §3.1); 204 when idle. */
export interface CurrentJob {
	id: string;
	type: 'Ingest' | 'Integrity' | 'DatRefresh' | 'MediaRetry';
	mode: 'Quick' | 'Deep' | null;
	status: string;
	current: number;
	total: number;
	currentItem: string | null;
	startedAt: string;
}

/** `{ items, page, pageSize, total }` (spec §23). */
export interface Page<T> {
	items: T[];
	page: number;
	pageSize: number;
	total: number;
}
