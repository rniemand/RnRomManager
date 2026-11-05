import { api } from './api/client';
import { isApiError } from './api/errors';
import type { GameDetail } from './api/types';

export interface RatingChange {
	/** 1–5; null clears it. Absent = unchanged. */
	rating?: number | null;
	favourite?: boolean;
}

/** The job lock is global and never waits, so a rating can collide with someone else's (spec B22). */
export const ratingRetryDelayMs = 1000;

/** `PATCH /api/games/{id}/rating`, retried once after 1 s on `409 JobRunning`; throws the last error. */
export async function saveRating(gameId: string, change: RatingChange): Promise<GameDetail> {
	const url = `/api/games/${encodeURIComponent(gameId)}/rating`;
	try {
		return await api.patch<GameDetail>(url, change);
	} catch (e) {
		if (!isApiError(e, 'JobRunning')) throw e;
		await new Promise((resolve) => setTimeout(resolve, ratingRetryDelayMs));
		return await api.patch<GameDetail>(url, change);
	}
}
