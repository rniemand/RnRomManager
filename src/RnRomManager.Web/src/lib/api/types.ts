import type { Bindings, EditableHotkeyId } from '../hotkeys';

/** A user role: an Admin has `admin`, a Site user none. */
export type Role = 'admin';

/** `GET /api/auth/me` (spec §23). */
export interface Me {
	id: number;
	username: string;
	roles: Role[];
	mustChangePassword: boolean;
}

/** `GET`/`PUT /api/auth/me/hotkeys` (B23). `implicitUnassigned`: actions whose default yields to an older override. */
export interface HotkeysResponse {
	bindings: Bindings;
	implicitUnassigned: EditableHotkeyId[];
}

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
		| 'ZipRepack';
	mode: 'Quick' | 'Deep' | 'Game' | null;
	status: string;
	current: number;
	total: number;
	currentItem: string | null;
	startedAt: string;
	/** The current file's streamed phase; null (or absent from an older server) when none runs. */
	step?: JobStep | null;
}

/** Large ROM import progress B1: one streamed phase of the current file. */
export interface JobStep {
	/** `Hashing`, `CopyingToLibrary` or `Copying`; another value shows as is. */
	phase: string;
	bytesDone: number;
	bytesTotal: number;
	bytesPerSecond: number | null;
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
	/** The handler's short name (`GB`), for system badges (L3). */
	shortName: string;
	gameCount: number;
	romCount: number;
	regions: string[];
	/** B15: the shared logo's `/api/discovery/systems/logo?key=<id>&v=…`, or null without one. */
	logoUrl: string | null;
}

/** One file entry of a library ZIP (Z2); sizes from the central directory. */
export interface ZipEntry {
	path: string;
	sizeBytes: number;
	compressedSizeBytes: number;
	modifiedUtc: string;
}

/** `GET /api/games/{id}/zip-entries` (Z2): the ZIP's file name and its file entries, ordered by path. */
export interface ZipEntries {
	zipName: string;
	entries: ZipEntry[];
}

/** `GET /api/games/facets` (F1): per option, the games matching every other active filter. */
export interface GameFacets {
	systems: { id: string; count: number }[];
	regions: { region: string; count: number }[];
	letters: { letter: string; count: number }[];
}

/** One item of `GET /api/games` (spec §23). */
export interface GameListItem {
	id: string;
	system: string;
	displayTitle: string;
	releaseYear: number | null;
	regions: string[];
	romCount: number;
	/** ROM size of the primary release (LP1); null when the game has none. */
	primarySizeBytes: number | null;
	boxArtUrl: string | null;
	/** The handler's display name, or the id when it isn't registered. */
	systemName: string;
	/** The handler's short name (`SNES`) for game pickers, or the id when it isn't registered. */
	systemShortName: string;
	/** 1–5; null = not rated. Shared per game. */
	rating: number | null;
	favourite: boolean;
	/** With `q`: the metadata value that matched when no word was in a title (S3); null otherwise. */
	matchedOn: {
		field: 'developer' | 'publisher' | 'genre' | 'dlc' | 'update';
		value: string;
	} | null;
	/** Roms flagged `Update` (C1). */
	updateCount: number;
	/** DLC B7: releases flagged `DLC` and not `Update`. */
	dlcCount: number;
	/** Saves in the game's ZIP. */
	saveCount: number;
	/** Primary release to quick play (C2); null when the system has no browser core or the release is external. */
	playRomId: number | null;
	playRomFileName: string | null;
	/** Sega CD B6: the quick-play release is external, so its play URL carries `disc=1`. */
	playDisc: boolean;
	/** The system handler's browser core, null when it has none. */
	browserCore: string | null;
}

/** One release of `GET /api/games/{id}` (spec §23, §6.2 Rom fields flattened). */
export type SaveKind = 'state' | 'battery';

/** B7: one save of a game; `romId` is null when no release has its `releaseFileName` (B4). */
export interface SaveItem {
	id: string;
	name: string;
	description: string;
	kind: SaveKind;
	releaseFileName: string;
	romId: number | null;
	sizeBytes: number;
	extension: string;
	uploadedBy: string | null;
	uploadedAt: string;
	updatedBy: string | null;
	updatedAt: string;
}

export interface RomDetail {
	id: number;
	fileName: string;
	originalFileName: string;
	baseTitle: string;
	regions: string[];
	languages: string[];
	revision: string | null;
	/** An update's dotted version (`1.0.1`); null otherwise. */
	version: string | null;
	flags: string[];
	isPrimary: boolean;
	identificationMethod: 'Dat' | 'Manual' | 'TitleDb' | 'Serial';
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
	/** B3: the ZIP holds `notes/<fileName>.md`. */
	hasNote: boolean;
	/** G7: the file next to the ZIP for an external system's release; null when the ROM is in the ZIP. */
	externalFileName: string | null;
	/** B20: a disc set's other files (cue, other tracks) in order; null for a release without. */
	files: ReleaseFile[] | null;
	/** B20: `sizeBytes` plus every `files` entry. */
	totalSizeBytes: number;
}

