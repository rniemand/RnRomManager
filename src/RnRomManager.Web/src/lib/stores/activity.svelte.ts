import { untrack } from 'svelte';
import { api } from '../api/client';
import type { Activity } from '#lib/types/index.js';
import { idlePollIntervalMs, pollIntervalMs } from './jobs.svelte';

/**
 * L3: `GET /api/activity`, what holds the job lock (a job, or e.g. "Saving changes"), for every signed-in user's footer.
 * Polls every 1.5 s while busy and 15 s while idle, only while something watches and the tab is visible (as
 * {@link import('./jobs.svelte').jobs} does); {@link refresh} checks at once. A failed poll (a 503 included) keeps
 * `current` and polls on at the idle interval.
 */
class ActivityStore {
	current = $state<Activity | null>(null);
	#watchers = 0;
	#timer: ReturnType<typeof setTimeout> | null = null;

	watch(): () => void {
		if (this.#watchers++ === 0)
			document.addEventListener('visibilitychange', this.#visibilityChanged);
		untrack(() => void this.refresh());
		return () => {
			this.#watchers--;
			if (this.#watchers === 0) {
				document.removeEventListener('visibilitychange', this.#visibilityChanged);
				this.#stop();
			}
		};
	}

	async refresh(): Promise<void> {
		this.#stop();
		let interval = idlePollIntervalMs;
		try {
			this.current = (await api.get<Activity | undefined>('/api/activity')) ?? null;
			if (this.current) interval = pollIntervalMs;
		} catch {
			// keep the last state; try again at the idle interval
		}
		this.#stop(); // an overlapping refresh may have scheduled one meanwhile; keep a single poll chain
		if (this.#watchers > 0 && document.visibilityState === 'visible')
			this.#timer = setTimeout(() => void this.refresh(), interval);
	}

	#visibilityChanged = (): void => {
		if (document.visibilityState === 'visible') void this.refresh();
		else this.#stop();
	};

	#stop(): void {
		if (this.#timer !== null) clearTimeout(this.#timer);
		this.#timer = null;
	}
}

export const activity = new ActivityStore();
