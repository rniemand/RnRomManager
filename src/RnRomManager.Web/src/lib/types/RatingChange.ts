export interface RatingChange {
	/** 1–5; null clears it. Absent = unchanged. */
	rating?: number | null;
	favourite?: boolean;
}
