import { api } from './api/client';
import { isApiError } from './api/errors';
import { toasts } from './stores/toasts.svelte';

/** RV1: deletes a review item's source file (the modal and both Shift+click shortcuts). */
export function deleteReviewItem(id: number): Promise<unknown> {
	return api.delete(`/api/review/${id}`);
}

/** RV1: a failed Shift+click delete toasts the error and changes nothing else. */
export function toastDeleteFailed(e: unknown): void {
	if (!toasts.jobRunning(e))
		toasts.show(isApiError(e) ? e.title : "The file couldn't be deleted.", 'danger');
}

/** Q4: `POST /api/review/delete` takes at most this many ids. */
export const reviewDeleteBatchSize = 500;

interface ReviewBatchDeleteResponse {
	deleted: number;
	skipped: number;
	failed: { id: number; code: string }[];
}

/** `1 file`, `2 files`. */
const files = (n: number) => `${n} ${n === 1 ? 'file' : 'files'}`;

/**
 * Q4: deletes the items in batches of {@link reviewDeleteBatchSize}, stopping at the first error, and toasts the
 * outcome (as `deleteIngestFiles`). Items already gone (e.g. with their archive) are skipped quietly. The caller
 * reloads afterwards.
 */
export async function deleteReviewItems(ids: number[]): Promise<void> {
	let deleted = 0;
	let failed = 0;
	let error: unknown = null;
	for (let i = 0; i < ids.length; i += reviewDeleteBatchSize) {
		try {
			const result = await api.post<ReviewBatchDeleteResponse>('/api/review/delete', {
				ids: ids.slice(i, i + reviewDeleteBatchSize)
			});
			deleted += result.deleted;
			failed += result.failed.length;
		} catch (e) {
			error = e;
			break;
		}
	}
	if (deleted > 0) toasts.show(`Deleted ${files(deleted)}.`, 'success');
	if (failed > 0) toasts.show(`${failed} couldn't be deleted.`, 'danger');
	if (error) toastDeleteFailed(error);
}
