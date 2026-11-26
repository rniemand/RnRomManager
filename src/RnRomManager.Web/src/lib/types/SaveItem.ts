import type { SaveKind } from '#lib/enums/index.js';

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
