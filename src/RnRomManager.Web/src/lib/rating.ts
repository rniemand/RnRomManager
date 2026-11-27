import { api } from './api/client';
import { isApiError } from './api/errors';
import type { GameDetail, RatingChange } from '#lib/types/index.js';

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

/** GS2, G1: an IGDB rating as a whole score, rounded half away from zero. */
export const igdbScore = (score: number): number => Math.sign(score) * Math.round(Math.abs(score));
