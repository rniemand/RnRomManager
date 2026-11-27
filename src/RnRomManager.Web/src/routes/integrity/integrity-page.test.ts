import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { expectOnePageTitle } from '#lib/testing/pageTitle.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

// Toggles `show` so a test can tell an open modal from a closed one.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		constructor(private element: Element) {}
		show() {
			this.element.classList.add('show');
		}
		hide() {
			this.element.classList.remove('show');
		}
		dispose() {}
	}
}));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: IntegrityPage } = await import('./+page.svelte');

const run = (id: string, type: string, mode: string | null, startedAt: string) => ({
	id,
	type,
	mode,
	triggerSource: 'Manual',
	status: 'Completed',
	requestedBy: 1,
	startedAt,
	finishedAt: startedAt,
	summary: null,
	error: null
});
const empty = { items: [], page: 1, pageSize: 10, total: 0 };

describe('integrity page', () => {
	let calls: { key: string; body: unknown }[];
	let fixed: Record<string, unknown>;

	beforeEach(() => {
		resetClient();
		jobs.current = null;
		const store = new Map<string, string>();
		vi.stubGlobal('localStorage', {
			getItem: (k: string) => store.get(k) ?? null,
			setItem: (k: string, v: string) => store.set(k, v)
		});
		toasts.items = [];
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin'],
			mustChangePassword: false
		});
		calls = [];
		fixed = {
			'GET /api/integrity/status': {
				lastQuick: run('q1', 'Integrity', 'Quick', '2026-11-04T03:00:00Z'),
				lastDeep: null,
				nextScheduledAt: null
			},
			'GET /api/jobs?type=Integrity&pageSize=10': {
				...empty,
				items: [run('q1', 'Integrity', 'Quick', '2026-11-04T03:00:00Z')]
			},
			'GET /api/jobs?type=MediaRetry&pageSize=10': {
				...empty,
				items: [run('m1', 'MediaRetry', null, '2026-11-05T03:00:00Z')]
			}
		};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body ? JSON.parse(init.body as string) : undefined });
			const body =
				fixed[key] ??
				(url.startsWith('/api/integrity/issues') ? { ...empty, pageSize: 10 } : undefined);
			return Promise.resolve(
				new Response(body === undefined ? null : JSON.stringify(body), {
					status: body === undefined ? 204 : 200
				})
			);
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('hides the storage moves button when no games wait (S4, B19)', async () => {
		fixed['GET /api/jobs/storage-migration'] = { pendingGames: 0 };
		render(IntegrityPage);

		await screen.findByRole('button', { name: 'Run quick' });
		expect(screen.queryByRole('button', { name: /Resume ROM storage moves/ })).toBeNull();
	});

	it('resumes the storage moves while games wait (S4, B19)', async () => {
		fixed['GET /api/jobs/storage-migration'] = { pendingGames: 3 };
		render(IntegrityPage);

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Resume ROM storage moves (3)' })
		);

		expect(calls.map((c) => c.key)).toContain('POST /api/jobs/storage-migration');
	});

	it('shows status, merged history (newest first) and open issues', async () => {
		render(IntegrityPage);

		expect(await screen.findByText('Never')).toBeTruthy();
		expect(screen.getByText('Not scheduled')).toBeTruthy();
		const jobsColumn = screen.getAllByRole('row').map((r) => r.textContent ?? '');
		expect(jobsColumn.findIndex((t) => t.includes('Media Retry'))).toBeLessThan(
			jobsColumn.findIndex((t) => t.includes('Integrity Check (Quick)'))
		);
		expect(calls.map((c) => c.key)).toContain(
			'GET /api/integrity/issues?page=1&pageSize=10&status=Open'
		);
	});

	it('duplicate issues show the plain path and an Open game link; others link the path (IN1)', async () => {
		const issue = (id: number, type: string, zipPath: string) => ({
			id,
			type,
			severity: 'Error',
			systemId: 'gb',
			zipPath,
			gameId: 'g1',
			entryName: null,
			details: 'detail text',
			status: 'Open',
			detectedAt: '2026-11-04T03:00:00Z',
			lastSeenAt: '2026-11-04T03:00:00Z'
		});
		fixed['GET /api/integrity/issues?page=1&pageSize=10&status=Open'] = {
			...empty,
			total: 3,
			items: [
				issue(1, 'DuplicateGameId', 'gb/T/Tetris (2).zip'),
				issue(2, 'DuplicateSha1', 'gb/B/Beta.zip'),
				issue(3, 'ZipUnreadable', 'gb/Z/Zelda.zip')
			]
		};
		render(IntegrityPage);

		const links = await screen.findAllByRole('link', { name: 'Open game' });
		expect(links).toHaveLength(2);
		expect(links.map((a) => [a.getAttribute('href'), a.className])).toEqual([
			['/games/g1', 'ms-2'],
			['/games/g1', 'ms-2']
		]);
		expect(screen.queryByRole('link', { name: 'gb/T/Tetris (2).zip' })).toBeNull();
		expect(links[0].closest('td')!.textContent).toContain('gb/T/Tetris (2).zip');
		expect(screen.getByRole('link', { name: 'gb/Z/Zelda.zip' }).getAttribute('href')).toBe(
			'/games/g1'
		);
	});

	describe('issue table (IN2, IN4, IN5)', () => {
		const openList = 'GET /api/integrity/issues?page=1&pageSize=10&status=Open';
		const issue = (id: number, type: string, status: string, zipPath: string) => ({
			id,
			type,
			severity: 'Warn',
			systemId: null,
			zipPath,
			gameId: null,
			entryName: null,
			details: null,
			status,
			detectedAt: '2026-11-04T03:00:00Z'
		});
		const rows = [
			issue(1, 'OrphanFile', 'Open', 'stray.txt'),
			issue(2, 'OrphanFile', 'Resolved', 'old.txt'),
			issue(3, 'ZipUnreadable', 'AutoRepaired', 'gb/T/Broken.zip')
		];
		const modal = () =>
			screen.getByText(/from the library folder\?/).closest('.modal') as HTMLElement;

		it('Type and Status are badges; the table is small with py-1 cells', async () => {
			fixed[openList] = { ...empty, total: 3, items: rows };
			render(IntegrityPage);

			const type = await screen.findByText('ZipUnreadable', { selector: 'span' });
			expect(type.className).toBe('badge text-bg-light border');
			expect(screen.getByText('Open', { selector: 'span' }).className).toBe(
				'badge text-bg-warning'
			);
			expect(screen.getByText('AutoRepaired', { selector: 'span' }).className).toBe(
				'badge text-bg-info'
			);
			expect(screen.getByText('Resolved', { selector: 'span' }).className).toBe(
				'badge text-bg-success'
			);
			const table = type.closest('table')!;
			expect(table.closest('.small')).toBeTruthy();
			expect(
				[...table.querySelectorAll('tbody td')].every((td) => td.classList.contains('py-1'))
			).toBe(true);
		});

		it('only an open orphan gets a delete button, hidden while a job runs', async () => {
			fixed[openList] = { ...empty, total: 3, items: rows };
			render(IntegrityPage);

			const del = await screen.findByRole('button', { name: 'Delete stray.txt' });
			expect(screen.queryByRole('button', { name: 'Delete old.txt' })).toBeNull();
			expect(screen.queryByRole('button', { name: 'Delete gb/T/Broken.zip' })).toBeNull();
			expect(del.closest('table')!.querySelector('thead th:last-child')!.textContent).toBe(
				'Actions'
			);

			jobs.current = {
				id: 'j1',
				type: 'Ingest',
				mode: null,
				status: 'Running',
				current: 0,
				total: 1,
				currentItem: null,
				startedAt: '2026-11-04T03:00:00Z'
			} as never;
			await vi.waitFor(() =>
				expect(screen.queryByRole('button', { name: 'Delete stray.txt' })).toBeNull()
			);
		});

		it('delete confirms, posts, toasts and reloads the list', async () => {
			fixed[openList] = { ...empty, total: 3, items: rows };
			render(IntegrityPage);
			await fireEvent.click(await screen.findByRole('button', { name: 'Delete stray.txt' }));

			expect(modal().classList.contains('show')).toBe(true);
			expect(modal().querySelector('.modal-title')!.textContent).toBe('Delete orphan file');
			expect(modal().textContent).toContain(
				"Delete stray.txt from the library folder? This can't be undone."
			);
			const before = calls.filter((c) => c.key === openList).length;
			await fireEvent.click(screen.getByRole('button', { name: 'Delete', hidden: true }));

			await vi.waitFor(() =>
				expect(calls.map((c) => c.key)).toContain('POST /api/integrity/issues/1/delete-file')
			);
			await vi.waitFor(() =>
				expect(calls.filter((c) => c.key === openList).length).toBe(before + 1)
			);
			expect(toasts.items.at(-1)).toMatchObject({ kind: 'success', text: 'Deleted stray.txt.' });
		});

		it('the page-size selector defaults to 10, is remembered, and goes back to page 1', async () => {
			fixed[openList] = { ...empty, total: 30, items: rows };
			render(IntegrityPage);
			await screen.findByText('stray.txt');
			const size = screen.getByLabelText(/per page|Page size/i) as HTMLSelectElement;
			expect(size.value).toBe('10');

			await fireEvent.change(size, { target: { value: '50' } });

			await vi.waitFor(() =>
				expect(calls.map((c) => c.key)).toContain(
					'GET /api/integrity/issues?page=1&pageSize=50&status=Open'
				)
			);
			expect(localStorage.getItem('rnrm.pageSize.integrity-issues')).toBe('50');
		});
	});

	it('Run deep starts an integrity job in Deep mode', async () => {
		render(IntegrityPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Run deep' }));

		await vi.waitFor(() =>
			expect(calls).toContainEqual({ key: 'POST /api/jobs/integrity', body: { mode: 'Deep' } })
		);
	});

	it('Issues and History are accordion panels, one open at a time, Issues open (B24)', async () => {
		render(IntegrityPage);
		await screen.findByText('No issues.');

		const issues = screen.getByRole('button', { name: 'Issues' });
		const history = screen.getByRole('button', { name: 'History' });
		expect(issues.getAttribute('aria-expanded')).toBe('true');
		expect(history.getAttribute('aria-expanded')).toBe('false');
		expect(screen.getByRole('heading', { level: 2, name: 'Issues' }).id).toBe('issues-heading');
		expect(screen.getByRole('heading', { level: 2, name: 'History' }).id).toBe('history-heading');

		await fireEvent.click(history);
		expect(history.getAttribute('aria-expanded')).toBe('true');
		expect(issues.getAttribute('aria-expanded')).toBe('false');
		await fireEvent.click(history);
		expect(history.getAttribute('aria-expanded')).toBe('false');
		expect(issues.getAttribute('aria-expanded')).toBe('false');
	});

	it('empty panels use EmptyState (B24)', async () => {
		fixed['GET /api/jobs?type=Integrity&pageSize=10'] = empty;
		fixed['GET /api/jobs?type=MediaRetry&pageSize=10'] = empty;
		render(IntegrityPage);

		expect((await screen.findByText('No issues.')).closest('.text-center.py-5')).not.toBeNull();
		expect(screen.getByText('No checks have run yet.').closest('.text-center.py-5')).not.toBeNull();
	});

	it('history table is in a card (B24)', async () => {
		const { container } = render(IntegrityPage);
		await screen.findByText('Never');

		const table = container.querySelector('#history-panel table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.closest('.table-responsive')?.parentElement?.classList.contains('card')).toBe(
			true
		);
	});

	it('issue filters go into the query and reset the page', async () => {
		render(IntegrityPage);
		await screen.findByText('No issues.');

		await fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'HashMismatch' } });

		await vi.waitFor(() =>
			expect(calls.map((c) => c.key)).toContain(
				'GET /api/integrity/issues?page=1&pageSize=10&status=Open&type=HashMismatch'
			)
		);
	});

	it('shows the status as a key/value table in the house style (B23)', async () => {
		render(IntegrityPage);
		const label = await screen.findByRole('rowheader', { name: 'Last quick check' });

		const table = label.closest('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.closest('.table-responsive')?.parentElement?.classList.contains('card')).toBe(
			true
		);
		expect(
			[...table.querySelectorAll('th[scope="row"]')].map((th) => th.textContent?.trim())
		).toEqual(['Last quick check', 'Last deep check', 'Next scheduled check']);
		expect(document.querySelector('dl')).toBeNull();
	});

	it('has one page-title h1 (B1)', () => {
		render(IntegrityPage);
		expectOnePageTitle('Integrity');
	});
});
