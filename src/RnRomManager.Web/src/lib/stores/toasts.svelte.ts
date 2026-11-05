import { isApiError } from '../api/errors';

export interface ToastMessage {
	id: number;
	text: string;
	kind: 'info' | 'warning' | 'danger' | 'success';
}

export const toastDurationMs = 5000;

/** Short-lived messages shown by `ToastHost` (spec §11: `409 JobRunning` = toast). */
class Toasts {
	items = $state<ToastMessage[]>([]);
	#next = 1;

	show(text: string, kind: ToastMessage['kind'] = 'info'): number {
		const id = this.#next++;
		this.items.push({ id, text, kind });
		setTimeout(() => this.dismiss(id), toastDurationMs);
		return id;
	}

	dismiss(id: number): void {
		this.items = this.items.filter((t) => t.id !== id);
	}

	/** Shows the standard toast for `409 JobRunning`; returns whether `error` was one. */
	jobRunning(error: unknown): boolean {
		if (!isApiError(error, 'JobRunning')) return false;
		this.show('Another job is running. Try again when it has finished.', 'warning');
		return true;
	}
}

export const toasts = new Toasts();
