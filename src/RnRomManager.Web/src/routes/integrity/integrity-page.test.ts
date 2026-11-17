import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';
import { expectOnePageTitle } from '#lib/testing/pageTitle.js';

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

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
				(url.startsWith('/api/integrity/issues') ? { ...empty, pageSize: 50 } : undefined);
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

	it('hides the storage migration button when no legacy games remain (S4)', async () => {
		fixed['GET /api/jobs/storage-migration'] = { legacyGames: 0 };
		render(IntegrityPage);

		await screen.findByRole('button', { name: 'Run quick' });
		expect(screen.queryByRole('button', { name: /Move DS ROMs out of ZIPs/ })).toBeNull();
	});

	it('starts the storage migration while legacy games remain (S4)', async () => {
		fixed['GET /api/jobs/storage-migration'] = { legacyGames: 3 };
		render(IntegrityPage);

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Move DS ROMs out of ZIPs (3)' })
		);

		expect(calls.map((c) => c.key)).toContain('POST /api/jobs/storage-migration');
	});

	it('shows status, merged history (newest first) and open issues', async () => {
		render(IntegrityPage);

		expect(await screen.findByText('Never')).toBeTruthy();
		expect(screen.getByText('Not scheduled')).toBeTruthy();
		const jobsColumn = screen.getAllByRole('row').map((r) => r.textContent ?? '');
		expect(jobsColumn.findIndex((t) => t.includes('MediaRetry'))).toBeLessThan(
			jobsColumn.findIndex((t) => t.includes('Integrity (Quick)'))
		);
		expect(calls.map((c) => c.key)).toContain(
			'GET /api/integrity/issues?page=1&pageSize=50&status=Open'
		);
	});

	it('Run deep starts an integrity job in Deep mode', async () => {
		render(IntegrityPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Run deep' }));

		await vi.waitFor(() =>
			expect(calls).toContainEqual({ key: 'POST /api/jobs/integrity', body: { mode: 'Deep' } })
		);
	});

	it('issue filters go into the query and reset the page', async () => {
		render(IntegrityPage);
		await screen.findByText('No issues.');

		await fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'HashMismatch' } });

		await vi.waitFor(() =>
			expect(calls.map((c) => c.key)).toContain(
				'GET /api/integrity/issues?page=1&pageSize=50&status=Open&type=HashMismatch'
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
