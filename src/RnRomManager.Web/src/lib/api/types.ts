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
