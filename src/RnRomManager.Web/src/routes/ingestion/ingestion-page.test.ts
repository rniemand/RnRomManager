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
	let bodies: Record<string, unknown[]>;

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
		bodies = {};
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
			(bodies[key] ??= []).push(
				typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body
			);
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
		await fireEvent.click(screen.getByRole('button', { name: /Import all/ }));

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
		expect(screen.queryByRole('button', { name: /Import all/ })).toBeNull();
	});

	it('409 DatsMissing from Start switches to the empty state', async () => {
		answers['POST /api/jobs/ingest'] = [{ status: 409, body: { code: 'DatsMissing', title: 'x' } }];
		render(IngestionPage);

		await fireEvent.click(await screen.findByRole('button', { name: /Import all/ }));

		expect(await screen.findByRole('link', { name: 'Go to Admin → DATs' })).toBeTruthy();
	});

	describe('file selection (IN2)', () => {
		const files = (names: string[]) =>
			names.map((name) => ({ name, sizeBytes: 1, modifiedAt: '' }));
		beforeEach(() => {
			fixed['GET /api/ingest/pending'] = {
				status: 200,
				body: [
					{ system: 'gb', files: files(['a.gb', 'b.gb']) },
					{ system: 'gbc', files: files(['c.gbc']) }
				]
			};
		});
		const box = (name: string) => screen.getByRole('checkbox', { name }) as HTMLInputElement;
		const importSelected = () =>
			screen.getByRole('button', { name: /Import selected/ }) as HTMLButtonElement;

		it('checks every row by default; Import selected is disabled at all and at none', async () => {
			render(IngestionPage);
			await screen.findByText('a.gb');

			expect(['Select a.gb', 'Select b.gb', 'Select c.gbc'].map((n) => box(n).checked)).toEqual([
				true,
				true,
				true
			]);
			expect(importSelected().textContent).toContain('Import selected (3)');
			expect(importSelected().disabled).toBe(true);

			await fireEvent.click(screen.getByRole('button', { name: 'Clear selection' }));
			expect(importSelected().textContent).toContain('(0)');
			expect(importSelected().disabled).toBe(true);
			await fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
			expect(box('Select b.gb').checked).toBe(true);
		});

		it('the system header checkbox toggles its rows and shows indeterminate when mixed', async () => {
			render(IngestionPage);
			await screen.findByText('a.gb');
			const header = box('Select all gb files');

			await fireEvent.click(box('Select a.gb'));
			expect(header.indeterminate).toBe(true);
			expect(header.checked).toBe(false);

			await fireEvent.click(header);
			expect([box('Select a.gb').checked, box('Select b.gb').checked]).toEqual([true, true]);
			expect(header.indeterminate).toBe(false);

			await fireEvent.click(header);
			expect([box('Select a.gb').checked, box('Select b.gb').checked]).toEqual([false, false]);
			expect(box('Select c.gbc').checked).toBe(true);
		});

		it('Import selected posts exactly the checked files; Import all posts no body', async () => {
			render(IngestionPage);
			await screen.findByText('a.gb');

			await fireEvent.click(box('Select b.gb'));
			await fireEvent.click(importSelected());
			await vi.waitFor(() => expect(calls).toContain('POST /api/jobs/ingest'));
			expect(bodies['POST /api/jobs/ingest']).toEqual([
				{
					files: [
						{ system: 'gb', name: 'a.gb' },
						{ system: 'gbc', name: 'c.gbc' }
					]
				}
			]);

			await fireEvent.click(screen.getByRole('button', { name: /Import all/ }));
			await vi.waitFor(() => expect(bodies['POST /api/jobs/ingest']).toHaveLength(2));
			expect(bodies['POST /api/jobs/ingest'][1]).toBeUndefined();
		});

		it('the selection survives paging; a reload re-checks everything', async () => {
			fixed['GET /api/ingest/pending'] = {
				status: 200,
				body: [
					{
						system: 'gb',
						files: files(Array.from({ length: 25 }, (_, i) => `f${String(i).padStart(2, '0')}.gb`))
					}
				]
			};
			const ended: Parameters<typeof jobs.onEnded>[0][] = [];
			const onEnded = vi.spyOn(jobs, 'onEnded').mockImplementation((fn) => {
				ended.push(fn);
				return () => {};
			});
			render(IngestionPage);
			await screen.findByText('f00.gb');

			await fireEvent.click(box('Select f00.gb'));
			const nav = screen.getByRole('navigation', { name: 'Pages of gb files' });
			await fireEvent.click(within(nav).getByRole('button', { name: 'Next' }));
			await fireEvent.click(within(nav).getByRole('button', { name: 'Previous' }));
			expect(box('Select f00.gb').checked).toBe(false);
			expect(importSelected().textContent).toContain('(24)');

			ended.forEach((fn) => fn(running as never));
			await vi.waitFor(() => expect(box('Select f00.gb').checked).toBe(true));
			onEnded.mockRestore();
		});
	});

	it('a User sees Not allowed and nothing is fetched', async () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		render(IngestionPage);

		expect(await screen.findByText(/Not allowed/i)).toBeTruthy();
		expect(calls).toEqual([]);
	});
});
