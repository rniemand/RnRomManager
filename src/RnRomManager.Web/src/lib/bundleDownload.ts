import { api } from './api/client';
import { isApiError } from './api/errors';
import type {
	BundleDownloadTicket,
	BundleEntry,
	BundleTicketStatus,
	RunItem,
	RunView,
	DownloadDeps
} from '#lib/types/index.js';
import { entryKey } from './stores/bundle.svelte';
import type { RunItemState, PauseReason, RunStatus } from '#lib/enums/index.js';

/** B7: how often the page asks for the current ticket's state. */
export const pollIntervalMs = 500;

/** Starts a native download of a same-origin URL, as a click on a hidden link would. */
export function clickDownload(url: string, doc: Document = document): void {
	const link = doc.createElement('a');
	link.href = url;
	link.download = '';
	link.rel = 'noopener';
	link.hidden = true;
	doc.body.append(link);
	link.click();
	link.remove();
}

/** The real server, clock and browser. Requests don't redirect on 401: the run pauses with a sign-in action (B7). */
export const browserDeps: DownloadDeps = {
	issue: (identity) =>
		api.post<BundleDownloadTicket>(
			'/api/bundle-downloads',
			{ gameId: identity.gameId, releaseSha1: identity.releaseSha1, member: identity.member },
			{ noRedirect: true }
		),
	status: (ticket) =>
		api.get<BundleTicketStatus>(`/api/bundle-downloads/${encodeURIComponent(ticket)}`, {
			noRedirect: true
		}),
	start: (url) => clickDownload(url),
	wait: (ms) => new Promise((resolve) => setTimeout(resolve, ms))
};

const retriable = (s: RunItemState) => s === 'failed' || s === 'expired';
const terminal = (s: RunItemState) => s === 'sent' || s === 'failed' || s === 'expired';

/**
 * B6–B8: sends a snapshot of the cart to the browser's Downloads flow one file at a time, in order. The next file is
 * issued only after the server reports the previous one `sent`, `failed` or `expired`. Ordinary failures are recorded
 * and skipped; an expired ticket, a lost answer, a lost ticket, sign-in, busy and full pause the run. The runner never
 * claims a file reached the disk: `sent` means the server finished sending it.
 */
export class BundleDownloadRun {
	#deps: DownloadDeps;
	#items: RunItem[] = [];
	#status: RunStatus = 'idle';
	#pause: PauseReason | null = null;
	#stopRequested = false;
	/** The file whose ticket the server lost, while the run waits on the user's choice (B8). */
	#lost: string | null = null;
	/** Bumped by every start and end, so a loop left over from an earlier run or page stops scheduling. */
	#generation = 0;
	#listeners = new Set<(view: RunView) => void>();
	/** Called after a file fails, so the page can refresh availability (B5). */
	onfailure: (() => void) | null = null;

	constructor(deps: DownloadDeps = browserDeps) {
		this.#deps = deps;
	}

	get view(): RunView {
		return {
			status: this.#status,
			pause: this.#pause,
			stopRequested: this.#stopRequested,
			canRetry: this.canRetry,
			items: this.#items.map((i) => ({ ...i }))
		};
	}

	get active(): boolean {
		return this.#status === 'running';
	}

	/** True when retry has something to do: a pause, or failed, expired or not-yet-sent files after the run stopped. */
	get canRetry(): boolean {
		if (this.#status === 'paused') return true;
		if (this.#status !== 'stopped' && this.#status !== 'finished') return false;
		return this.#items.some((i) => retriable(i.state) || i.state === 'waiting');
	}

	subscribe(listener: (view: RunView) => void): () => void {
		this.#listeners.add(listener);
		listener(this.view);
		return () => this.#listeners.delete(listener);
	}

	/** B6/B8: a new run over an immutable copy of these entries. Ignored while a run is going or paused. */
	start(entries: BundleEntry[]): void {
		if (this.#status === 'running' || this.#status === 'paused') return;
		this.#items = entries.map((entry) => ({
			key: entryKey(entry),
			entry: plainEntry(entry),
			state: 'waiting',
			errorCode: null,
			ticket: null
		}));
		this.#begin();
	}

	/** B6: lets the current file reach a terminal state, then stops before the next one. */
	stopAfterCurrent(): void {
		if (this.#status === 'paused') {
			this.#settleStop();
			return;
		}
		if (this.#status !== 'running') return;
		this.#stopRequested = true;
		this.#emit();
	}

	/**
	 * B8: resumes a paused run on its open ticket, or reissues failed and expired files in their original order and
	 * continues the waiting ones. Sent files are never reissued. The file whose ticket was just lost is reissued too;
	 * one the user skipped earlier stays "Transfer status unknown".
	 */
	retry(): void {
		if (!this.canRetry) return;
		for (const item of this.#items) {
			if (retriable(item.state) || (item.state === 'unknown' && item.key === this.#lost)) {
				item.state = 'waiting';
				item.errorCode = null;
				item.ticket = null;
			}
		}
		this.#begin();
	}

	/** B8: after a lost ticket, leaves that file as "Transfer status unknown" and goes on with the next one. */
	skipUnknown(): void {
		if (this.#status !== 'paused' || this.#pause !== 'unknown') return;
		this.#begin();
	}

	/** Sign-out or leaving the page: stop scheduling. A native transfer already started may finish on its own (B7). */
	end(): void {
		this.#generation++;
		this.#lost = null;
		if (this.#status === 'running' || this.#status === 'paused') {
			this.#status = 'ended';
			this.#pause = null;
			this.#stopRequested = false;
			this.#emit();
		}
	}

	#begin(): void {
		this.#generation++;
		this.#lost = null;
		this.#status = 'running';
		this.#pause = null;
		this.#stopRequested = false;
		this.#emit();
		void this.#loop(this.#generation);
	}

	#settleStop(): void {
		this.#generation++;
		this.#status = 'stopped';
		this.#pause = null;
		this.#stopRequested = false;
		this.#emit();
	}

	#halt(reason: PauseReason): void {
		this.#status = 'paused';
		this.#pause = reason;
		this.#stopRequested = false;
		this.#emit();
	}

