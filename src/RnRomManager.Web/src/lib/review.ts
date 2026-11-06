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
