import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
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

const { default: ReportsIndex } = await import('./+page.svelte');
const { default: MissingImages } = await import('./missing-images/+page.svelte');

type Answer = { status: number; body?: unknown };

const systems = [
	{ id: 'gb', displayName: 'Game Boy', gameCount: 2, romCount: 2, regions: [] },
	{ id: 'gbc', displayName: 'Game Boy Color', gameCount: 1, romCount: 1, regions: [] }
];

describe('reports pages', () => {
	let calls: string[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		navigateTo('/reports');
		calls = [];
		answers = { 'GET /api/systems': { status: 200, body: systems } };
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
	afterEach(() => vi.unstubAllGlobals());

	it('/reports redirects to Library Size, replacing the history entry (RP1)', () => {
		render(ReportsIndex);
		expect(goto).toHaveBeenCalledWith('/reports/library-size', { replace: true });
	});

	describe('Missing Images (RP5)', () => {
		const list = (query = '') => `GET /api/reports/missing-images?page=1&pageSize=50${query}`;

		it('lists games with a link to their Images tab, the system badge and the reason', async () => {
			answers[list()] = {
				status: 200,
				body: {
					items: [
						{
							id: 'g1',
							displayTitle: 'Alpha',
							system: 'gb',
							systemName: 'Game Boy',
							reason: 'NoCover'
						},
						{
							id: 'g2',
							displayTitle: 'Echo',
							system: 'gbc',
							systemName: 'Game Boy Color',
							reason: 'Placeholder'
						}
					],
					page: 1,
					pageSize: 50,
					total: 2
				}
			};
			render(MissingImages);

			const link = await screen.findByRole('link', { name: 'Alpha' });
			expect(link.getAttribute('href')).toBe('/games/g1?tab=images');
			const rows = [...document.querySelectorAll('tbody tr')].map((r) =>
				[...r.querySelectorAll('td')].map((td) => td.textContent?.trim())
			);
			expect(rows).toEqual([
				['Alpha', 'Game Boy', 'No cover'],
				['Echo', 'Game Boy Color', 'Placeholder art']
			]);
			expect(document.querySelector('thead')!.className).toBe('table-dark');
		});

		it('the System filter sends system and goes back to page 1', async () => {
			answers[list()] = { status: 200, body: { items: [], page: 1, pageSize: 50, total: 0 } };
			render(MissingImages);
			await screen.findByRole('option', { name: 'Game Boy Color' });

			await fireEvent.change(screen.getByLabelText('System'), { target: { value: 'gbc' } });

			await vi.waitFor(() => expect(calls).toContain(list('&system=gbc')));
		});

		it('shows the empty state', async () => {
			answers[list()] = { status: 200, body: { items: [], page: 1, pageSize: 50, total: 0 } };
			render(MissingImages);

			expect(await screen.findByText('Every game has a cover image.')).toBeTruthy();
		});
	});
});
