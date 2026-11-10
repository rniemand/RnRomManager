import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

const goto = vi.fn((...args: [string, unknown?]) => navigateTo(args[0]));
vi.mock('$app/navigation', () => ({ goto: (...args: [string, unknown?]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		get url() {
			return testUrl.current;
		}
	}
}));

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
// use:tooltip builds a Bootstrap Tooltip the Modal-only mock above lacks; record the text instead.
const tooltips: { node: Element; text: string }[] = [];
vi.mock('#lib/tooltip.js', () => ({
	tooltip: (node: Element, text: string) => {
		tooltips.push({ node, text });
	}
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
		navigateTo('/ingestion');
		goto.mockClear();
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
		toasts.items = [];
		fixed = {
			'GET /api/ingest/failed': { status: 200, body: [{ system: 'gb', files: [] }] },
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

	const tabs = () =>
		screen.getAllByRole('tab').map((t) => [t.textContent!.trim(), t.getAttribute('aria-selected')]);
	const panels = () =>
		['pending', 'failed', 'runs'].map((id) => document.getElementById(`${id}-panel`)!.hidden);

	it('Pending files is the default tab; labels count files across systems; Run stays outside the tabs (I1)', async () => {
		fixed['GET /api/ingest/failed'] = {
			status: 200,
			body: [
				{ system: 'gb', files: [{ name: 'bad.gb', sizeBytes: 1, modifiedAt: '', reason: null }] },
				{ system: 'gbc', files: [{ name: 'x.gbc', sizeBytes: 1, modifiedAt: '', reason: null }] }
			]
		};
		render(IngestionPage);
		await screen.findByText('Tetris.gb');

		expect(tabs()).toEqual([
			['Pending files (1)', 'true'],
			['Failed files (2)', 'false'],
			['Last runs', 'false']
		]);
		expect(panels()).toEqual([false, true, true]);
		const run = document.querySelector('section[aria-labelledby="run-heading"]')!;
		expect(run.closest('[role="tabpanel"]')).toBeNull();
		expect(screen.getByRole('heading', { name: 'Run' })).toBeTruthy();
		expect(document.querySelector('#pending-panel h2')!.className).toContain('visually-hidden');
	});

	for (const [query, selected] of [
		['?tab=failed', 1],
		['?tab=runs', 2],
		['?tab=bogus', 0]
	] as const) {
		it(`${query} opens tab ${selected}, Run still visible (I2)`, async () => {
			navigateTo(`/ingestion${query}`);
			render(IngestionPage);
			await screen.findByRole('tab', { name: 'Last runs' });

			expect(tabs().map(([, s]) => s)).toEqual([0, 1, 2].map((i) => String(i === selected)));
			expect(panels()).toEqual([0, 1, 2].map((i) => i !== selected));
			expect(screen.getByRole('button', { name: /Import all/ })).toBeTruthy();
		});
	}

	it('switching tabs updates the URL without reloading, and keeps selections (I2, I3)', async () => {
		render(IngestionPage);
		const checkbox = (await screen.findByRole('checkbox', {
			name: 'Select Tetris.gb'
		})) as HTMLInputElement;
		await fireEvent.click(checkbox);
		expect(checkbox.checked).toBe(false);
		const before = calls.length;

		await fireEvent.click(screen.getByRole('tab', { name: 'Failed files (0)' }));
		expect(goto).toHaveBeenLastCalledWith('/ingestion?tab=failed', { replace: true, reset: false });
		await vi.waitFor(() => expect(panels()).toEqual([true, false, true]));
		await fireEvent.click(screen.getByRole('tab', { name: 'Last runs' }));
		expect(goto).toHaveBeenLastCalledWith('/ingestion?tab=runs', { replace: true, reset: false });
		await fireEvent.click(screen.getByRole('tab', { name: 'Pending files (1)' }));
		expect(goto).toHaveBeenLastCalledWith('/ingestion', { replace: true, reset: false });
		await vi.waitFor(() => expect(panels()).toEqual([false, true, true]));

		expect(calls.slice(before)).toEqual([]);
		expect(
			(screen.getByRole('checkbox', { name: 'Select Tetris.gb' }) as HTMLInputElement).checked
		).toBe(false);
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
		const rows = () => document.querySelectorAll('#pending-panel tbody tr');
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
		navigateTo('/ingestion?tab=runs');
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

	describe('delete (D3–D6)', () => {
		const files = (names: string[]) =>
			names.map((name) => ({ name, sizeBytes: 1, modifiedAt: '' }));
		const section = (id: string) => document.getElementById(`${id}-panel`) as HTMLElement;
		const deleteBodies = () => bodies['POST /api/ingest/delete'] ?? [];
		const confirmButton = () => screen.getByRole('button', { name: 'Delete', hidden: true });
		const modalTitle = () => document.querySelector('.modal-title')!.textContent;

		beforeEach(() => {
			fixed['GET /api/ingest/pending'] = {
				status: 200,
				body: [
					{ system: 'gb', files: files(['a.gb', 'pack.zip']) },
					{ system: 'gbc', files: files(['c.gbc']) }
				]
			};
			fixed['GET /api/ingest/failed'] = {
				status: 200,
				body: [
					{
						system: 'gb',
						files: [
							{ name: 'bad.gb', sizeBytes: 1, modifiedAt: '', reason: 'InvalidHeader' },
							{ name: 'odd.gb', sizeBytes: 1, modifiedAt: '', reason: null }
						]
					},
					{ system: 'gbc', files: [] }
				]
			};
			fixed['POST /api/ingest/delete'] = { status: 200, body: { deleted: 1, failed: [] } };
		});

		it('a row delete opens the modal and posts one entry with its folder on confirm', async () => {
			render(IngestionPage);
			const button = await screen.findByRole('button', { name: 'Delete a.gb' });
			expect(tooltips.find((t) => t.node === button)?.text).toBe(
				'Delete (Shift+click to skip confirmation)'
			);

			await fireEvent.click(button);
			expect(modalTitle()).toBe('Delete a.gb');
			expect(deleteBodies()).toEqual([]);
			await fireEvent.click(confirmButton());

			await vi.waitFor(() =>
				expect(deleteBodies()).toEqual([
					{ files: [{ system: 'gb', folder: 'pending', name: 'a.gb' }] }
				])
			);
			await vi.waitFor(() =>
				expect(calls.filter((c) => c === 'GET /api/ingest/failed')).toHaveLength(2)
			);
			expect(toasts.items.map((t) => t.text)).toContain('Deleted a.gb.');
		});

		it('Shift+click on a row posts without the modal; a failed row uses folder failed', async () => {
			navigateTo('/ingestion?tab=failed');
			render(IngestionPage);
			await fireEvent.click(await screen.findByRole('button', { name: 'Delete bad.gb' }), {
				shiftKey: true
			});
			await vi.waitFor(() =>
				expect(deleteBodies()).toEqual([
					{ files: [{ system: 'gb', folder: 'failed', name: 'bad.gb' }] }
				])
			);
		});

		it('Shift+click on an archive row still opens the modal', async () => {
			render(IngestionPage);
			await fireEvent.click(await screen.findByRole('button', { name: 'Delete pack.zip' }), {
				shiftKey: true
			});
			expect(modalTitle()).toBe('Delete pack.zip');
			expect(deleteBodies()).toEqual([]);
		});

		it('Delete selected with Shift still opens the modal, listing system/name entries', async () => {
			render(IngestionPage);
			await screen.findByText('a.gb');
			const button = within(section('pending')).getByRole('button', {
				name: 'Delete selected (3)'
			});

			await fireEvent.click(button, { shiftKey: true });

			expect(modalTitle()).toBe('Delete 3 files');
			const items = [...document.querySelectorAll('.modal-body li')].map((li) => li.textContent);
			expect(items).toEqual(['gb/a.gb', 'gb/pack.zip', 'gbc/c.gbc']);
			expect(document.querySelector('.modal-body')!.textContent).toContain(
				'pending Review items are deleted too'
			);
			expect(deleteBodies()).toEqual([]);
		});

		it('the modal lists at most 10 entries and "and n more"', async () => {
			fixed['GET /api/ingest/pending'] = {
				status: 200,
				body: [{ system: 'gb', files: files(Array.from({ length: 13 }, (_, i) => `f${i}.gb`)) }]
			};
			render(IngestionPage);
			await screen.findByText('f0.gb');
			await fireEvent.click(
				within(section('pending')).getByRole('button', { name: 'Delete selected (13)' })
			);

			const items = [...document.querySelectorAll('.modal-body li')].map((li) => li.textContent);
			expect(items).toHaveLength(11);
			expect(items[10]).toBe('and 3 more');
		});

		it('the failed section lists reasons, starts unselected and deletes its selection', async () => {
			navigateTo('/ingestion?tab=failed');
			render(IngestionPage);
			await screen.findByText('bad.gb');
			const failed = section('failed');

			expect(within(failed).getByText('InvalidHeader').tagName).toBe('CODE');
			expect(within(failed).getByText('—')).toBeTruthy();
			const deleteSelected = within(failed).getByRole('button', {
				name: 'Delete selected (0)'
			}) as HTMLButtonElement;
			expect(deleteSelected.disabled).toBe(true);
			expect(
				(within(failed).getByRole('checkbox', { name: 'Select bad.gb' }) as HTMLInputElement)
					.checked
			).toBe(false);

			await fireEvent.click(within(failed).getByRole('button', { name: 'Select all' }));
			await fireEvent.click(within(failed).getByRole('button', { name: 'Delete selected (2)' }));
			expect(document.querySelector('.modal-body')!.textContent).not.toContain('Review');
			await fireEvent.click(confirmButton());

			await vi.waitFor(() =>
				expect(deleteBodies()).toEqual([
					{
						files: [
							{ system: 'gb', folder: 'failed', name: 'bad.gb' },
							{ system: 'gb', folder: 'failed', name: 'odd.gb' }
						]
					}
				])
			);
		});

		it('no failed files shows the empty state', async () => {
			fixed['GET /api/ingest/failed'] = { status: 200, body: [{ system: 'gb', files: [] }] };
			render(IngestionPage);
			expect(await screen.findByText('No failed imports.')).toBeTruthy();
		});

		it('every delete button is disabled while a job runs', async () => {
			fixed['GET /api/jobs/current'] = { status: 200, body: running };
			jobs.current = running as never;
			render(IngestionPage);
			await screen.findByText('a.gb');

			// Both panels, the hidden Failed one included.
			const deletes = [
				...document.querySelectorAll('#pending-panel button, #failed-panel button')
			].filter((b) => /^Delete/.test(b.getAttribute('aria-label') ?? b.textContent!.trim()));
			expect(deletes.length).toBeGreaterThan(4);
			expect(deletes.every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
		});

		it('JobRunning from a delete shows the job-running toast', async () => {
			answers['POST /api/ingest/delete'] = [
				{ status: 409, body: { code: 'JobRunning', title: 'x' } }
			];
			render(IngestionPage);
			await fireEvent.click(await screen.findByRole('button', { name: 'Delete a.gb' }), {
				shiftKey: true
			});
			await vi.waitFor(() => expect(toasts.items.map((t) => t.kind)).toEqual(['warning']));
		});
	});

	it('a User sees Not allowed and nothing is fetched', async () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		render(IngestionPage);

		expect(await screen.findByText(/Not allowed/i)).toBeTruthy();
		expect(calls).toEqual([]);
	});
});
