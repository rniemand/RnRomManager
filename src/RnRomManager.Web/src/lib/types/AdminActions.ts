/** T2, AC8: `GET /api/admin/actions`, the Admin Actions tab's counts. */
export interface AdminActions {
	igdbConfigured: boolean;
	igdbLinked: number;
	/** Linked games a default cover pull processes (never pulled nor checked). */
	coversPending: number;
	/** Linked games without a pulled cover (Try Missing Images). */
	coversMissing: number;
	/** Linked games without a stored IGDB rating. */
	ratingsMissing: number;
	lastIgdbSyncAt: string | null;
}
