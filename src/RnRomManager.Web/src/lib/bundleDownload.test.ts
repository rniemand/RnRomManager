import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './api/errors';
import type { BundleEntry, BundleTicketStatus, DownloadDeps, RunView } from '#lib/types/index.js';
import type { BundleTicketState } from '#lib/enums/index.js';
import { BundleDownloadRun, clickDownload, pollIntervalMs, runItemLabel } from './bundleDownload';

const gameA = '8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
const gameB = '1d2e3f40-5a6b-4c7d-8e9f-a0b1c2d3e4f5';
const entry = (n: number, gameId = gameA): BundleEntry => ({
	gameId,
	releaseSha1: n.toString(16).padStart(40, '0'),
	member: null,
	gameTitle: `Game ${n}`,
	fileName: `game ${n}.bin`
});
const problem = (status: number, code?: string) => new ApiError(status, { status, code });

type StatusAnswer = BundleTicketState | Error | BundleTicketStatus;

/**
 * A fake server: tickets are `t1`, `t2`… in issue order; each file's status answers are scripted by release number
 * (default: `sending`, then `sent`), and issue failures likewise. `wait` resolves at once, so polling is instant.
 */
function fakeServer() {
	let next = 0;
	const tickets = new Map<string, number>();
	const statusScript = new Map<number, StatusAnswer[]>();
	const issueScript = new Map<number, Error[]>();
	const issued: number[] = [];
	const clicked: string[] = [];
	const polled: string[] = [];
	const waits: number[] = [];
	const release = (e: { releaseSha1: string }) => parseInt(e.releaseSha1, 16);

	const deps: DownloadDeps = {
		issue: async (identity) => {
			const n = release(identity);
			const failure = issueScript.get(n)?.shift();
			if (failure) throw failure;
			issued.push(n);
			const ticket = `t${++next}`;
			tickets.set(ticket, n);
			return { ticket, fileUrl: `/api/bundle-downloads/${ticket}/file` };
		},
		status: async (ticket) => {
			polled.push(ticket);
			const n = tickets.get(ticket)!;
			if (!statusScript.has(n)) statusScript.set(n, ['sending', 'sent']);
			const script = statusScript.get(n)!;
			const answer = script.length > 1 ? script.shift()! : script[0];
			if (answer instanceof Error) {
				// A one-off failure must not repeat forever.
				if (script.length === 1) statusScript.set(n, ['sent']);
				throw answer;
			}
			return typeof answer === 'string'
				? { state: answer, errorCode: answer === 'failed' ? 'IoError' : null }
				: answer;
		},
		start: (url) => clicked.push(url),
		wait: async (ms) => void waits.push(ms)
	};

	return {
		deps,
		issued,
		clicked,
		polled,
		waits,
		/** Status answers for a file's next ticket(s), in order; the last one repeats. */
		status: (n: number, ...answers: StatusAnswer[]) => statusScript.set(n, answers),
		issueFails: (n: number, ...errors: Error[]) => issueScript.set(n, errors)
	};
}

function track(run: BundleDownloadRun) {
	const views: RunView[] = [];
	run.subscribe((v) => views.push(v));
	const last = () => views.at(-1)!;
	return {
		views,
		last,
		/** Waits until the run is no longer `running`. */
		settled: () => vi.waitFor(() => expect(last().status).not.toBe('running')),
		states: () => last().items.map((i) => i.state)
	};
}

