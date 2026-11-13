import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from '../api/client';
import { ApiError } from '../api/errors';
import type { CurrentJob } from '../api/types';
import { iconClass } from '../components/icons';
import { auth } from './auth.svelte';
import { jobs, pollIntervalMs } from './jobs.svelte';
import { toasts, toastDurationMs } from './toasts.svelte';
import { reviewJobs, summaryError } from './reviewJobs.svelte';
import { render, screen } from '@testing-library/svelte';
import ToastHost from '../components/ToastHost.svelte';

const app = vi.hoisted(() => ({ goto: vi.fn(), url: new URL('http://localhost/review/7') }));
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => app.goto(...args) }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('$app/state', () => ({
	page: {
		get url() {
			return app.url;
		}
	}
}));

const job = (id: string): CurrentJob => ({
	id,
	type: 'Ingest',
	mode: null,
	status: 'Running',
	current: 1,
	total: 3,
	currentItem: 'a.gb',
	startedAt: '2026-11-04T12:00:00Z'
});

function stubFetch(answer: (url: string) => Response) {
	const urls: string[] = [];
	vi.stubGlobal('fetch', (url: string) => {
		urls.push(url);
		return Promise.resolve(answer(url));
	});
	return urls;
}

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

describe('jobs polling', () => {
	beforeEach(() => {
		vi.useFakeTimers();
		resetClient();
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
	});

	it('polls every 1.5 s while a job runs, stops when idle, and reports the end', async () => {
		let running: CurrentJob | null = job('j1');
		const urls = stubFetch(() => (running ? ok(running) : new Response(null, { status: 204 })));
		const ended = vi.fn();
		const stopListening = jobs.onEnded(ended);

		const unwatch = jobs.watch();
		await vi.advanceTimersByTimeAsync(0);
		expect(jobs.current?.id).toBe('j1');

		await vi.advanceTimersByTimeAsync(pollIntervalMs);
		expect(urls).toHaveLength(2);

		running = null;
		await vi.advanceTimersByTimeAsync(pollIntervalMs);
		expect(jobs.current).toBeNull();
		expect(ended).toHaveBeenCalledWith(expect.objectContaining({ id: 'j1' }));

		await vi.advanceTimersByTimeAsync(pollIntervalMs * 3);
		expect(urls).toHaveLength(3); // idle: no more polling

		unwatch();
		stopListening();
	});

	it('stops polling when the last watcher leaves', async () => {
		const urls = stubFetch(() => ok(job('j2')));

		const unwatch = jobs.watch();
		await vi.advanceTimersByTimeAsync(0);
		unwatch();
		await vi.advanceTimersByTimeAsync(pollIntervalMs * 3);

		expect(urls).toHaveLength(1);
	});
});

describe('auth', () => {
	afterEach(() => vi.unstubAllGlobals());

	it('loads me, and treats 401 as signed out', async () => {
		stubFetch(() =>
			ok({ id: 1, username: 'boss', roles: ['admin', 'rom-manager'], mustChangePassword: false })
		);
		await auth.load();
		expect([auth.me?.username, auth.isAdmin, auth.loaded]).toEqual(['boss', true, true]);

		stubFetch(() => new Response(JSON.stringify({ code: 'Unauthorized' }), { status: 401 }));
		expect(await auth.load()).toBeNull();
		expect(auth.isAdmin).toBe(false);
	});
});

describe('toasts', () => {
	afterEach(() => vi.useRealTimers());

	it('shows the JobRunning toast for a 409 JobRunning only, and hides it after a while', () => {
		vi.useFakeTimers();

		expect(toasts.jobRunning(new ApiError(409, { code: 'JobRunning' }))).toBe(true);
		expect(toasts.jobRunning(new ApiError(409, { code: 'AmbiguousGroup' }))).toBe(false);
		expect(toasts.items.map((t) => t.kind)).toEqual(['warning']);

		vi.advanceTimersByTime(toastDurationMs);
		expect(toasts.items).toEqual([]);
	});
});

describe('icons', () => {
	it('maps semantic names, and unmapped systems fall back to a controller', () => {
		expect(iconClass('download')).toBe('bi bi-download');
		expect(iconClass('github')).toBe('fa-brands fa-github');
		expect(iconClass('system-snes')).toBe('bi bi-controller');
	});

	it('gives the disc systems brand or disc icons (B22)', () => {
		expect(iconClass('system-psx')).toBe('bi bi-playstation');
		expect(iconClass('system-psp')).toBe('bi bi-playstation');
		expect(iconClass('system-x360')).toBe('bi bi-xbox');
		expect(iconClass('system-gc')).toBe('bi bi-disc');
		expect(iconClass('system-wii')).toBe('bi bi-disc');
		expect(iconClass('system-saturn')).toBe('bi bi-disc');
	});
});

