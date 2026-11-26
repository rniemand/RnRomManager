/** A `ReviewResolve` job's summary (B5). */
export interface ReviewResolveSummary {
	reviewId: number;
	fileName: string;
	gameId: string | null;
	code: string | null;
	errors: Record<string, string[]> | null;
}
