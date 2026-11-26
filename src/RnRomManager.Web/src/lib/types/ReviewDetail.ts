import type { ReviewReason } from '#lib/enums/index.js';

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
	/** B12: false for a set member (`.cue`/`.bin`/`.ccd`/`.img`/`.sub`/`.toc`/`.mds`/`.mdf`) on a disc system that isn't dismissible and whose set isn't found; it can only be deleted. */
	resolvable: boolean;
	/** B16: a disc system (bin/cue sets, disc numbers). */
	fileSets: boolean;
	/** A titledb (Switch) system: Attach offers DLC. */
	addOns: boolean;
	/** MS-DOS N5: false for a system with no DAT (`dos`): no Suggestions tab, and Manual starts with a title from the file name. */
	hasDat: boolean;
	/** B13: the unmatched set this item belongs to, cue/`.toc`/`.ccd`/`.mds` first; null when there is none. */
	setMembers: { reviewId: number; fileName: string; sizeBytes: number }[] | null;
	/** B15: false for every set member (`.cue`/`.bin`/`.ccd`/`.img`/`.sub`/`.toc`/`.mds`/`.mdf`) on a disc system but a lone PlayStation `.img`; Dismiss is hidden then. */
	dismissible: boolean;
	gameId: string | null;
	createdAt: string;
}
