import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from '../api/client';
import { ApiError } from '../api/errors';
import type { CurrentJob } from '../api/types';
import { iconClass } from '../components/icons';
import { auth } from './auth.svelte';
import { idlePollIntervalMs, jobs, pollIntervalMs } from './jobs.svelte';
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

	describe('idle poll (ROM Zip Config B17)', () => {
		let visibility: DocumentVisibilityState;
		beforeEach(() => {
			visibility = 'visible';
			vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);
		});
		afterEach(() => vi.restoreAllMocks());
		const idle = () => stubFetch(() => new Response(null, { status: 204 }));
		const setVisibility = (state: DocumentVisibilityState) => {
			visibility = state;
			document.dispatchEvent(new Event('visibilitychange'));
		};

		it('polls every 15 s while watched, idle and visible, and stops when unwatched', async () => {
			const urls = idle();
			const unwatch = jobs.watch();
			await vi.advanceTimersByTimeAsync(0);
			await vi.advanceTimersByTimeAsync(idlePollIntervalMs * 2);
			expect(urls).toHaveLength(3);

			unwatch();
			await vi.advanceTimersByTimeAsync(idlePollIntervalMs * 2);
			expect(urls).toHaveLength(3);
		});

		it('a hidden tab stops the idle poll, and becoming visible refreshes once', async () => {
			const urls = idle();
			const unwatch = jobs.watch();
			await vi.advanceTimersByTimeAsync(0);

			setVisibility('hidden');
			await vi.advanceTimersByTimeAsync(idlePollIntervalMs * 3);
			expect(urls).toHaveLength(1);

			setVisibility('visible');
			await vi.advanceTimersByTimeAsync(0);
			expect(urls).toHaveLength(2);
			unwatch();
		});

		it('a job started elsewhere appears on the next idle poll', async () => {
			let running: CurrentJob | null = null;
			stubFetch(() => (running ? ok(running) : new Response(null, { status: 204 })));
			const unwatch = jobs.watch();
			await vi.advanceTimersByTimeAsync(0);
			running = job('j9');

			await vi.advanceTimersByTimeAsync(idlePollIntervalMs);

			expect(jobs.current?.id).toBe('j9');
			running = null;
			await vi.advanceTimersByTimeAsync(pollIntervalMs);
			unwatch();
		});
	});

	it('keeps one poll chain when watched from effects and refreshed while a refresh is in flight', async () => {
		const urls = stubFetch(() => ok({ ...job('j3'), current: urls.length }));
		const cleanup = $effect.root(() => {
			$effect(() => jobs.watch());
			$effect(() => jobs.watch());
		});
		void jobs.refresh(); // overlaps the effects' refresh, as starting a job does
		await vi.advanceTimersByTimeAsync(0);
		const start = urls.length;

		await vi.advanceTimersByTimeAsync(pollIntervalMs * 4);

		expect(urls.length - start).toBe(4);
		cleanup();
		await vi.advanceTimersByTimeAsync(pollIntervalMs * 3);
		expect(urls.length - start).toBe(4);
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
		stubFetch(() => ok({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false }));
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
		expect(iconClass('keyboard')).toBe('bi bi-keyboard');
		expect(iconClass('system-snes')).toBe('bi bi-controller');
	});

	it('gives the disc systems brand or disc icons (B22)', () => {
		expect(iconClass('system-psx')).toBe('bi bi-playstation');
		expect(iconClass('system-psp')).toBe('bi bi-playstation');
		expect(iconClass('system-x360')).toBe('bi bi-xbox');
		expect(iconClass('system-gc')).toBe('bi bi-disc');
		expect(iconClass('system-wii')).toBe('bi bi-disc');
		expect(iconClass('system-saturn')).toBe('bi bi-disc');
		expect(iconClass('system-segacd')).toBe('bi bi-disc');
		expect(iconClass('system-3do')).toBe('bi bi-disc');
		expect(iconClass('system-tgcd')).toBe('bi bi-disc');
		expect(iconClass('system-dos')).toBe('bi bi-terminal');
		expect(iconClass('system-mame2003')).toBe('bi bi-joystick');
		expect(iconClass('system-fbneo')).toBe('bi bi-joystick');
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

	describe('Use and Next (B13)', () => {
		const lookupUrl = '/api/review?status=Pending&page=1&pageSize=2';
		const success = () => jobRow('Completed', summary({ gameId: 'g1' }));
		const queue = (...ids: number[]) =>
			ok({ items: ids.map((id) => ({ id })), page: 1, pageSize: 2, total: ids.length });

		/** Starts a `next` job that succeeds at its first poll; `lookup` answers the next-item request. */
		function start(lookup: () => Response | Promise<Response>): string[] {
			const urls: string[] = [];
			vi.stubGlobal('fetch', (url: string) => {
				urls.push(url);
				return Promise.resolve(url === lookupUrl ? lookup() : success());
			});
			reviewJobs.start({ ...started, next: true });
			return urls;
		}

		it('on the item page looks the next item up again and opens it', async () => {
			app.url = new URL('http://localhost/review/7');
			const urls = start(() => queue(7, 9));

			await vi.advanceTimersByTimeAsync(pollIntervalMs);

			expect(urls).toEqual(['/api/jobs/j9', lookupUrl]);
			expect(app.goto).toHaveBeenCalledExactlyOnceWith('/review/9');
			expect(toasts.items.map((t) => [t.text, t.link])).toEqual([['Added as Game.', undefined]]);
			expect(reviewJobs.running).toBeNull();
		});

		it.each([
			['none is left', () => queue(7)],
			['the lookup fails', () => new Response(null, { status: 500 })]
		])('opens the game when %s', async (_case, lookup) => {
			app.url = new URL('http://localhost/review/7');
			start(lookup);

			await vi.advanceTimersByTimeAsync(pollIntervalMs);

			expect(app.goto).toHaveBeenCalledExactlyOnceWith('/games/g1');
			expect(toasts.items.map((t) => t.text)).toEqual(['Added as Game.']);
		});

		it('stays running until the lookup settles', async () => {
			app.url = new URL('http://localhost/review/7');
			let answer!: (r: Response) => void;
			start(() => new Promise<Response>((r) => (answer = r)));

			await vi.advanceTimersByTimeAsync(pollIntervalMs);
			expect(reviewJobs.running?.jobId).toBe('j9');
			expect(app.goto).not.toHaveBeenCalled();

			answer(queue(9));
			await vi.advanceTimersByTimeAsync(0);
			expect(reviewJobs.running).toBeNull();
			expect(app.goto).toHaveBeenCalledWith('/review/9');
		});

		it('leaving the page during the lookup shows the off-page toast and does not navigate', async () => {
			app.url = new URL('http://localhost/review/7');
			let answer!: (r: Response) => void;
			start(() => new Promise<Response>((r) => (answer = r)));
			await vi.advanceTimersByTimeAsync(pollIntervalMs);

			app.url = new URL('http://localhost/library');
			answer(queue(9));
			await vi.advanceTimersByTimeAsync(0);

			expect(app.goto).not.toHaveBeenCalled();
			expect(reviewJobs.running).toBeNull();
			expect(toasts.items.map((t) => [t.text, t.link])).toEqual([
				['disc2.iso added to the library.', { href: '/games/g1', text: 'Open game' }]
			]);
		});

		it('off the page at the end: the usual toast, no lookup', async () => {
			app.url = new URL('http://localhost/library');
			const urls = start(() => queue(9));

			await vi.advanceTimersByTimeAsync(pollIntervalMs);

			expect(urls).toEqual(['/api/jobs/j9']);
			expect(app.goto).not.toHaveBeenCalled();
			expect(toasts.items.map((t) => t.text)).toEqual(['disc2.iso added to the library.']);
		});

		it('a failure is handled as Use, with no lookup', async () => {
			app.url = new URL('http://localhost/review/7');
			const failures: unknown[] = [];
			const stop = reviewJobs.onFailure((e) => failures.push(e));
			const urls: string[] = [];
			vi.stubGlobal('fetch', (url: string) => {
				urls.push(url);
				return Promise.resolve(jobRow('Completed', summary({ code: 'VerifyFailed' })));
			});
			reviewJobs.start({ ...started, next: true });

			await vi.advanceTimersByTimeAsync(pollIntervalMs);
			stop();

			expect(urls).toEqual(['/api/jobs/j9']);
			expect(failures).toEqual([expect.objectContaining({ code: 'VerifyFailed' })]);
			expect(app.goto).not.toHaveBeenCalled();
		});

		it('a job without next makes no lookup', async () => {
			app.url = new URL('http://localhost/review/7');
			const urls = stubFetch(success);
			reviewJobs.start(started);

			await vi.advanceTimersByTimeAsync(pollIntervalMs);

			expect(urls).toEqual(['/api/jobs/j9']);
			expect(app.goto).toHaveBeenCalledExactlyOnceWith('/games/g1');
		});
	});
});

