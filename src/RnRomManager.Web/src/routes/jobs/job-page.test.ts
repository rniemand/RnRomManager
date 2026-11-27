import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

vi.mock('$app/state', () => ({ page: { params: { id: 'j1' } } }));

const { default: JobPage } = await import('./[id]/+page.svelte');

const job = {
	id: 'j1',
	type: 'Ingest',
	mode: null,
	triggerSource: 'Manual',
	status: 'Completed',
	requestedBy: 1,
	startedAt: '2026-11-05T10:00:00Z',
	finishedAt: '2026-11-05T10:01:00Z',
	summary: { Ingested: 1, Failed: 1 },
	error: null
};
const item = {
	id: 5,
	fileId: 'f',
	systemId: 'gb',
	sourcePath: 'ingest/gb/x.zip',
	archiveEntry: 'bad.gb',
	outcome: 'Failed',
	reason: 'TooSmall',
	details: null,
	gameId: null,
	sha1: null,
	createdAt: ''
};

describe('job detail page', () => {
	let calls: string[];

	beforeEach(() => {
		resetClient();
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin'],
			mustChangePassword: false
		});
		calls = [];
		vi.stubGlobal('fetch', (url: string) => {
			calls.push(url);
			const body =
				url === '/api/jobs/j1' ? job : { items: [item], page: 1, pageSize: 50, total: 1 };
			return Promise.resolve(new Response(JSON.stringify(body), { status: 200 }));
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('shows the summary and the items, filtered by outcome', async () => {
		render(JobPage);

		expect(await screen.findByText('Ingested 1')).toBeTruthy();
		expect(await screen.findByText('ingest/gb/x.zip › bad.gb')).toBeTruthy();
		expect(calls).toContain('/api/jobs/j1/items?page=1&pageSize=50');

		await fireEvent.change(screen.getByLabelText('Outcome'), { target: { value: 'Failed' } });

		await vi.waitFor(() =>
			expect(calls).toContain('/api/jobs/j1/items?page=1&pageSize=50&outcome=Failed')
		);
	});

	it('links back to all jobs (ROM Zip Config B18) as outline buttons (JB2)', async () => {
		render(JobPage);

		const all = await screen.findByRole('link', { name: 'All jobs' });
		expect(all.getAttribute('href')).toBe('/jobs');
		expect(all.className).toBe('btn btn-sm btn-outline-primary');
		expect(all.querySelector('i')!.className).toContain('bi-activity');
		const back = screen.getByRole('link', { name: 'Back to Ingestion' });
		expect(back.getAttribute('href')).toBe('/ingestion');
		expect(back.querySelector('i')!.className).toContain('bi-arrow-left');
		expect(back.parentElement!.className).toBe('d-flex flex-wrap gap-2 mb-3');
	});
});
