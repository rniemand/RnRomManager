import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { pollIntervalMs } from '#lib/stores/jobs.svelte.js';
import { reviewJobs } from '#lib/stores/reviewJobs.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

const app = vi.hoisted(() => ({ goto: vi.fn(), id: '7' }));
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => app.goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		get params() {
			return { id: app.id };
		},
		get url() {
			return new URL(`http://localhost/review/${app.id}`);
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: ReviewPage } = await import('./[id]/+page.svelte');

const detail = (id: number) => ({
	id,
	system: 'gb',
	systemName: 'Game Boy',
	reason: 'NoDatMatch',
	sourcePath: `/app/ingest/gb/item${id}.gb`,
	archivePath: null,
	archiveEntry: null,
	originalFileName: `item${id}.gb`,
	sizeBytes: 3,
	crc32: '00000000',
	md5: '0'.repeat(32),
	sha1: '0'.repeat(40),
	header: null,
	suggestions: [],
	datIdentification: null,
	candidateGames: [],
	status: 'Pending',
	gameId: null,
	createdAt: '2026-11-13T12:00:00Z',
	lastSeenJobId: null,
	resolvedAt: null,
	resolvedBy: null,
	resolvable: true,
	dismissible: true
});

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

describe('review item page: resolving as a job', () => {
	let jobAnswer: () => Response;

	beforeEach(() => {
		vi.useFakeTimers();
		resetClient();
		app.goto.mockClear();
		app.id = '7';
		toasts.items = [];
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		jobAnswer = () =>
			json({ id: 'j1', type: 'ReviewResolve', status: 'Running', summary: null, error: null });
		vi.stubGlobal('fetch', (input: string, init?: RequestInit) => {
			if (input === '/api/review/7/resolve' && init?.method === 'POST')
				return Promise.resolve(json({ jobId: 'j1' }, 202));
			if (input === '/api/auth/antiforgery')
				return Promise.resolve(new Response(null, { status: 204 }));
			if (input === '/api/jobs/current')
				return Promise.resolve(new Response(null, { status: 204 }));
			if (input === '/api/jobs/j1') return Promise.resolve(jobAnswer());
			const match = /^\/api\/review\/(\d+)$/.exec(input);
			if (match) return Promise.resolve(json(detail(Number(match[1]))));
			return Promise.resolve(new Response(null, { status: 404 }));
		});
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
	});

	async function resolveManually(title = 'My Game'): Promise<void> {
		render(ReviewPage);
		await vi.advanceTimersByTimeAsync(0);
		await fireEvent.input(screen.getByLabelText('Title'), { target: { value: title } });
		await fireEvent.click(screen.getByRole('button', { name: 'Add manually' }));
		await vi.advanceTimersByTimeAsync(0);
	}

	/** Lets the job end with `summary` at the next poll. */
	async function finish(summary: object | null, status = 'Completed'): Promise<void> {
		jobAnswer = () =>
			json({ id: 'j1', type: 'ReviewResolve', status, summary, error: summary ? null : 'boom' });
		await vi.advanceTimersByTimeAsync(pollIntervalMs);
	}

	it('shows progress and disables every action, also on another item page', async () => {
		await resolveManually();

		expect(screen.getByText('Resolving item7.gb…')).toBeTruthy();
		expect(screen.getByRole('button', { name: /Delete/ }).hasAttribute('disabled')).toBe(true);
		expect(screen.getByRole('button', { name: 'Add manually' }).hasAttribute('disabled')).toBe(
			true
		);

		document.body.innerHTML = '';
		app.id = '8';
		render(ReviewPage);
		await vi.advanceTimersByTimeAsync(0);
		expect(screen.getByText('Resolving item7.gb…')).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Dismiss' }).hasAttribute('disabled')).toBe(true);

		await finish({ reviewId: 7, fileName: 'item7.gb', gameId: 'g1', code: null, errors: null });
		expect(screen.queryByText('Resolving item7.gb…')).toBeNull();
		expect(screen.getByRole('button', { name: 'Dismiss' }).hasAttribute('disabled')).toBe(false);
		expect(app.goto).not.toHaveBeenCalled();
	});

	it('opens the game when the job succeeds on the item page', async () => {
		await resolveManually();
		await finish({ reviewId: 7, fileName: 'item7.gb', gameId: 'g1', code: null, errors: null });

		expect(app.goto).toHaveBeenCalledWith('/games/g1');
		expect(toasts.items.map((t) => t.text)).toEqual(['Added as My Game.']);
	});

	it("shows a failed summary's field errors on the form", async () => {
		await resolveManually();
		await finish({
			reviewId: 7,
			fileName: 'item7.gb',
			gameId: null,
			code: 'ValidationFailed',
			errors: { title: ['The generated ROM filename must not contain ...'] }
		});

		expect(
			screen.getAllByText('The generated ROM filename must not contain ...').length
		).toBeGreaterThan(0);
		expect(screen.getByLabelText('Title').classList.contains('is-invalid')).toBe(true);
		expect(reviewJobs.running).toBeNull();
	});

	it('hides Dismiss when the item is not dismissible (B15)', async () => {
		const plain = detail(7);
		vi.stubGlobal('fetch', () => Promise.resolve(json({ ...plain, dismissible: false })));
		render(ReviewPage);
		await vi.advanceTimersByTimeAsync(0);

		expect(screen.getByRole('button', { name: /Delete/ })).toBeTruthy();
		expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();
	});

	it('SourceMissing toasts and returns to the queue', async () => {
		await resolveManually();
		await finish({
			reviewId: 7,
			fileName: 'item7.gb',
			gameId: null,
			code: 'SourceMissing',
			errors: null
		});

		expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([
			['The source file is gone, so the item was removed.', 'warning']
		]);
		expect(app.goto).toHaveBeenCalledWith('/review');
	});
});
