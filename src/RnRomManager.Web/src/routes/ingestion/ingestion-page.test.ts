import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { default: IngestionPage } = await import('./+page.svelte');

type Answer = { status: number; body?: unknown };

const running = {
	id: 'j1',
	type: 'Ingest',
	mode: null,
	status: 'Running',
	current: 1,
	total: 4,
	currentItem: 'Tetris.gb',
	startedAt: '2026-11-05T10:00:00Z'
};

describe('ingestion page', () => {
	let calls: string[];
	let answers: Record<string, Answer[]>;
	let fixed: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		jobs.current = null;
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin', 'rom-manager'],
			mustChangePassword: false
		});
		calls = [];
		answers = {};
		fixed = {
			'GET /api/ingest/pending': {
				status: 200,
				body: [
					{
						system: 'gb',
						files: [{ name: 'Tetris.gb', sizeBytes: 32768, modifiedAt: '2026-11-05T09:00:00Z' }]
					},
					{ system: 'gbc', files: [] }
				]
			},
			'GET /api/jobs?type=Ingest&page=1&pageSize=20': {
				status: 200,
				body: { items: [], page: 1, pageSize: 20, total: 0 }
			},
			'GET /api/admin/dats': {
				status: 200,
				body: [{ datName: 'gb', version: '1', downloadedAt: '' }]
			}
		};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			const answer = answers[key]?.shift() ?? fixed[key] ?? { status: 204 };
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

	it('lists pending files and starts a run, then shows its progress', async () => {
		render(IngestionPage);
		expect(await screen.findByText('32.0 KiB')).toBeTruthy();

		answers['GET /api/jobs/current'] = [{ status: 200, body: running }];
		await fireEvent.click(screen.getByRole('button', { name: /Start ingestion/ }));

		expect(await screen.findByText('1 / 4 · Tetris.gb')).toBeTruthy();
		expect(calls).toContain('POST /api/jobs/ingest');
		expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('25');
	});

	it('pages 45 pending files as 20/20/5 in a standard table', async () => {
		fixed['GET /api/ingest/pending'] = {
			status: 200,
			body: [
				{
					system: 'gb',
					files: Array.from({ length: 45 }, (_, i) => ({
						name: `f${String(i).padStart(2, '0')}.gb`,
						sizeBytes: 1,
						modifiedAt: ''
					}))
				}
			]
		};
		render(IngestionPage);

		await screen.findByText('f00.gb');
		const rows = () =>
			document.querySelectorAll('section[aria-labelledby="pending-heading"] tbody tr');
		expect(document.querySelector('table')!.className).toBe(
			'table table-sm table-striped table-hover align-middle mb-0'
		);
		expect(rows()).toHaveLength(20);
		expect(screen.getByText('Page 1 of 3')).toBeTruthy();
		const nav = screen.getByRole('navigation', { name: 'Pages of gb files' });
		await fireEvent.click(within(nav).getByRole('button', { name: 'Next' }));
		expect(rows()).toHaveLength(20);
		expect(screen.getByText('f20.gb')).toBeTruthy();
		await fireEvent.click(within(nav).getByRole('button', { name: 'Next' }));
		expect(rows()).toHaveLength(5);
		expect(screen.getByText('f44.gb')).toBeTruthy();
	});

	it('Last runs are paged by the server, 20 per page', async () => {
		const run = (id: string) => ({
			id,
			type: 'Ingest',
			mode: null,
			triggerSource: 'Manual',
			status: 'Completed',
			startedAt: '2026-11-05T10:00:00Z',
			summary: null
		});
		fixed['GET /api/jobs?type=Ingest&page=1&pageSize=20'] = {
			status: 200,
			body: { items: [run('r1')], page: 1, pageSize: 20, total: 25 }
		};
		fixed['GET /api/jobs?type=Ingest&page=2&pageSize=20'] = {
			status: 200,
			body: { items: [run('r21')], page: 2, pageSize: 20, total: 25 }
		};
		render(IngestionPage);

		const nav = await screen.findByRole('navigation', { name: 'Pages of runs' });
		await fireEvent.click(within(nav).getByRole('button', { name: 'Next' }));

		await vi.waitFor(() => expect(calls).toContain('GET /api/jobs?type=Ingest&page=2&pageSize=20'));
		expect(await screen.findByText('Page 2 of 2')).toBeTruthy();
	});

	it('no DATs → empty state with a link to Admin → DATs instead of Start', async () => {
		fixed['GET /api/admin/dats'] = { status: 200, body: [] };
		render(IngestionPage);

		expect(await screen.findByRole('link', { name: 'Go to Admin → DATs' })).toBeTruthy();
		expect(screen.queryByRole('button', { name: /Start ingestion/ })).toBeNull();
	});

	it('409 DatsMissing from Start switches to the empty state', async () => {
		answers['POST /api/jobs/ingest'] = [{ status: 409, body: { code: 'DatsMissing', title: 'x' } }];
		render(IngestionPage);

		await fireEvent.click(await screen.findByRole('button', { name: /Start ingestion/ }));

		expect(await screen.findByRole('link', { name: 'Go to Admin → DATs' })).toBeTruthy();
	});

	it('a User sees Not allowed and nothing is fetched', async () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		render(IngestionPage);

		expect(await screen.findByText(/Not allowed/i)).toBeTruthy();
		expect(calls).toEqual([]);
	});
});