describe('BundleDownloadRun (B6–B8)', () => {
	it('sends files one at a time in cart order, advancing only on a terminal state', async () => {
		const server = fakeServer();
		server.status(1, 'pending', 'pending', 'sending', 'sent');
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2, gameB)]);
		await t.settled();

		expect(t.last().status).toBe('finished');
		expect(server.issued).toEqual([1, 2]);
		expect(server.clicked).toEqual([
			'/api/bundle-downloads/t1/file',
			'/api/bundle-downloads/t2/file'
		]);
		expect(server.polled).toEqual(['t1', 't1', 't1', 't1', 't2', 't2']);
		expect(server.waits.every((ms) => ms === pollIntervalMs)).toBe(true);
		// The second file was issued only after the first was sent.
		const secondIssued = t.views.findIndex((v) => v.items[1]?.state === 'issuing');
		expect(t.views[secondIssued].items[0].state).toBe('sent');
		expect(t.states()).toEqual(['sent', 'sent']);
	});

	it('runs an immutable snapshot of the entries', async () => {
		const server = fakeServer();
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);
		const entries = [entry(1)];

		run.start(entries);
		entries.push(entry(2));
		entries[0].fileName = 'changed.bin';
		await t.settled();

		expect(server.issued).toEqual([1]);
		expect(t.last().items[0].entry.fileName).toBe('game 1.bin');
	});

	it('records an unavailable file and a failed transfer, skips them and refreshes availability', async () => {
		const server = fakeServer();
		server.issueFails(1, problem(404));
		server.status(2, 'sending', { state: 'failed', errorCode: 'OpenFailed' });
		const run = new BundleDownloadRun(server.deps);
		const failures = vi.fn();
		run.onfailure = failures;
		const t = track(run);

		run.start([entry(1), entry(2), entry(3)]);
		await t.settled();

		expect(t.last().status).toBe('finished');
		expect(t.states()).toEqual(['failed', 'failed', 'sent']);
		expect(t.last().items.map((i) => i.errorCode)).toEqual(['Unavailable', 'OpenFailed', null]);
		expect(runItemLabel(t.last().items[1])).toBe('Failed: the file could not be opened');
		expect(failures).toHaveBeenCalledTimes(2);
		expect(t.last().canRetry).toBe(true);
	});

	it('pauses on an expired ticket (blocked downloads) and retry reissues it without resending sent files', async () => {
		const server = fakeServer();
		server.status(2, 'pending', 'expired');
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2), entry(3)]);
		await t.settled();

		expect(t.last().status).toBe('paused');
		expect(t.last().pause).toBe('blocked');
		expect(t.states()).toEqual(['sent', 'expired', 'waiting']);
		expect(server.issued).toEqual([1, 2]);

		server.status(2, 'sent');
		run.retry();
		await t.settled();

		expect(t.last().status).toBe('finished');
		expect(server.issued).toEqual([1, 2, 2, 3]);
		expect(t.states()).toEqual(['sent', 'sent', 'sent']);
	});

	it('retries failed files in their original order after the run finished, skipping sent ones', async () => {
		const server = fakeServer();
		server.status(1, 'failed');
		server.status(3, 'failed');
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2), entry(3)]);
		await t.settled();
		expect(t.states()).toEqual(['failed', 'sent', 'failed']);

		server.status(1, 'sent');
		server.status(3, 'sent');
		run.retry();
		await t.settled();

		expect(server.issued).toEqual([1, 2, 3, 1, 3]);
		expect(t.states()).toEqual(['sent', 'sent', 'sent']);
		expect(t.last().canRetry).toBe(false);
	});

	it('pauses on a lost status answer and resumes polling the same ticket', async () => {
		const server = fakeServer();
		server.status(1, 'sending', new ApiError(0));
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2)]);
		await t.settled();

		expect(t.last().pause).toBe('network');
		expect(t.last().items[0]).toMatchObject({ state: 'sending', ticket: 't1' });

		run.retry();
		await t.settled();

		expect(t.last().status).toBe('finished');
		expect(server.issued).toEqual([1, 2]);
		expect(server.polled.filter((p) => p === 't1').length).toBe(3);
	});

	it('marks a ticket the server lost as unknown and lets the user resend it', async () => {
		const server = fakeServer();
		server.status(1, 'sending', problem(404));
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2)]);
		await t.settled();

		expect(t.last().pause).toBe('unknown');
		expect(runItemLabel(t.last().items[0])).toBe('Transfer status unknown');

		server.status(1, 'sent');
		run.retry();
		await t.settled();

		expect(server.issued).toEqual([1, 1, 2]);
		expect(t.states()).toEqual(['sent', 'sent']);
	});

	it('can skip a lost ticket, which later retries leave alone', async () => {
		const server = fakeServer();
		server.status(1, 'sending', problem(404));
		server.status(2, 'failed');
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2)]);
		await t.settled();
		run.skipUnknown();
		await t.settled();

		expect(t.states()).toEqual(['unknown', 'failed']);
		server.status(2, 'sent');
		run.retry();
		await t.settled();

		expect(server.issued).toEqual([1, 2, 2]);
		expect(t.states()).toEqual(['unknown', 'sent']);
	});

	it('pauses for sign-in, a busy account and a full server, and continues on retry', async () => {
		for (const [error, reason] of [
			[problem(401), 'signIn'],
			[problem(409, 'BundleDownloadBusy'), 'busy'],
			[problem(503, 'BundleDownloadsFull'), 'full']
		] as const) {
			const server = fakeServer();
			server.issueFails(2, error);
			const run = new BundleDownloadRun(server.deps);
			const t = track(run);

			run.start([entry(1), entry(2)]);
			await t.settled();
			expect(t.last().pause).toBe(reason);
			expect(t.states()).toEqual(['sent', 'waiting']);

			run.retry();
			await t.settled();
			expect(t.states()).toEqual(['sent', 'sent']);
		}
	});

	it('stops after the current file and can continue the same snapshot', async () => {
		const server = fakeServer();
		let release!: () => void;
		const gate = new Promise<void>((r) => (release = r));
		const wait = server.deps.wait;
		server.deps.wait = async (ms) => {
			await wait(ms);
			if (server.issued.length === 1) await gate;
		};
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2), entry(3)]);
		await vi.waitFor(() => expect(t.last().items[0].state).toBe('pending'));
		run.stopAfterCurrent();
		expect(t.last().stopRequested).toBe(true);
		release();
		await t.settled();

		expect(t.last().status).toBe('stopped');
		expect(t.states()).toEqual(['sent', 'waiting', 'waiting']);
		expect(server.issued).toEqual([1]);

		run.retry();
		await t.settled();
		expect(server.issued).toEqual([1, 2, 3]);
	});

	it('stops scheduling on sign-out or leaving the page, even mid-poll', async () => {
		const server = fakeServer();
		let release!: () => void;
		const gate = new Promise<void>((r) => (release = r));
		server.deps.wait = () => gate;
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1), entry(2)]);
		await vi.waitFor(() => expect(server.clicked.length).toBe(1));
		run.end();
		release();
		await Promise.resolve();
		await new Promise((r) => setTimeout(r, 0));

		expect(t.last().status).toBe('ended');
		expect(server.polled).toEqual([]);
		expect(server.issued).toEqual([1]);
		expect(t.last().canRetry).toBe(false);
	});

	it('ignores a second start while a run is going', async () => {
		const server = fakeServer();
		const run = new BundleDownloadRun(server.deps);
		const t = track(run);

		run.start([entry(1)]);
		run.start([entry(2)]);
		await t.settled();

		expect(server.issued).toEqual([1]);
	});
});

describe('clickDownload', () => {
	afterEach(() => vi.restoreAllMocks());

	it('clicks a hidden same-origin download link and removes it', () => {
		const clicks: HTMLAnchorElement[] = [];
		vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
			this: HTMLAnchorElement
		) {
			clicks.push(this);
		});

		clickDownload('/api/bundle-downloads/t1/file');

		expect(clicks).toHaveLength(1);
		expect(clicks[0].getAttribute('href')).toBe('/api/bundle-downloads/t1/file');
		expect(clicks[0].hasAttribute('download')).toBe(true);
		expect(document.querySelector('a[download]')).toBeNull();
	});
});