describe('review resolve jobs', () => {
	const started = { jobId: 'j9', reviewId: 7, fileName: 'disc2.iso', done: 'Added as Game.' };
	const summary = (over: object = {}) => ({
		reviewId: 7,
		fileName: 'disc2.iso',
		gameId: null,
		code: null,
		errors: null,
		...over
	});
	const jobRow = (status: string, s: object | null, error: string | null = null) =>
		ok({ id: 'j9', type: 'ReviewResolve', status, summary: s, error });

	beforeEach(() => {
		vi.useFakeTimers();
		resetClient();
		app.goto.mockClear();
		toasts.items = [];
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
	});

	/** Starts the job, answers `Running` once, then `last`; returns the polled URLs. */
	async function run(last: Response): Promise<string[]> {
		let polls = 0;
		const urls = stubFetch(() => (polls++ === 0 ? jobRow('Running', null) : last));
		reviewJobs.start(started);
		expect(reviewJobs.running?.jobId).toBe('j9');
		await vi.advanceTimersByTimeAsync(pollIntervalMs * 2);
		return urls;
	}

	it('polls every 1.5 s until the job ends, then opens the game on the item page', async () => {
		app.url = new URL('http://localhost/review/7');

		const urls = await run(jobRow('Completed', summary({ gameId: 'g1' })));

		expect(urls).toEqual(['/api/jobs/j9', '/api/jobs/j9']);
		expect(reviewJobs.running).toBeNull();
		expect(app.goto).toHaveBeenCalledWith('/games/g1');
		expect(toasts.items.map((t) => [t.text, t.link])).toEqual([['Added as Game.', undefined]]);
	});

	it('elsewhere shows a toast with an Open game link and never navigates', async () => {
		app.url = new URL('http://localhost/library');

		await run(jobRow('Completed', summary({ gameId: 'g1' })));

		expect(app.goto).not.toHaveBeenCalled();
		expect(toasts.items.map((t) => [t.text, t.kind, t.link])).toEqual([
			['disc2.iso added to the library.', 'success', { href: '/games/g1', text: 'Open game' }]
		]);
	});

	it('elsewhere shows a failure toast with an Open review item link, none for SourceMissing', async () => {
		app.url = new URL('http://localhost/review/8');
		await run(jobRow('Completed', summary({ code: 'VerifyFailed' })));
		await run(jobRow('Completed', summary({ code: 'SourceMissing' })));

		expect(toasts.items.map((t) => [t.text, t.kind, t.link])).toEqual([
			[
				'Resolving disc2.iso failed: VerifyFailed.',
				'danger',
				{ href: '/review/7', text: 'Open review item' }
			],
			['Resolving disc2.iso failed: SourceMissing.', 'danger', undefined]
		]);
		expect(app.goto).not.toHaveBeenCalled();
	});

	it('a Failed job without a summary is a failure toast elsewhere and an error on the item page', async () => {
		app.url = new URL('http://localhost/');
		await run(jobRow('Failed', null, 'boom'));
		expect(toasts.items.map((t) => t.text)).toEqual(['Resolving disc2.iso failed.']);

		app.url = new URL('http://localhost/review/7');
		const failures: unknown[] = [];
		const stop = reviewJobs.onFailure((e) => failures.push(e));
		await run(jobRow('Aborted', null, 'boom'));
		stop();
		expect(failures).toEqual([expect.objectContaining({ code: 'JobFailed', title: 'boom' })]);
	});

	it('hands a failed summary to the item page as the ApiError the endpoint would have thrown', async () => {
		app.url = new URL('http://localhost/review/7');
		const failures: unknown[] = [];
		const stop = reviewJobs.onFailure((e) => failures.push(e));

		await run(
			jobRow('Completed', summary({ code: 'ValidationFailed', errors: { title: ['Bad.'] } }))
		);
		stop();

		expect(failures).toEqual([
			expect.objectContaining({
				status: 400,
				code: 'ValidationFailed',
				errors: { title: ['Bad.'] }
			})
		]);
	});

	it('keeps polling after a failed poll', async () => {
		let polls = 0;
		stubFetch(() =>
			polls++ === 0
				? new Response(null, { status: 502 })
				: jobRow('Completed', summary({ gameId: 'g1' }))
		);
		app.url = new URL('http://localhost/');
		reviewJobs.start(started);
		await vi.advanceTimersByTimeAsync(pollIntervalMs * 2);
		expect(reviewJobs.running).toBeNull();
		expect(polls).toBe(2);
	});

	it('gives a failed summary the status of its code (B8)', () => {
		const status = (code: string) => summaryError(summary({ code })).status;
		expect([
			status('ValidationFailed'),
			status('NotFound'),
			status('NameTaken'),
			status('IoError')
		]).toEqual([400, 404, 409, 500]);
		expect(summaryError(summary({ code: 'NotFound' })).title).toBe('Not found');
	});
});

describe('toast links', () => {
	afterEach(() => vi.useRealTimers());

	it('renders the link, and a toast with one stays until closed', async () => {
		vi.useFakeTimers();
		toasts.items = [];
		toasts.show('plain');
		toasts.show('linked', 'success', { href: '/games/g1', text: 'Open game' });
		render(ToastHost);

		expect(screen.getByRole('link', { name: 'Open game' }).getAttribute('href')).toBe('/games/g1');
		vi.advanceTimersByTime(toastDurationMs);
		expect(toasts.items.map((t) => t.text)).toEqual(['linked']);
	});
});
