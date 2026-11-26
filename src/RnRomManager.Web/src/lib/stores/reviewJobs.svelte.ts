import { goto } from '$app/navigation';
import { resolve } from '$app/paths';
import { page } from '$app/state';
import { api } from '../api/client';
import { ApiError } from '../api/errors';
import type { Job, ReviewResolveSummary } from '#lib/types/index.js';
import { nextReviewItem } from '../review';
import { pollIntervalMs } from './jobs.svelte';
import { toasts } from './toasts.svelte';

/** A Review resolution started from an item page (B7). `done` is the success text shown on the item's page. */
export interface ReviewJob {
	jobId: string;
	reviewId: number;
	fileName: string;
	done: string;
	/** Review QoL B10: started by Use and Next. */
	next?: true;
}

// The codes the resolve endpoint answered 409 for before it ran as a job.
const conflicts = new Set([
	'JobRunning',
	'SourceMissing',
	'AmbiguousGroup',
	'TargetZipUnreadable',
	'InfoJsonNewerSchema',
	'Duplicate',
	'SetFileNotResolvable',
	'NameTaken',
	'NameTooLong'
]);

/** B8: a failed summary as the {@link ApiError} the synchronous endpoint would have thrown. */
export function summaryError(summary: ReviewResolveSummary): ApiError {
	const code = summary.code ?? 'HttpError';
	const status =
		code === 'ValidationFailed' ? 400 : code === 'NotFound' ? 404 : conflicts.has(code) ? 409 : 500;
	return new ApiError(status, {
		code,
		title: code === 'NotFound' ? 'Not found' : code,
		errors: summary.errors ?? undefined
	});
}

/**
 * B7/B8: the running Review resolution, kept across SPA navigation. Polls `GET /api/jobs/{id}` every 1.5 s until the
 * job ends. On the item's own page the outcome goes to that page (the game, or its error handling); anywhere else
 * it is a toast with a link, never a navigation.
 */
class ReviewJobs {
	running = $state<ReviewJob | null>(null);
	#onFailure: ((error: unknown) => void) | null = null;

	start(job: ReviewJob): void {
		this.running = job;
		this.#schedule(job);
	}

	/** The item page's error handling, used when a job ends while that page is open; returns the unregister. */
	onFailure(handler: (error: unknown) => void): () => void {
		this.#onFailure = handler;
		return () => {
			if (this.#onFailure === handler) this.#onFailure = null;
		};
	}

	#schedule(job: ReviewJob): void {
		setTimeout(() => void this.#poll(job), pollIntervalMs);
	}

	async #poll(job: ReviewJob): Promise<void> {
		let row: Job | undefined;
		try {
			row = await api.get<Job | undefined>(`/api/jobs/${encodeURIComponent(job.jobId)}`);
		} catch {
			row = undefined;
		}
		if (!row || row.status === 'Running') {
			// Still running, or no answer: try again at the next tick.
			this.#schedule(job);
			return;
		}
		void this.#finish(job, row);
	}

	async #finish(job: ReviewJob, row: Job): Promise<void> {
		const item = resolve('/review/[id]', { id: String(job.reviewId) });
		const summary = row.summary as ReviewResolveSummary | null;
		if (summary && !summary.code && summary.gameId) {
			const game = resolve('/games/[id]', { id: summary.gameId });
			let target = game;
			if (job.next && page.url.pathname === item) {
				// Review QoL B13: look the next item up again (the queue may have changed); `running` stays set meanwhile.
				const next = await nextReviewItem(job.reviewId).catch(() => null);
				if (next !== null) target = resolve('/review/[id]', { id: String(next) });
			}
			this.running = null;
			if (page.url.pathname === item) {
				toasts.show(job.done, 'success');
				void goto(target);
			} else
				toasts.show(`${job.fileName} added to the library.`, 'success', {
					href: game,
					text: 'Open game'
				});
			return;
		}
		this.running = null;
		const here = page.url.pathname === item;
		if (summary?.code === 'SourceMissing') {
			// §7.5: the row is deleted, so there is no item to link to.
			if (here) {
				toasts.show('The source file is gone, so the item was removed.', 'warning');
				void goto(resolve('/review'));
			} else toasts.show(`Resolving ${job.fileName} failed: SourceMissing.`, 'danger');
			return;
		}
		if (here) {
			this.#onFailure?.(
				summary
					? summaryError(summary)
					: new ApiError(500, { code: 'JobFailed', title: row.error ?? 'The job failed.' })
			);
			return;
		}
		toasts.show(
			summary
				? `Resolving ${job.fileName} failed: ${summary.code}.`
				: `Resolving ${job.fileName} failed.`,
			'danger',
			{ href: item, text: 'Open review item' }
		);
	}
}

export const reviewJobs = new ReviewJobs();
