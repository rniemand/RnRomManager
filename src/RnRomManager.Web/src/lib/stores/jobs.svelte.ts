import { api } from '../api/client';
import type { CurrentJob } from '../api/types';

export const pollIntervalMs = 1500;

type JobEnded = (job: CurrentJob) => void;

/**
 * `GET /api/jobs/current` (spec §11 polling): every 1.5 s while a job runs, stopped when idle. Pages that show job
 * progress (Ingestion, Integrity, Admin → DATs) call {@link watch} on mount and the returned function on destroy;
 * {@link refresh} checks once (e.g. right after starting a job). Listeners hear when a running job ends.
 */
class Jobs {
	current = $state<CurrentJob | null>(null);
	#watchers = 0;
	#timer: ReturnType<typeof setTimeout> | null = null;
	#ended = new Set<JobEnded>();

	/** Polls while anything watches and a job is running. */
	watch(): () => void {
		this.#watchers++;
		void this.refresh();
		return () => {
			this.#watchers--;
			if (this.#watchers === 0) this.#stop();
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
		if (next && this.#watchers > 0)
			this.#timer = setTimeout(() => void this.refresh(), pollIntervalMs);
	}

	#stop(): void {
		if (this.#timer !== null) clearTimeout(this.#timer);
		this.#timer = null;
	}
}

export const jobs = new Jobs();