/** B20: one file of a disc set, besides the release's main file. */
export interface ReleaseFile {
	id: number;
	fileName: string;
	sizeBytes: number;
	crc32: string;
	md5: string;
	sha1: string;
	downloadUrl: string;
}

export interface ImageInfo {
	/** Changes whenever the game's ZIP is rewritten: use the ids from the latest detail. */
	id: number;
	type: 'BoxArt' | 'Snap' | 'TitleScreen' | 'Other';
	fileName: string;
	source: 'libretro-thumbnails' | 'default' | 'upload';
	/** The system placeholder box art. */
	isDefault: boolean;
	/** The image shown for the game (exactly one when it has images). */
	isCover: boolean;
	sizeBytes: number;
	width: number;
	height: number;
	url: string;
}

/** `GET /api/games/{id}` (spec §23). */
export interface GameDetail {
	id: string;
	system: string;
	/** The handler's display name (GP2). */
	systemName: string;
	/** Browser emulators B2: the EmulatorJS core that plays this system, or null. */
	browserCore: string | null;
	title: string;
	displayTitle: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
	hasDefaultBoxArt: boolean;
	rating: number | null;
	favourite: boolean;
	/** Saves in the game's ZIP. */
	saveCount: number;
	metadata: { createdAt: string; updatedAt: string };
	roms: RomDetail[];
	images: ImageInfo[];
}

/** `GET /api/ingest/pending` (spec §23). */
export interface PendingSystem {
	system: string;
	files: { name: string; sizeBytes: number; modifiedAt: string }[];
}

/** `GET /api/ingest/removed` (Deleted ROMs D2): only systems with files in their `_removed/` folder. */
export type RemovedSystem = PendingSystem;

/** `POST /api/ingest/restore` (Deleted ROMs D5); `jobId` is null when no ingest job started. */
export interface RemovedRestoreResponse {
	restored: number;
	failed: { system: string; name: string }[];
	jobId: string | null;
}

/** `GET /api/ingest/failed` (D1): the files in each system's `_failed/` folder; `reason` from its `.error.txt`. */
export interface FailedSystem {
	system: string;
	files: { name: string; sizeBytes: number; modifiedAt: string; reason: string | null }[];
}

/** One entry of `POST /api/ingest/delete` (D2). */
export interface IngestDeleteEntry {
	system: string;
	folder: 'pending' | 'failed' | 'removed';
	name: string;
}

/** `POST /api/ingest/delete` response (D2). */
export interface IngestDeleteResponse {
	deleted: number;
	failed: IngestDeleteEntry[];
}

export type JobStatus = 'Running' | 'Completed' | 'Cancelled' | 'Failed' | 'Aborted';

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

