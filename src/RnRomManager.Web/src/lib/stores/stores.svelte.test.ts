import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from '../api/client';
import { ApiError } from '../api/errors';
import type { CurrentJob } from '../api/types';
import { iconClass } from '../components/icons';
import { auth } from './auth.svelte';
import { jobs, pollIntervalMs } from './jobs.svelte';
import { toasts, toastDurationMs } from './toasts.svelte';

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
