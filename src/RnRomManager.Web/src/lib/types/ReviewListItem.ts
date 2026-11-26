import type { ReviewReason } from '#lib/enums/index.js';

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
