import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/state', () => ({
	page: { params: { id: 'j1' }, url: new URL('http://localhost/jobs/j1') }
}));

function stubJob(fields: Record<string, unknown>): void {
	const job = {
		id: 'j1',
		mode: null,
		triggerSource: 'Manual',
		status: 'Completed',
		requestedBy: 1,
		startedAt: '2026-11-13T12:00:00Z',
		finishedAt: '2026-11-13T12:00:05Z',
		error: null,
		...fields
	};
	const empty = { items: [], page: 1, pageSize: 50, total: 0 };
	vi.stubGlobal('fetch', (url: string) =>
		Promise.resolve(
			new Response(JSON.stringify(url.includes('/items') ? empty : job), { status: 200 })
		)
	);
}

const { default: JobPage } = await import('./jobs/[id]/+page.svelte');

describe('job page', () => {
	beforeEach(() => {
		resetClient();
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		stubJob({
			type: 'ReviewResolve',
			summary: {
				reviewId: 7,
				fileName: 'disc2.iso',
				gameId: null,
				code: 'ValidationFailed',
				errors: { title: ['Bad.'] }
			}
		});
	});

	afterEach(() => vi.unstubAllGlobals());

	it("shows a ReviewResolve summary's errors as JSON and a null value as null (B6)", async () => {
		render(JobPage);

		expect(await screen.findByText('errors {"title":["Bad."]}')).toBeTruthy();
		expect(screen.getByText('gameId null')).toBeTruthy();
		expect(screen.getByText('fileName disc2.iso')).toBeTruthy();
		expect(screen.queryByText(/\[object Object\]/)).toBeNull();
	});

	it("shows a DatRefresh job's versions as a table, not a JSON badge", async () => {
		stubJob({
			type: 'DatRefresh',
			summary: { files: 2, versions: { 'Nintendo - Game Boy': '2026.11.14' } }
		});
		render(JobPage);

		expect(await screen.findByText('files 2')).toBeTruthy();
		expect(screen.getByRole('columnheader', { name: 'DAT' })).toBeTruthy();
		expect(screen.getByRole('cell', { name: 'Nintendo - Game Boy' })).toBeTruthy();
		expect(screen.getByRole('cell', { name: '2026.11.14' })).toBeTruthy();
		expect(screen.queryByText(/versions \{/)).toBeNull();
	});

	it('shows Started, Finished and Trigger as a table', async () => {
		stubJob({ type: 'Integrity', summary: null });
		render(JobPage);

		expect(await screen.findByRole('columnheader', { name: 'Trigger' })).toBeTruthy();
		expect(screen.getByRole('columnheader', { name: 'Started' })).toBeTruthy();
		expect(screen.getByRole('cell', { name: 'Manual' })).toBeTruthy();
	});

	it('heads the page with the job label and "Job", the mode in brackets (J3)', async () => {
		stubJob({ type: 'Ingest', summary: null });
		render(JobPage);
		expect((await screen.findByRole('heading', { level: 1 })).textContent).toMatch(
			/^\s*Ingest Job\s/
		);
	});

	it('heads a job with a mode as "Ingest (Quick) Job"', async () => {
		stubJob({ type: 'Ingest', mode: 'Quick', summary: null });
		render(JobPage);
		expect((await screen.findByRole('heading', { level: 1 })).textContent).toMatch(
			/^\s*Ingest \(Quick\) Job\s/
		);
	});
});
