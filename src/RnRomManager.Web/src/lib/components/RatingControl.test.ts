import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import { toasts } from '../stores/toasts.svelte';
import RatingControl from './RatingControl.svelte';

type Answer = { status: number; body?: unknown; hold?: boolean };
const jobRunning: Answer = { status: 409, body: { code: 'JobRunning', title: 'JobRunning' } };
const detail = (rating: number | null, favourite: boolean) => ({ id: 'g1', rating, favourite });

describe('RatingControl', () => {
	let answers: Answer[];
	let bodies: unknown[];
	let release: (() => void) | null;

	beforeEach(() => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		resetClient();
		toasts.items = [];
		answers = [];
		bodies = [];
		release = null;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (init?.method !== 'PATCH') return Promise.resolve(new Response(null, { status: 204 }));
			expect(url).toBe('/api/games/g1/rating');
			bodies.push(JSON.parse(String(init.body)));
			const answer = answers.shift() ?? { status: 200, body: detail(null, false) };
			const response = () =>
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				});
			if (answer.hold) return new Promise<Response>((r) => (release = () => r(response())));
			return Promise.resolve(response());
		});
	});
	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
	});

	const props = (rating: number | null = null, favourite = false, extra = {}) => ({
		gameId: 'g1',
		title: 'Tetris',
		rating,
		favourite,
		...extra
	});
	const star = (n: number) => screen.getByRole('button', { name: `Rate ${n} of 5` });
	const heart = () => screen.getByRole('button', { name: 'Favourite' });
	const pressed = (el: HTMLElement) => el.getAttribute('aria-pressed') === 'true';

	it('clicking a star sends it and shows it at once; onsaved gets the detail', async () => {
		const onsaved = vi.fn();
		answers.push({ status: 200, body: detail(3, false) });
		render(RatingControl, props(null, false, { onsaved }));

		await fireEvent.click(star(3));

		expect(pressed(star(3))).toBe(true);
		await vi.waitFor(() => expect(bodies).toEqual([{ rating: 3 }]));
		await vi.waitFor(() => expect(onsaved).toHaveBeenCalledWith(detail(3, false)));
	});

	it('clicking the current star clears the rating', async () => {
		render(RatingControl, props(4));

		await fireEvent.click(star(4));

		await vi.waitFor(() => expect(bodies).toEqual([{ rating: null }]));
		expect(pressed(star(4))).toBe(false);
	});

	it('the heart toggles favourite', async () => {
		answers.push({ status: 200, body: detail(null, true) });
		render(RatingControl, props());

		await fireEvent.click(heart());

		await vi.waitFor(() => expect(bodies).toEqual([{ favourite: true }]));
		expect(pressed(heart())).toBe(true);
	});

	it('409 JobRunning is retried once after 1 s and keeps the value', async () => {
		answers.push(jobRunning, { status: 200, body: detail(5, false) });
		render(RatingControl, props());

		await fireEvent.click(star(5));
		await vi.advanceTimersByTimeAsync(1000);

		await vi.waitFor(() => expect(bodies).toHaveLength(2));
		await vi.waitFor(() => expect(star(5).hasAttribute('disabled')).toBe(false));
		expect(pressed(star(5))).toBe(true);
		expect(toasts.items).toEqual([]);
	});

	it('409 twice → the job toast and a revert', async () => {
		answers.push(jobRunning, jobRunning);
		render(RatingControl, props(2));

		await fireEvent.click(star(5));
		await vi.advanceTimersByTimeAsync(1000);

		await vi.waitFor(() => expect(pressed(star(2))).toBe(true));
		expect(bodies).toHaveLength(2);
		expect(toasts.items.map((t) => t.kind)).toEqual(['warning']);
		expect(toasts.items[0].text).toContain('Another job is running');
	});

	it('another error reverts with an error toast and no retry', async () => {
		answers.push({ status: 500, body: { code: 'IoError', title: 'Disk full' } });
		render(RatingControl, props(null, true));

		await fireEvent.click(heart());

		await vi.waitFor(() => expect(pressed(heart())).toBe(true));
		expect(bodies).toHaveLength(1);
		expect(toasts.items).toMatchObject([{ text: 'Disk full', kind: 'danger' }]);
	});

	it('the whole control is disabled while a save is pending', async () => {
		answers.push({ status: 200, body: detail(1, false), hold: true });
		render(RatingControl, props());

		await fireEvent.click(star(1));
		await vi.waitFor(() => expect(release).not.toBeNull());
		await fireEvent.click(heart());

		expect(bodies).toHaveLength(1);
		for (const n of [1, 2, 3, 4, 5]) expect(star(n).hasAttribute('disabled')).toBe(true);
		expect(heart().hasAttribute('disabled')).toBe(true);
		release!();
		await vi.waitFor(() => expect(heart().hasAttribute('disabled')).toBe(false));
	});

	it('read-only shows one accessible text and no buttons', () => {
		render(RatingControl, props(3, true, { readonly: true }));

		expect(screen.queryAllByRole('button')).toEqual([]);
		expect(screen.getByText('Rated 3 of 5')).toBeTruthy();
		expect(screen.getByText('Favourite')).toBeTruthy();
	});

	it('read-only renders nothing when unrated and not a favourite', () => {
		const { container } = render(RatingControl, props(null, false, { readonly: true }));

		expect(container.textContent?.trim()).toBe('');
		expect(container.querySelector('i')).toBeNull();
	});
});
