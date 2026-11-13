import { api } from '../api/client';
import type { Page, RemovedSystem } from '../api/types';
import { auth } from './auth.svelte';

/**
 * D6: the navbar's admin counts: pending Review items and Deleted ROMs. The layout refreshes them on every
 * navigation and job end; the Review and Deleted ROMs pages call `refreshCounts()` after changing them in place.
 */
class NavCounts {
	/** Pending review items; null until loaded. */
	review = $state<number | null>(null);
	/** Files in every `_removed/` folder; null until loaded. */
	deleted = $state<number | null>(null);

	/** A failed call keeps that count's last value (the badges are hints). */
	async refresh(): Promise<void> {
		// Every /api call but me/change-password/logout is 403 until the password is changed (§9.1).
		if (!auth.isAdmin || auth.me?.mustChangePassword) return;
		await Promise.all([
			api
				.get<Page<unknown>>('/api/review?status=Pending&pageSize=1')
				.then((page) => (this.review = page.total))
				.catch(() => {}),
			api
				.get<RemovedSystem[]>('/api/ingest/removed')
				.then((systems) => (this.deleted = systems.reduce((sum, s) => sum + s.files.length, 0)))
				.catch(() => {})
		]);
	}

	reset(): void {
		this.review = null;
		this.deleted = null;
	}
}

export const navCounts = new NavCounts();

export const refreshCounts = (): Promise<void> => navCounts.refresh();
