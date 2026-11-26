import { untrack } from 'svelte';
import { api } from '#lib/api/client.js';
import type { CurrentJob } from '#lib/types/index.js';

export const pollIntervalMs = 1500;

/** ROM Zip Config B17: the idle poll while watched and the tab is visible, so a job started elsewhere appears. */
export const idlePollIntervalMs = 15_000;

type JobEnded = (job: CurrentJob) => void;

/**
 * `GET /api/jobs/current` (spec §11 polling): every 1.5 s while a job runs, stopped when idle. Pages that show job
 * progress (Ingestion, Integrity, Admin → DATs) call {@link watch} on mount and the returned function on destroy;
 * {@link refresh} checks once (e.g. right after starting a job). Listeners hear when a running job ends. While watched
 * and idle it polls every 15 s, only while the tab is visible (a hidden tab's poll would keep renewing the sliding
 * session cookie), and checks once when the tab becomes visible again.
 */
class Jobs {
	current = $state<CurrentJob | null>(null);
	#watchers = 0;
	#timer: ReturnType<typeof setTimeout> | null = null;
	#ended = new Set<JobEnded>();

	/** Polls while anything watches and a job is running. */
	watch(): () => void {
		if (this.#watchers++ === 0)
			document.addEventListener('visibilitychange', this.#visibilityChanged);
		// Untracked: refresh reads `current`, which would make a calling $effect re-run (and re-watch) on every poll.
		untrack(() => void this.refresh());
		return () => {
			this.#watchers--;
			if (this.#watchers === 0) {
				document.removeEventListener('visibilitychange', this.#visibilityChanged);
				this.#stop();
			}
		};
	}

	onEnded(listener: JobEnded): () => void {
		this.#ended.add(listener);
		return () => this.#ended.delete(listener);
	}

	async refresh(): Promise<void> {
		this.#stop();
		const previous = this.current;
		let next: CurrentJob | null;
		try {
			next = (await api.get<CurrentJob | undefined>('/api/jobs/current')) ?? null;
		} catch {
			next = previous; // keep showing the last state; try again on the next tick
		}
		this.current = next;
		if (previous && (!next || next.id !== previous.id))
			for (const listener of this.#ended) listener(previous);
		this.#stop(); // an overlapping refresh may have scheduled one meanwhile; keep a single poll chain
		if (this.#watchers === 0) return;
		if (next) this.#timer = setTimeout(() => void this.refresh(), pollIntervalMs);
		else if (document.visibilityState === 'visible')
			this.#timer = setTimeout(() => void this.refresh(), idlePollIntervalMs);
	}

	#visibilityChanged = (): void => {
		if (document.visibilityState === 'visible') void this.refresh();
		else if (!this.current) this.#stop();
	};

	#stop(): void {
		if (this.#timer !== null) clearTimeout(this.#timer);
		this.#timer = null;
	}
}

export const jobs = new Jobs();