describe('toast links', () => {
	afterEach(() => vi.useRealTimers());

	it('renders the link, and a toast with one closes itself like any other (B1)', async () => {
		vi.useFakeTimers();
		toasts.items = [];
		toasts.show('plain');
		toasts.show('linked', 'success', { href: '/games/g1', text: 'Open game' });
		render(ToastHost);

		expect(screen.getByRole('link', { name: 'Open game' }).getAttribute('href')).toBe('/games/g1');
		vi.advanceTimersByTime(toastDurationMs - 1);
		expect(toasts.items.map((t) => t.text)).toEqual(['plain', 'linked']);
		vi.advanceTimersByTime(1);
		expect(toasts.items).toEqual([]);
	});

	it('a paused toast outlives its timer and closes 5 s after resume; others are unaffected (B2)', () => {
		vi.useFakeTimers();
		toasts.items = [];
		const paused = toasts.show('paused', 'info', { href: '/games/g1', text: 'Open game' });
		toasts.show('other');
		toasts.pause(paused);

		vi.advanceTimersByTime(toastDurationMs * 3);
		expect(toasts.items.map((t) => t.text)).toEqual(['paused']);

		toasts.resume(paused);
		vi.advanceTimersByTime(toastDurationMs - 1);
		expect(toasts.items.map((t) => t.text)).toEqual(['paused']);
		vi.advanceTimersByTime(1);
		expect(toasts.items).toEqual([]);
	});

	it('resuming a dismissed toast does nothing (B2)', () => {
		vi.useFakeTimers();
		toasts.items = [];
		const id = toasts.show('gone');
		toasts.dismiss(id);
		toasts.resume(id);
		toasts.show('kept');
		vi.advanceTimersByTime(toastDurationMs - 1);
		expect(toasts.items.map((t) => t.text)).toEqual(['kept']);
	});
});
