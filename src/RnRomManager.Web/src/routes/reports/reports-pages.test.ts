import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
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
// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { default: ReportsIndex } = await import('./+page.svelte');
const { default: MissingImages } = await import('./missing-images/+page.svelte');
const { default: LibrarySize } = await import('./library-size/+page.svelte');
const { default: Duplicates } = await import('./duplicates/+page.svelte');

type Answer = { status: number; body?: unknown };

const systems = [
	{ id: 'gb', displayName: 'Game Boy', gameCount: 2, romCount: 2, regions: [] },
	{ id: 'gbc', displayName: 'Game Boy Color', gameCount: 1, romCount: 1, regions: [] }
];

describe('reports pages', () => {
	let calls: string[];
	let bodies: Record<string, unknown[]>;
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		navigateTo('/reports');
		calls = [];
		bodies = {};
		answers = { 'GET /api/systems': { status: 200, body: systems } };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url === '/api/auth/antiforgery')
				return Promise.resolve(new Response(null, { status: 204 }));
			calls.push(key);
			if (typeof init?.body === 'string') (bodies[key] ??= []).push(JSON.parse(init.body));
			const answer = answers[key] ?? { status: 204 };
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

	it('/reports redirects to Library Size, replacing the history entry (RP1)', () => {
		render(ReportsIndex);
		expect(goto).toHaveBeenCalledWith('/reports/library-size', { replace: true });
	});

	describe('Library Size (RP3)', () => {
		const library = {
			totalBytes: 3000,
			gameCount: 3,
			systems: [
				{ system: 'gbc', systemName: 'Game Boy Color', bytes: 2000, gameCount: 1 },
				{ system: 'gb', systemName: 'Game Boy', bytes: 1000, gameCount: 2 }
			]
		};
		const tiles = () => [...document.querySelectorAll('svg rect')];
		const tileLinks = () =>
			[...document.querySelectorAll('svg a')].map((a) => a.getAttribute('href'));

		it('library level: one rectangle per system in its colour, and the table with shares', async () => {
			answers['GET /api/reports/library-size'] = { status: 200, body: library };
			render(LibrarySize);

			await screen.findByRole('link', { name: 'Game Boy Color' });
			expect(document.querySelector('p')!.textContent).toContain('in 3 games');
			expect(tiles().map((r) => r.getAttribute('fill'))).toEqual([
				'var(--bs-info)',
				'var(--bs-success)'
			]);
			expect(tiles()[0].getAttribute('stroke')).toBe('var(--bs-body-bg)');
			expect(document.querySelector('svg')!.getAttribute('role')).toBe('img');
			expect(document.querySelector('svg')!.getAttribute('aria-label')).toContain('Game Boy Color');
			expect(document.querySelector('svg title')!.textContent).toBe('Game Boy Color — 2.0 KiB');
			expect(document.querySelector('svg text')!.getAttribute('fill')).toBe('#000');
			const rows = [...document.querySelectorAll('tbody tr')].map((r) =>
				[...r.querySelectorAll('td')].map((td) => td.textContent?.trim())
			);
			expect(rows).toEqual([
				['Game Boy Color', '1', '2.0 KiB', '66.7%'],
				['Game Boy', '2', '1000 B', '33.3%']
			]);
		});

		it('a system rectangle or row drills in with ?system=', async () => {
			answers['GET /api/reports/library-size'] = { status: 200, body: library };
			render(LibrarySize);

			const row = await screen.findByRole('link', { name: 'Game Boy' });
			expect(row.getAttribute('href')).toBe('/reports/library-size?system=gb');
			expect(tileLinks()).toEqual([
				'/reports/library-size?system=gbc',
				'/reports/library-size?system=gb'
			]);
		});

		it('system level: game rectangles and rows open the game; a breadcrumb leads back', async () => {
			navigateTo('/reports/library-size?system=gb');
			answers['GET /api/reports/library-size/gb'] = {
				status: 200,
				body: {
					system: 'gb',
					systemName: 'Game Boy',
					bytes: 1000,
					games: [
						{ id: 'g1', displayTitle: 'Bravo', bytes: 700, romCount: 2 },
						{ id: 'g2', displayTitle: 'Alpha', bytes: 300, romCount: 1 }
					]
				}
			};
			render(LibrarySize);

			expect((await screen.findByRole('link', { name: 'Bravo' })).getAttribute('href')).toBe(
				'/games/g1'
			);
			expect(tileLinks()).toEqual(['/games/g1', '/games/g2']);
			expect(screen.getByRole('link', { name: 'Library' }).getAttribute('href')).toBe(
				'/reports/library-size'
			);
			expect(screen.getByText('Game Boy', { selector: '.breadcrumb-item' })).toBeTruthy();
			expect(screen.getByRole('columnheader', { name: '% of system' })).toBeTruthy();
		});

		it('draws the 300 largest games plus one unclickable "others" rectangle', async () => {
			navigateTo('/reports/library-size?system=gb');
			const games = Array.from({ length: 305 }, (_, i) => ({
				id: `g${i}`,
				displayTitle: `Game ${i}`,
				bytes: 10_000 - i,
				romCount: 1
			}));
			answers['GET /api/reports/library-size/gb'] = {
				status: 200,
				body: { system: 'gb', systemName: 'Game Boy', bytes: 1, games }
			};
			render(LibrarySize);

			await screen.findByRole('link', { name: 'Game 304' });
			expect(tiles()).toHaveLength(301);
			expect(tileLinks()).toHaveLength(300);
			const others = [...document.querySelectorAll('svg g title')].map((t) => t.textContent);
			expect(others).toHaveLength(1);
			expect(others[0]).toMatch(/^5 others — /);
			expect(document.querySelectorAll('tbody tr')).toHaveLength(305);
		});

		it('an empty library shows the empty state', async () => {
			answers['GET /api/reports/library-size'] = {
				status: 200,
				body: { totalBytes: 0, gameCount: 0, systems: [] }
			};
			render(LibrarySize);

			expect(await screen.findByText('The library is empty.')).toBeTruthy();
			expect(document.querySelector('svg')).toBeNull();
		});
	});

	describe('Duplicates (RP9)', () => {
		const list = 'GET /api/reports/duplicates?page=1&pageSize=25';
		const game = (id: string, displayTitle: string, romCount: number) => ({
			id,
			displayTitle,
			romCount,
			zipSizeBytes: 2048,
			createdAt: '2026-11-01T00:00:00Z',
			boxArtUrl: null
		});
		const group = {
			system: 'gb',
			systemName: 'Game Boy',
			baseTitle: 'Quest',
			gamesKey: 'k'.repeat(64),
			games: [game('g1', 'Quest', 2), game('g2', 'Quest DX', 1), game('g3', 'Quest II', 1)]
		};
		const page = { items: [group], page: 1, pageSize: 25, total: 1 };
		const signIn = (roles: ('admin' | 'rom-manager')[]) =>
			auth.set({ id: 1, username: 'u', roles, mustChangePassword: false });
		const radio = (title: string) =>
			screen.getByRole('radio', { name: `Merge into ${title}` }) as HTMLInputElement;

		it('renders a card per group; the first game is pre-selected and labelled', async () => {
			signIn(['rom-manager']);
			answers[list] = { status: 200, body: page };
			render(Duplicates);

			expect(await screen.findByRole('heading', { name: 'Quest' })).toBeTruthy();
			expect([radio('Quest').checked, radio('Quest DX').checked]).toEqual([true, false]);
			const first = screen.getByRole('link', { name: 'Quest' }).closest('tr')!;
			expect(first.textContent).toContain('Suggested target');
			expect(screen.getAllByText('Suggested target')).toHaveLength(1);
		});

		it('a user without rom-manager sees the list without radios or buttons', async () => {
			signIn(['admin']);
			answers[list] = { status: 200, body: page };
			render(Duplicates);

			await screen.findByRole('link', { name: 'Quest DX' });
			expect(screen.queryAllByRole('radio')).toEqual([]);
			expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();
			expect(screen.queryByRole('button', { name: 'Merge into selected' })).toBeNull();
		});

		it('merge confirms, posts the selected target and the other ids, then reloads', async () => {
			signIn(['rom-manager']);
			answers[list] = { status: 200, body: page };
			answers['POST /api/reports/duplicates/merge'] = { status: 200, body: { targetGameId: 'g2' } };
			render(Duplicates);
			await screen.findByRole('link', { name: 'Quest DX' });

			await fireEvent.click(radio('Quest DX'));
			await fireEvent.click(screen.getByRole('button', { name: 'Merge into selected' }));
			const modal = document.querySelector('.modal')!;
			expect(modal.textContent?.replace(/\s+/g, ' ')).toContain('Merge 2 games into Quest DX?');
			await fireEvent.click(screen.getByRole('button', { name: 'Merge', hidden: true }));

			await vi.waitFor(() => expect(calls.filter((c) => c === list)).toHaveLength(2));
			expect(bodies['POST /api/reports/duplicates/merge']).toEqual([
				{ targetGameId: 'g2', sourceGameIds: ['g1', 'g3'] }
			]);
		});

		it('dismiss posts the group key and reloads', async () => {
			signIn(['rom-manager']);
			answers[list] = { status: 200, body: page };
			render(Duplicates);
			await screen.findByRole('link', { name: 'Quest DX' });

			await fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

			await vi.waitFor(() => expect(calls.filter((c) => c === list)).toHaveLength(2));
			expect(bodies['POST /api/reports/duplicates/dismiss']).toEqual([
				{ system: 'gb', baseTitle: 'Quest', gamesKey: group.gamesKey }
			]);
		});

		it('a merge that stops part-way toasts the problem and the merged count', async () => {
			signIn(['rom-manager']);
			toasts.items = [];
			answers[list] = { status: 200, body: page };
			answers['POST /api/reports/duplicates/merge'] = {
				status: 404,
				body: { status: 404, code: 'NotFound', title: 'Not found', merged: ['g2'] }
			};
			render(Duplicates);
			await screen.findByRole('link', { name: 'Quest DX' });

			await fireEvent.click(screen.getByRole('button', { name: 'Merge into selected' }));
			await fireEvent.click(screen.getByRole('button', { name: 'Merge', hidden: true }));

			await vi.waitFor(() =>
				expect(toasts.items.at(-1)).toMatchObject({
					kind: 'danger',
					text: 'Not found 1 merged before it stopped.'
				})
			);
		});

		it('shows the empty state', async () => {
			answers[list] = { status: 200, body: { items: [], page: 1, pageSize: 25, total: 0 } };
			render(Duplicates);

			expect(await screen.findByText('No duplicate games found.')).toBeTruthy();
		});
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
