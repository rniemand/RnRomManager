import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/state', () => ({ page: { params: {}, url: new URL('http://localhost/ingestion') } }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: Page } = await import('./ingestion/+page.svelte');

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

/** Answers every load of the page; `/api/jobs/current` is a running job of `type`. */
function stubApi(type: string): void {
	vi.stubGlobal('fetch', (input: string) => {
		if (input === '/api/jobs/current')
			return Promise.resolve(
				json({
					id: 'j1',
					type,
					mode: null,
					status: 'Running',
					current: 0,
					total: 1,
					currentItem: 'disc2.iso',
					startedAt: '2026-11-13T12:00:00Z'
				})
			);
		if (input === '/api/integrity/status')
			return Promise.resolve(json({ lastQuick: null, lastDeep: null, nextScheduledAt: null }));
		if (input.startsWith('/api/ingest/') || input === '/api/admin/dats')
			return Promise.resolve(json([]));
		return Promise.resolve(json({ items: [], page: 1, pageSize: 10, total: 0 }));
	});
}

describe('ingestion page: running job card', () => {
	beforeEach(() => {
		resetClient();
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
	});

	afterEach(() => vi.unstubAllGlobals());

	it('has no Cancel button while a ReviewResolve job runs (B4)', async () => {
		stubApi('ReviewResolve');
		render(Page);

		expect(await screen.findByText('ReviewResolve running')).toBeTruthy();
		expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull();
	});

	it('keeps Cancel for a cancellable job', async () => {
		stubApi('Ingest');
		render(Page);

		expect(await screen.findByRole('button', { name: 'Cancel' })).toBeTruthy();
	});
});
