import { isApiError } from '../api/errors';
import type { ToastMessage, ToastLink } from '#lib/types/index.js';

export const toastDurationMs = 5000;

/** Short-lived messages shown by `ToastHost` (spec §11: `409 JobRunning` = toast). */
class Toasts {
	items = $state<ToastMessage[]>([]);
	#next = 1;
	#timers = new Map<number, ReturnType<typeof setTimeout>>();

	/** B1: every toast, links included, closes itself `toastDurationMs` after it appears. */
	show(text: string, kind: ToastMessage['kind'] = 'info', link?: ToastLink): number {
		const id = this.#next++;
		this.items.push({ id, text, kind, link });
		this.#schedule(id);
		return id;
	}

	dismiss(id: number): void {
		this.pause(id);
		this.items = this.items.filter((t) => t.id !== id);
	}

	/** B2: stops a toast's timer while it is hovered or holds focus. */
	pause(id: number): void {
		clearTimeout(this.#timers.get(id));
		this.#timers.delete(id);
	}

	/** B2: gives a still-shown toast a fresh `toastDurationMs`. */
	resume(id: number): void {
		if (this.items.some((t) => t.id === id)) this.#schedule(id);
	}

	#schedule(id: number): void {
		clearTimeout(this.#timers.get(id));
		this.#timers.set(
			id,
			setTimeout(() => this.dismiss(id), toastDurationMs)
		);
	}

	/** Shows the standard toast for `409 JobRunning`; returns whether `error` was one. */
	jobRunning(error: unknown): boolean {
		if (!isApiError(error, 'JobRunning')) return false;
		this.show('Another job is running. Try again when it has finished.', 'warning');
		return true;
	}
}

export const toasts = new Toasts();