	async #loop(generation: number): Promise<void> {
		while (generation === this.#generation) {
			// An item with an open ticket (after a lost status answer) is resumed before new ones are issued.
			const open = this.#items.find((i) => i.ticket && !terminal(i.state));
			const next = open ?? this.#items.find((i) => i.state === 'waiting');
			if (!next) {
				this.#status = 'finished';
				this.#stopRequested = false;
				this.#emit();
				return;
			}
			if (!open && this.#stopRequested) {
				this.#settleStop();
				return;
			}
			const carryOn = await this.#send(next, generation);
			if (!carryOn || generation !== this.#generation) return;
		}
	}

	/** One file: issue (unless resuming), click, poll to a terminal state. False when the run paused or was ended. */
	async #send(item: RunItem, generation: number): Promise<boolean> {
		const live = () => generation === this.#generation;
		if (!item.ticket) {
			item.state = 'issuing';
			this.#emit();
			let issued: BundleDownloadTicket;
			try {
				issued = await this.#deps.issue(item.entry);
			} catch (error) {
				if (!live()) return false;
				item.state = 'waiting';
				if (isApiError(error) && (error.status === 404 || error.status === 400)) {
					// B7: an ordinary unavailable file is recorded and skipped.
					this.#fail(item, error.status === 404 ? 'Unavailable' : 'Invalid');
					return true;
				}
				this.#halt(this.#pauseFor(error));
				return false;
			}
			if (!live()) return false;
			item.ticket = issued.ticket;
			item.state = 'pending';
			this.#emit();
			this.#deps.start(issued.fileUrl);
		}

		for (;;) {
			await this.#deps.wait(pollIntervalMs);
			if (!live()) return false;
			let answer: BundleTicketStatus;
			try {
				answer = await this.#deps.status(item.ticket!);
			} catch (error) {
				if (!live()) return false;
				if (isApiError(error) && error.status === 404) {
					// B8: the server lost this ticket (restart); the user decides whether to resend the file.
					item.state = 'unknown';
					item.ticket = null;
					this.#lost = item.key;
					this.#halt('unknown');
					return false;
				}
				this.#halt(this.#pauseFor(error));
				return false;
			}
			if (!live()) return false;
			item.state = answer.state;
			item.errorCode = answer.state === 'failed' ? answer.errorCode : null;
			if (!terminal(answer.state)) {
				this.#emit();
				continue;
			}
			item.ticket = null;
			if (answer.state === 'failed') {
				this.#emit();
				this.onfailure?.();
				return true;
			}
			if (answer.state === 'expired') {
				// B7: the browser never fetched the file; automatic downloads are probably blocked.
				this.#halt('blocked');
				return false;
			}
			this.#emit();
			return true;
		}
	}

	#fail(item: RunItem, code: string): void {
		item.state = 'failed';
		item.errorCode = code;
		item.ticket = null;
		this.#emit();
		this.onfailure?.();
	}

	#pauseFor(error: unknown): PauseReason {
		if (isApiError(error)) {
			if (error.status === 401) return 'signIn';
			if (error.code === 'BundleDownloadBusy') return 'busy';
			if (error.status === 503) return 'full';
		}
		return 'network';
	}

	#emit(): void {
		const view = this.view;
		for (const listener of this.#listeners) listener(view);
	}
}

/** A plain copy of an entry, so later cart changes (it may be a Svelte state proxy) never reach the run. */
function plainEntry(entry: BundleEntry): BundleEntry {
	return {
		gameId: entry.gameId,
		releaseSha1: entry.releaseSha1,
		member: entry.member ? { sha1: entry.member.sha1, fileName: entry.member.fileName } : null,
		gameTitle: entry.gameTitle,
		fileName: entry.fileName
	};
}

/** The words for a file's state in the run table. */
export function runItemLabel(item: RunItem): string {
	switch (item.state) {
		case 'waiting':
			return 'Waiting';
		case 'issuing':
		case 'pending':
			return 'Starting…';
		case 'sending':
			return 'Sending…';
		case 'sent':
			return 'Sent to the browser';
		case 'expired':
			return 'Not started by the browser';
		case 'unknown':
			return 'Transfer status unknown';
		case 'failed':
			return failureLabel(item.errorCode);
	}
}

function failureLabel(code: string | null): string {
	switch (code) {
		case 'Unavailable':
			return 'Failed: no longer in the library';
		case 'OpenFailed':
			return 'Failed: the file could not be opened';
		case 'Aborted':
			return 'Failed: the transfer was cancelled';
		case 'Stalled':
			return 'Failed: the transfer stalled';
		case 'Invalid':
			return 'Failed: invalid entry';
		default:
			return 'Failed: read or write error';
	}
}
