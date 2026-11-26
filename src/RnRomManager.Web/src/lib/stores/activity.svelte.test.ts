import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from '../api/client';
import type { Activity } from '../api/types';
import { activity } from './activity.svelte';
import { idlePollIntervalMs, pollIntervalMs } from './jobs.svelte';

describe('activity store (L3)', () => {
	let answer: () => Response;
	let urls: string[];
	let visibility: DocumentVisibilityState;
	const busy = (body: Activity) => () => new Response(JSON.stringify(body), { status: 200 });
	const free = () => new Response(null, { status: 204 });
	const setVisibility = (state: DocumentVisibilityState) => {
		visibility = state;
		document.dispatchEvent(new Event('visibilitychange'));
	};

	beforeEach(() => {
		vi.useFakeTimers();
		resetClient();
		activity.current = null;
		urls = [];
		answer = free;
		visibility = 'visible';
		vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);
		vi.stubGlobal('fetch', (url: string) => {
			urls.push(url);
			return Promise.resolve(answer());
		});
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
		vi.restoreAllMocks();
	});

	it('polls every 1.5 s while busy and every 15 s while idle, and stops when unwatched', async () => {
		answer = busy({ activity: 'Saving changes', jobType: null });
		const unwatch = activity.watch();
		await vi.advanceTimersByTimeAsync(0);
		expect(activity.current).toEqual({ activity: 'Saving changes', jobType: null });

		await vi.advanceTimersByTimeAsync(pollIntervalMs);
		expect(urls).toEqual(['/api/activity', '/api/activity']);

		answer = free;
		await vi.advanceTimersByTimeAsync(pollIntervalMs);
		expect(activity.current).toBeNull();
		await vi.advanceTimersByTimeAsync(idlePollIntervalMs - 1);
		expect(urls).toHaveLength(3);
		await vi.advanceTimersByTimeAsync(1);
		expect(urls).toHaveLength(4);

		unwatch();
		await vi.advanceTimersByTimeAsync(idlePollIntervalMs * 2);
		expect(urls).toHaveLength(4);
	});

	it('a hidden tab stops polling; becoming visible checks at once', async () => {
		answer = busy({ activity: 'Integrity', jobType: 'Integrity' });
		const unwatch = activity.watch();
		await vi.advanceTimersByTimeAsync(0);

		setVisibility('hidden');
		await vi.advanceTimersByTimeAsync(idlePollIntervalMs * 2);
		expect(urls).toHaveLength(1);

		setVisibility('visible');
		await vi.advanceTimersByTimeAsync(0);
		expect(urls).toHaveLength(2);
		unwatch();
	});

	it('a failed poll (503) keeps the value and polls on at the idle interval', async () => {
		answer = busy({ activity: 'Saving changes', jobType: null });
		const unwatch = activity.watch();
		await vi.advanceTimersByTimeAsync(0);

		answer = () =>
			new Response(JSON.stringify({ status: 503, code: 'Maintenance' }), { status: 503 });
		await vi.advanceTimersByTimeAsync(pollIntervalMs);
		expect(activity.current).toEqual({ activity: 'Saving changes', jobType: null });

		await vi.advanceTimersByTimeAsync(pollIntervalMs);
		expect(urls).toHaveLength(2);
		await vi.advanceTimersByTimeAsync(idlePollIntervalMs);
		expect(urls).toHaveLength(3);
		unwatch();
	});

	it('refresh checks at once, even when nothing watches, without starting a poll', async () => {
		answer = busy({ activity: 'Saving changes', jobType: null });
		await activity.refresh();
		expect(activity.current?.activity).toBe('Saving changes');

		await vi.advanceTimersByTimeAsync(idlePollIntervalMs * 2);
		expect(urls).toHaveLength(1);
	});
});
