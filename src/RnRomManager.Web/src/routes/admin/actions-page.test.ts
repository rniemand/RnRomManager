import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { AdminActions } from '#lib/api/types.js';
import { formatDateTime } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { default: ActionsPage } = await import('./actions/+page.svelte');

const counts = (changes: Partial<AdminActions> = {}): AdminActions => ({
	igdbConfigured: true,
	igdbLinked: 1200,
	coversPending: 34,
	lastIgdbSyncAt: '2026-11-20T10:00:00Z',
	...changes
});

type Answer = { status: number; body?: unknown };

describe('Admin Actions tab (T1–T4)', () => {
	let calls: string[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		jobs.current = null;
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		answers = { 'GET /api/admin/actions': { status: 200, body: counts() } };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	const button = (name: string) => screen.getByRole('button', { name });

	it('shows both cards with their counts', async () => {
		render(ActionsPage);

		expect((await screen.findByTestId('covers-count')).textContent!.trim()).toBe(
			'34 games to process'
		);
		expect(screen.getByTestId('ratings-count').textContent!.trim()).toBe(
			`1,200 linked games · last refreshed ${formatDateTime('2026-11-20T10:00:00Z')}`
		);
		expect(screen.getByRole('heading', { name: 'Pull Cover Images' })).toBeTruthy();
		expect(screen.getByRole('heading', { name: 'Refresh IGDB Ratings' })).toBeTruthy();
		expect(button('Pull Cover Images').className).toBe('btn btn-primary');
		expect(screen.queryByRole('status')).toBeNull();
	});

	it('singular counts, and never refreshed', async () => {
		answers['GET /api/admin/actions'] = {
			status: 200,
			body: counts({ coversPending: 1, igdbLinked: 1, lastIgdbSyncAt: null })
		};
		render(ActionsPage);

		expect((await screen.findByTestId('covers-count')).textContent!.trim()).toBe(
			'1 game to process'
		);
		expect(screen.getByTestId('ratings-count').textContent!.trim()).toBe(
			'1 linked game · never refreshed'
		);
	});

	it.each([
		[1, 'This replaces the cover of 1 game with its IGDB cover, including covers you uploaded.'],
		[
			34,
			'This replaces the cover of 34 games with their IGDB cover, including covers you uploaded.'
		]
	])('Pull Cover Images asks first (%i), then starts and toasts a link', async (pending, text) => {
		answers['GET /api/admin/actions'] = { status: 200, body: counts({ coversPending: pending }) };
		answers['POST /api/jobs/pull-covers'] = { status: 202, body: { jobId: 'j7' } };
		render(ActionsPage);
		await screen.findByTestId('covers-count');

		await fireEvent.click(button('Pull Cover Images'));
		expect(screen.getByText(text)).toBeTruthy();
		expect(calls).not.toContain('POST /api/jobs/pull-covers');
		const confirm = screen
			.getAllByRole('button', { name: 'Pull Cover Images', hidden: true })
			.find((b) => b.closest('.modal-footer'))!;
		await fireEvent.click(confirm);

		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({
				text: 'Pull Cover Images started',
				kind: 'success',
				link: { href: '/jobs/j7', text: 'View job' }
			})
		);
		expect(calls).toContain('GET /api/jobs/current');
	});

	it('Refresh IGDB Ratings starts directly', async () => {
		answers['POST /api/jobs/igdb-sync'] = { status: 202, body: { jobId: 'j8' } };
		render(ActionsPage);
		await screen.findByTestId('ratings-count');

		await fireEvent.click(button('Refresh IGDB Ratings'));

		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({
				text: 'Refresh IGDB Ratings started',
				link: { href: '/jobs/j8' }
			})
		);
	});

	it('not configured: an info alert, both buttons disabled', async () => {
		answers['GET /api/admin/actions'] = { status: 200, body: counts({ igdbConfigured: false }) };
		render(ActionsPage);

		expect((await screen.findByText(/^Set Sources/)).textContent!.trim()).toBe(
			'Set Sources:IgdbClientId and Sources:IgdbClientSecret to use IGDB actions.'
		);
		expect((button('Pull Cover Images') as HTMLButtonElement).disabled).toBe(true);
		expect((button('Refresh IGDB Ratings') as HTMLButtonElement).disabled).toBe(true);
	});

	it('a running job disables both buttons', async () => {
		answers['GET /api/jobs/current'] = {
			status: 200,
			body: {
				id: 'j1',
				type: 'Integrity',
				mode: 'Quick',
				status: 'Running',
				current: 0,
				total: 1,
				currentItem: null,
				startedAt: '2026-11-18T10:00:00Z'
			}
		};
		render(ActionsPage);
		await screen.findByTestId('covers-count');

		await vi.waitFor(() =>
			expect((button('Pull Cover Images') as HTMLButtonElement).disabled).toBe(true)
		);
		expect((button('Refresh IGDB Ratings') as HTMLButtonElement).disabled).toBe(true);
	});

	it('409 JobRunning uses the job-running toast; other errors toast their title', async () => {
		answers['POST /api/jobs/igdb-sync'] = {
			status: 409,
			body: { status: 409, code: 'JobRunning', title: 'Another job is running.' }
		};
		const jobRunning = vi.spyOn(toasts, 'jobRunning');
		render(ActionsPage);
		await screen.findByTestId('ratings-count');
		await fireEvent.click(button('Refresh IGDB Ratings'));
		await vi.waitFor(() => expect(jobRunning).toHaveBeenCalledOnce());

		answers['POST /api/jobs/igdb-sync'] = {
			status: 409,
			body: {
				status: 409,
				code: 'IgdbNotConfigured',
				title: 'Refreshing ratings needs Sources:IgdbClientId and Sources:IgdbClientSecret.'
			}
		};
		await vi.waitFor(() =>
			expect((button('Refresh IGDB Ratings') as HTMLButtonElement).disabled).toBe(false)
		);
		await fireEvent.click(button('Refresh IGDB Ratings'));

		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({
				text: 'Refreshing ratings needs Sources:IgdbClientId and Sources:IgdbClientSecret.',
				kind: 'danger'
			})
		);
		jobRunning.mockRestore();
	});
});
