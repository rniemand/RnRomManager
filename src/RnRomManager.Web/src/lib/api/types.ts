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

/** One release of `GET /api/games/{id}` (spec §23, §6.2 Rom fields flattened). */
export interface RomDetail {
	id: number;
	fileName: string;
	originalFileName: string;
	baseTitle: string;
	regions: string[];
	languages: string[];
	revision: string | null;
	flags: string[];
	isPrimary: boolean;
	identificationMethod: 'Dat' | 'Manual';
	datName: string | null;
	datVersion: string | null;
	datGameName: string | null;
	/** Handler-specific (§8.2); null when unreadable. */
	header: Record<string, unknown> | null;
	sizeBytes: number;
	crc32: string;
	md5: string;
	sha1: string;
	dateAdded: string;
	dateModified: string;
	downloadUrl: string;
}

export interface ImageInfo {
	id: number;
	type: 'BoxArt' | 'Snap' | 'TitleScreen' | 'Other';
	isDefault: boolean;
	width: number;
	height: number;
	url: string;
}

/** `GET /api/games/{id}` (spec §23). */
export interface GameDetail {
	id: string;
	system: string;
	title: string;
	displayTitle: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
	hasDefaultBoxArt: boolean;
	metadata: { createdAt: string; updatedAt: string };
	roms: RomDetail[];
	images: ImageInfo[];
}

/** `GET /api/ingest/pending` (spec §23). */
export interface PendingSystem {
	system: string;
	files: { name: string; sizeBytes: number; modifiedAt: string }[];
}

export type JobStatus = 'Running' | 'Completed' | 'Cancelled' | 'Failed' | 'Aborted';

/** `GET /api/jobs/{id}` and the items of `GET /api/jobs` (spec §23, §14.1 `jobs`). */
export interface Job {
	id: string;
	type: CurrentJob['type'];
	mode: 'Quick' | 'Deep' | null;
	triggerSource: 'Manual' | 'Schedule';
	status: JobStatus;
	requestedBy: number | null;
	startedAt: string;
	finishedAt: string | null;
	/** Counts, e.g. `{ Ingested: 3, Review: 1 }`. */
	summary: Record<string, number> | null;
	error: string | null;
}

/** `GET /api/jobs/{id}/items` (spec §14.1 `job_items`). */
export interface JobItem {
	id: number;
	fileId: string;
	systemId: string | null;
	sourcePath: string;
	archiveEntry: string | null;
	outcome: string;
	reason: string | null;
	details: string | null;
	gameId: string | null;
	sha1: string | null;
	createdAt: string;
}

/** `GET /api/admin/dats` (spec §23). */
export interface DatVersion {
	datName: string;
	version: string;
	downloadedAt: string;
}

export type ReviewReason = 'NoDatMatch' | 'AmbiguousGroup';

/** One item of `GET /api/review` (spec §23). */
export interface ReviewListItem {
	id: number;
	system: string;
	reason: ReviewReason;
	fileName: string;
	sizeBytes: number;
	headerTitle: string | null;
	createdAt: string;
}

/** `GET /api/review/{id}` (spec §23, §14.1 `ingest_review_items`). */
export interface ReviewDetail {
	id: number;
	system: string;
	reason: ReviewReason;
	sourcePath: string;
	archivePath: string | null;
	archiveEntry: string | null;
	originalFileName: string;
	sizeBytes: number;
	crc32: string;
	md5: string;
	sha1: string;
	header: Record<string, unknown> | null;
	suggestions: { datGameName: string; romName: string; score: number }[] | null;
	/** `AmbiguousGroup`: the DAT entry the file matched. */
	datIdentification: {
		datName: string;
		datVersion: string;
		datGameName: string;
		romName: string;
	} | null;
	candidateGames: { id: string; displayTitle: string }[];
	status: 'Pending' | 'Resolved' | 'Dismissed';
	gameId: string | null;
	createdAt: string;
}

/** `GET /api/dat/search` (spec §23). */
export interface DatSearchResult {
	gameName: string;
	romName: string;
	sizeBytes: number;
	crc32: string;
}

/** `GET /api/integrity/status` (spec §23). */
export interface IntegrityStatus {
	lastQuick: Job | null;
	lastDeep: Job | null;
	/** UTC; null when the schedule is disabled. */
	nextScheduledAt: string | null;
}

/** One item of `GET /api/integrity/issues` (spec §14.1 `integrity_issues`). */
export interface IntegrityIssue {
	id: number;
	type: string;
	severity: 'Error' | 'Warn' | 'Info';
	system: string | null;
	zipPath: string | null;
	gameId: string | null;
	entryName: string | null;
	details: string | null;
	status: 'Open' | 'AutoRepaired' | 'Resolved';
	firstSeenJobId: string;
	lastSeenJobId: string;
	detectedAt: string;
	resolvedAt: string | null;
}
