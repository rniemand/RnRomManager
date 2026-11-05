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

/** `GET /api/systems` (spec §23). */
export interface SystemInfo {
	id: string;
	displayName: string;
	gameCount: number;
	romCount: number;
	regions: string[];
}

/** One item of `GET /api/games` (spec §23). */
export interface GameListItem {
	id: string;
	system: string;
	displayTitle: string;
	releaseYear: number | null;
	regions: string[];
	romCount: number;
	boxArtUrl: string | null;
}
