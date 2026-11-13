import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail } from '#lib/api/types.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

vi.mock('$app/navigation', () => ({ goto: (url: string) => navigateTo(url) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: 'g1' },
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: GamePage } = await import('./[id]/+page.svelte');

const game = (): GameDetail => ({
	id: 'g1',
	system: 'gb',
	systemName: 'Game Boy',
	browserCore: null,
	title: 'Tetris',
	displayTitle: 'Tetris',
	releaseYear: null,
	developers: [],
	publishers: [],
	genres: [],
	description: null,
	hasDefaultBoxArt: false,
	rating: null,
	favourite: false,
	metadata: { createdAt: '', updatedAt: '' },
	roms: [],
	images: []
});

const listItem = (id: string, name: string) => ({
	id,
	name,
	gameCount: 1,
	coverUrl: null,
	updatedAt: ''
});

type Answer = { status: number; body?: unknown };

describe('game page collections (B25)', () => {
	let calls: { key: string; body?: string }[];
	let answers: Record<string, Answer[]>;

	beforeEach(() => {
		resetClient();
		navigateTo('/games/g1');
		toasts.items.length = 0;
		calls = [];
		answers = {
			'GET /api/games/g1': [{ status: 200, body: game() }],
			'GET /api/games/g1/collections': [
				{
					status: 200,
					body: [
						{ id: 'c1', name: 'Favourites' },
						{ id: 'c2', name: 'Puzzle' }
					]
				}
			],
			'GET /api/collections?pageSize=200': [
				{
					status: 200,
					body: {
						items: [
							listItem('c1', 'Favourites'),
							listItem('c2', 'Puzzle'),
							listItem('c3', 'Retro')
						],
						page: 1,
						pageSize: 200,
						total: 3
					}
				}
			]
		};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url !== '/api/auth/antiforgery') calls.push({ key, body: init?.body as string });
			const answer = answers[key]?.shift() ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	const modal = () => {
		const title = [...document.querySelectorAll('.modal-title')].find((t) =>
			t.textContent?.startsWith('Collections —')
		)!;
		return within(title.closest<HTMLElement>('.modal')!);
	};
	const box = (name: string) =>
		modal().getByRole('checkbox', {
			name: (accessible) => accessible.startsWith(`${name} `),
			hidden: true
		}) as HTMLInputElement;
	const badges = () =>
		within(screen.getByRole('list', { name: 'In collections' }))
			.getAllByRole('link')
			.map((a) => [a.textContent?.trim(), a.getAttribute('href')]);

	async function openModal(): Promise<void> {
		render(GamePage);
		await fireEvent.click(await screen.findByRole('button', { name: /Collections/ }));
		await vi.waitFor(() => expect(box('Retro')).toBeTruthy());
	}

	it('badges under the title link to each collection holding the game', async () => {
		render(GamePage);

		await vi.waitFor(() =>
			expect(badges()).toEqual([
				['Favourites', '/collections/c1'],
				['Puzzle', '/collections/c2']
			])
		);
	});

	it('no collections → no badge row', async () => {
		answers['GET /api/games/g1/collections'] = [{ status: 200, body: [] }];
		render(GamePage);

		await screen.findByRole('heading', { name: 'Tetris' });
		expect(screen.queryByRole('list', { name: 'In collections' })).toBeNull();
	});

	it('the modal ticks the collections holding the game and links to /collections', async () => {
		await openModal();

		expect([box('Favourites').checked, box('Puzzle').checked, box('Retro').checked]).toEqual([
			true,
			true,
			false
		]);
		expect(
			modal()
				.getByRole('link', { name: 'Create or manage collections', hidden: true })
				.getAttribute('href')
		).toBe('/collections');
	});

	it('ticking adds at once and shows the new badge; unticking removes', async () => {
		answers['POST /api/collections/c3/games'] = [{ status: 200, body: { added: 1 } }];
		await openModal();

		await fireEvent.click(box('Retro'));
		await vi.waitFor(() =>
			expect(badges().map(([name]) => name)).toEqual(['Favourites', 'Puzzle', 'Retro'])
		);
		const add = calls.find((c) => c.key === 'POST /api/collections/c3/games')!;
		expect(JSON.parse(add.body!)).toEqual({ gameIds: ['g1'] });

		await fireEvent.click(box('Favourites'));
		await vi.waitFor(() => expect(badges().map(([name]) => name)).toEqual(['Puzzle', 'Retro']));
		expect(calls.map((c) => c.key)).toContain('DELETE /api/collections/c1/games/g1');
	});

	it('a failed change rolls back with an error toast', async () => {
		answers['DELETE /api/collections/c1/games/g1'] = [
			{ status: 500, body: { status: 500, code: 'HttpError', title: 'Server error' } }
		];
		await openModal();

		await fireEvent.click(box('Favourites'));

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => [t.kind, t.text])).toContainEqual([
				'danger',
				'Removing from Favourites failed: Server error'
			])
		);
		expect(box('Favourites').checked).toBe(true);
		expect(badges().map(([name]) => name)).toEqual(['Favourites', 'Puzzle']);
	});

	it('over 200 collections shows the filter hint; the filter searches on the server', async () => {
		answers['GET /api/collections?pageSize=200'] = [
			{
				status: 200,
				body: { items: [listItem('c1', 'Favourites')], page: 1, pageSize: 200, total: 250 }
			}
		];
		answers['GET /api/collections?q=retro&pageSize=200'] = [
			{ status: 200, body: { items: [listItem('c3', 'Retro')], page: 1, pageSize: 200, total: 1 } }
		];
		render(GamePage);
		await fireEvent.click(await screen.findByRole('button', { name: /Collections/ }));

		expect(await modal().findByText('Showing the first 200 — filter to find more.')).toBeTruthy();
		await fireEvent.input(modal().getByLabelText('Filter collections'), {
			target: { value: ' retro ' }
		});
		await vi.waitFor(() => expect(box('Retro')).toBeTruthy(), { timeout: 2000 });
		expect(modal().queryByText(/Showing the first 200/)).toBeNull();
	});
});
