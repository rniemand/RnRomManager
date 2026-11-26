import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { formatDateTime } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { jobs, pollIntervalMs } from '#lib/stores/jobs.svelte.js';

const { default: JobsPage } = await import('./+page.svelte');

const job = (id: string, status = 'Completed') => ({
	id,
	type: 'StorageMigration',
	mode: null,
	triggerSource: 'Manual',
	status,
	requestedBy: 1,
	startedAt: '2026-11-18T10:00:00Z',
	finishedAt: status === 'Running' ? null : '2026-11-18T10:05:00Z',
	summary: null,
	error: null
});
const current = {
	id: 'run',
	type: 'StorageMigration',
	mode: null,
	status: 'Running',
	current: 2,
	total: 5,
	currentItem: 'nds/A/Alpha.zip',
	startedAt: '2026-11-18T10:00:00Z'
};

describe('jobs page (ROM Zip Config B18)', () => {
	let calls: { url: string; method: string }[];
	let running: typeof current | null;
	let total: number;

	beforeEach(() => {
		resetClient();
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		jobs.current = null; // the store outlives each test
		calls = [];
		running = null;
		total = 1;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			calls.push({ url, method: init?.method ?? 'GET' });
			if (url === '/api/jobs/current')
				return Promise.resolve(
					running ? new Response(JSON.stringify(running)) : new Response(null, { status: 204 })
				);
			if (url.endsWith('/cancel')) return Promise.resolve(new Response(null, { status: 202 }));
			const page = Number(new URL(url, 'http://x').searchParams.get('page'));
			return Promise.resolve(
				new Response(JSON.stringify({ items: [job(`j${page}`)], page, pageSize: 25, total }))
			);
		});
	});
	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('shows "No job running" and the recent jobs with label, trigger, status and a details link', async () => {
		render(JobsPage);

		const idle = await screen.findByText('No job running.');
		expect(idle.tagName).toBe('TD');
		expect(idle.getAttribute('colspan')).toBe('5');
		const [runningTable] = screen.getAllByRole('table');
		expect(
			[...runningTable.querySelectorAll('thead th')].map((h) => h.textContent?.trim())
		).toEqual(['Job', 'Progress', 'Started', 'Details', 'Cancel']);
		const row = (await screen.findByText('Moving ROMs')).closest('tr')!;
		expect(row.textContent).toContain('Manual');
		expect(row.textContent).toContain('Completed');
		expect(row.querySelector('a')!.getAttribute('href')).toBe('/jobs/j1');
		expect(calls.map((c) => c.url)).toContain('/api/jobs?page=1&pageSize=25');
	});

	it('shows the running job with its progress, and Cancel posts the cancel', async () => {
		running = current;
		render(JobsPage);

		const progress = await screen.findByText('2 / 5 · nds/A/Alpha.zip');
		expect(progress.className).toContain('text-truncate');
		expect(progress.getAttribute('title')).toBe('2 / 5 · nds/A/Alpha.zip');
		expect(progress.closest('table')!.className).toContain('job-fixed-table');
		const row = progress.closest('tr')!;
		const cells = row.querySelectorAll('td');
		expect(cells[2].textContent?.trim()).toBe(formatDateTime(current.startedAt));
		expect(cells[2].className).toBe('text-nowrap');
		expect(cells[3].querySelector('a')!.getAttribute('href')).toBe('/jobs/run');
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

		await vi.waitFor(() =>
			expect(calls).toContainEqual({ url: '/api/jobs/run/cancel', method: 'POST' })
		);
	});

	it('a running job that cannot be cancelled has no Cancel button', async () => {
		running = { ...current, type: 'ReviewResolve' };
		render(JobsPage);

		await screen.findByText('2 / 5 · nds/A/Alpha.zip');
		expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull();
	});

	it('pages through the list', async () => {
		total = 60;
		render(JobsPage);
		await screen.findByText('Moving ROMs');

		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));

		await vi.waitFor(() =>
			expect(calls.map((c) => c.url)).toContain('/api/jobs?page=2&pageSize=25')
		);
	});

	it('reloads the list when the running job ends', async () => {
		running = current;
		render(JobsPage);
		await screen.findByText('2 / 5 · nds/A/Alpha.zip');
		const lists = () => calls.filter((c) => c.url.startsWith('/api/jobs?')).length;
		const before = lists();

		running = null;
		await new Promise((r) => setTimeout(r, pollIntervalMs + 200));

		await vi.waitFor(() => expect(lists()).toBeGreaterThan(before));
		expect(await screen.findByText('No job running.')).toBeTruthy();
	});
});