/** A `ReviewResolve` job's summary (B5). */
export interface ReviewResolveSummary {
	reviewId: number;
	fileName: string;
	gameId: string | null;
	code: string | null;
	errors: Record<string, string[]> | null;
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

export type ReviewReason = 'NoDatMatch' | 'AmbiguousGroup' | 'AmbiguousSet';

/** One item of `GET /api/review` (spec §23). */
export interface ReviewListItem {
	id: number;
	system: string;
	reason: ReviewReason;
	fileName: string;
	sizeBytes: number;
	headerTitle: string | null;
	createdAt: string;
	/** The file came from an archive; deleting it deletes the whole archive (RV1). */
	isArchive: boolean;
}

/** `GET /api/review/{id}` (spec §23, §14.1 `ingest_review_items`). */
export interface ReviewDetail {
	id: number;
	system: string;
	/** The handler's display name (RV2). */
	systemName: string;
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
		/** T4: a titledb AmbiguousGroup item also queues the whole identity. */
		titleId?: string;
		baseTitle?: string;
		regions?: string[];
		languages?: string[];
		revision?: string | null;
		flags?: string[];
	} | null;
	candidateGames: { id: string; displayTitle: string }[];
	status: 'Pending' | 'Resolved' | 'Dismissed';
	/** B12: false for a `.cue`/`.bin` on a disc system whose bin/cue set isn't found; it can only be deleted. */
	resolvable: boolean;
	/** B16: a disc system (bin/cue sets, disc numbers). */
	fileSets: boolean;
	/** B13: the unmatched set this item belongs to, cue first; null when there is none. */
	setMembers: { reviewId: number; fileName: string; sizeBytes: number }[] | null;
	/** B15: false for every `.cue`/`.bin` on a disc system; Dismiss is hidden then. */
	dismissible: boolean;
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

/** One event of `GET /api/admin/logs/stream` (L2); `time` is UTC ISO 8601. */
export interface LogEntry {
	id: number;
	time: string;
	level: 'Trace' | 'Debug' | 'Info' | 'Warn' | 'Error' | 'Fatal';
	logger: string;
	message: string;
	exception: string | null;
}

/** `GET /api/users` (spec §23). */
export interface UserInfo {
	id: number;
	username: string;
	roles: Role[];
	isDisabled: boolean;
	lastLoginAt: string | null;
}

/** `GET /api/about` (spec §23). */
export interface About {
	version: string;
	licenses: { name: string; license: string; url: string }[];
}

/** A backup ZIP in `Paths:Backups` (B17). */
export interface BackupFile {
	fileName: string;
	sizeBytes: number;
	createdAt: string;
}

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

/** B27: `POST /api/admin/backups/{fileName}/restore`. */
export interface BackupRestoreResult {
	restored: string;
	safetyBackup: string;
}

/** B28: `GET/PUT /api/admin/dats/settings`. */
export interface DatRefreshSettings {
	enabled: boolean;
	/** `HH:mm` in the server's time zone. */
	time: string;
	/** The container's `TZ` id. */
	timeZone: string;
	nextRunAt: string | null;
}

/** `GET /api/discovery/{kind}` item (discovery B3): one publisher or genre across systems. */
export interface DiscoveryGroup {
	/** Lowercase hex SHA-256 of the normalized name. */
	key: string;
	name: string;
	gameCount: number;
	/** The shared logo's `…/logo?key=…&v=…`, or null without one (B6, B15). */
	logoUrl?: string | null;
}

/** `GET /api/collections` item (B6). */
export interface CollectionListItem {
	id: string;
	name: string;
	gameCount: number;
	/** `/api/collections/{id}/cover?v=…`, or null without a cover. */
	coverUrl: string | null;
	updatedAt: string;
}

/** `GET /api/collections/{id}` and every collection edit's response (B7). */
export interface CollectionDetail extends CollectionListItem {
	notes: string | null;
	createdAt: string;
}

/** `GET /api/games/{id}/collections` item (B16). */
export interface CollectionRef {
	id: string;
	name: string;
}

/** Bundle Creator B3: a set file inside a release, by its SHA-1 and stored `rom_files.file_name`. */
export interface BundleMember {
	sha1: string;
	fileName: string;
}

/** Bundle Creator B3/B4: one cart or queue entry, a stable identity plus its last-known labels. */
export interface BundleEntry {
	gameId: string;
	releaseSha1: string;
	member: BundleMember | null;
	gameTitle: string;
	fileName: string;
}

/** `GET /api/bundle-queues` (B4). */
export interface BundleQueueSummary {
	id: string;
	name: string;
	revision: number;
	itemCount: number;
}

/** `GET /api/bundle-queues/{id}` and the create/replace answers (B4). */
export interface BundleQueue {
	id: string;
	name: string;
	revision: number;
	entries: BundleEntry[];
}

/** One `POST /api/bundle-files/resolve` answer item (B5); the strings are null when unavailable. */
export interface BundleResolvedItem {
	available: boolean;
	gameTitle: string | null;
	fileName: string | null;
	downloadUrl: string | null;
}

/** `POST /api/bundle-downloads` (B7). */
export interface BundleDownloadTicket {
	ticket: string;
	fileUrl: string;
}

export type BundleTicketState = 'pending' | 'sending' | 'sent' | 'failed' | 'expired';

/** `GET /api/bundle-downloads/{ticket}` (B7). */
export interface BundleTicketStatus {
	state: BundleTicketState;
	errorCode: string | null;
}

/** UI polish B21: one audit row in the Activity report (the `/api/admin/audit` item plus `gameTitle`). */
export interface ActivityAuditItem {
	id: number;
	occurredAt: string;
	action: string;
	system: string | null;
	gameId: string | null;
	user: string | null;
	jobId: string | null;
	path: string | null;
	targetPath: string | null;
	details: string | null;
	/** Null when the game is gone. */
	gameTitle: string | null;
}

/** B21: a recently added release; `sizeBytes` is the release total. */
export interface ActivityRom {
	romId: number;
	gameId: string;
	gameTitle: string;
	fileName: string;
	system: string;
	sizeBytes: number;
	addedAt: string;
}

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

/** Missing Metadata M2: one row of `GET /api/reports/missing-metadata`. */
export interface MissingMetadataItem {
	id: string;
	displayTitle: string;
	system: string;
	systemName: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
}

/** ROM Zip Config B12: `GET /api/admin/rom-storage`, one row per system. */
export interface RomStorageRow {
	id: string;
	displayName: string;
	/** true: ROMs inside the game ZIP when nothing is configured. */
	defaultEmbedRoms: boolean;
	/** The effective storage. */
	embedRoms: boolean;
	configurable: boolean;
	/** Games still waiting to be moved to the effective storage. */
	pendingGames: number;
	/** ZIP Compression Z6: the system's level (Med when none is stored). */
	zipCompression: 'Store' | 'Med' | 'High';
	/** Z5: true while the system's ROMs are stored in the ZIP. */
	compressionConfigurable: boolean;
}
