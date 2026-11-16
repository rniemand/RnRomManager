import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { GameListItem } from '../api/types';
import type { DiscoveryState } from '../discovery';

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));
vi.mock('../tooltip', () => ({ tooltip: () => ({}) }));

const { default: DiscoveryGames } = await import('./DiscoveryGames.svelte');

const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
const game = (id: string, title: string, system: string, short: string): GameListItem => ({
	id,
	system,
	displayTitle: title,
	releaseYear: 1990,
	regions: ['USA'],
	romCount: 1,
	primarySizeBytes: 1024,
	boxArtUrl: null,
	systemName: short,
	systemShortName: short,
	rating: null,
	favourite: false,
	matchedOn: null,
	updateCount: 0,
	saveCount: 0,
	playRomId: null,
	playRomFileName: null,
	playDisc: false,
	browserCore: null
});
const pageOf = (items: GameListItem[], total = items.length, page = 1) => ({
	items,
	page,
	pageSize: 20,
	total
});
const view = (extra: Partial<Omit<DiscoveryState, 'key'>> = {}) => ({
	kind: 'publishers' as const,
	key,
	groupPage: 1,
	gamePage: 1,
	...extra
});
const url = (kind: string, extra = '') =>
	`/api/discovery/${kind}/games?key=${key}${extra}&pageSize=20`;

type Reply = { status: number; body?: unknown };
let calls: string[];
let answers: Record<string, (Reply | Promise<Reply>)[]>;

beforeEach(() => {
	resetClient();
	calls = [];
	answers = {};
	const store = new Map<string, string>();
	vi.stubGlobal('localStorage', {
		getItem: (k: string) => store.get(k) ?? null,
		setItem: (k: string, value: string) => void store.set(k, value)
	});
	vi.stubGlobal('fetch', async (requested: string) => {
		calls.push(requested);
		const answer = await (answers[requested]?.shift() ?? { status: 500, body: { status: 500 } });
		const payload = answer.body === undefined ? null : JSON.stringify(answer.body);
		return new Response(payload, { status: answer.status });
	});
});
afterEach(() => vi.unstubAllGlobals());

describe('DiscoveryGames (discovery B2)', () => {
	it("shows the group's games across systems, each linking to its page", async () => {
		answers[url('publishers')] = [
			{
				status: 200,
				body: pageOf([game('g1', 'Mario', 'nes', 'NES'), game('g2', 'Tetris', 'gb', 'GB')], 2)
			}
		];
		render(DiscoveryGames, { view: view(), name: 'Nintendo', onpage: vi.fn() });

		expect((await screen.findByRole('link', { name: 'Mario' })).getAttribute('href')).toBe(
			'/games/g1'
		);
		expect(screen.getByRole('link', { name: 'Tetris' }).getAttribute('href')).toBe('/games/g2');
		expect(screen.getAllByText('NES').length).toBeGreaterThan(0);
		expect(screen.getAllByText('GB').length).toBeGreaterThan(0);
		expect(screen.getByRole('heading', { name: /Nintendo/ }).textContent).toContain('2 games');
	});

	it('without the group name, the heading names the kind', async () => {
		answers[url('genres')] = [{ status: 200, body: pageOf([game('g1', 'Tetris', 'gb', 'GB')]) }];
		render(DiscoveryGames, { view: view({ kind: 'genres' }), name: null, onpage: vi.fn() });

		await screen.findByRole('link', { name: 'Tetris' });
		expect(screen.getByRole('heading', { name: /Selected genre/ }).textContent).toBe(
			'Selected genre · 1 game'
		);
	});

	it('pages through the URL', async () => {
		const onpage = vi.fn();
		answers[url('publishers', '&page=2')] = [
			{ status: 200, body: pageOf([game('g21', 'Zelda', 'snes', 'SNES')], 45, 2) }
		];
		render(DiscoveryGames, { view: view({ gamePage: 2 }), name: 'Nintendo', onpage });

		expect(await screen.findByText('Page 2 of 3')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
		expect(onpage).toHaveBeenCalledWith(3);
	});

	it('a selection no game has any more offers the way back, keeping the group page', async () => {
		answers[url('genres')] = [{ status: 404, body: { status: 404, code: 'NotFound' } }];
		render(DiscoveryGames, {
			view: view({ kind: 'genres', groupPage: 3 }),
			name: null,
			onpage: vi.fn()
		});

		expect(await screen.findByText('No games have this genre any more.')).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Back to all genres' }).getAttribute('href')).toBe(
			'/collections?tab=genres&groupPage=3'
		);
		expect(screen.queryByRole('alert')).toBeNull();
	});

	it('another failure shows the error and retries', async () => {
		answers[url('publishers')] = [
			{ status: 500, body: { status: 500, title: 'Boom' } },
			{ status: 200, body: pageOf([game('g1', 'Mario', 'nes', 'NES')]) }
		];
		render(DiscoveryGames, { view: view(), name: 'Nintendo', onpage: vi.fn() });

		await fireEvent.click(await screen.findByRole('button', { name: /Retry/ }));

		expect(await screen.findByRole('link', { name: 'Mario' })).toBeTruthy();
	});

	it('a late answer for the previous page never replaces the current one', async () => {
		let releaseFirst!: (reply: Reply) => void;
		answers[url('publishers')] = [new Promise((resolve) => (releaseFirst = resolve))];
		answers[url('publishers', '&page=2')] = [
			{ status: 200, body: pageOf([game('g21', 'Zelda', 'snes', 'SNES')], 25, 2) }
		];
		const rendered = render(DiscoveryGames, { view: view(), name: 'Nintendo', onpage: vi.fn() });

		await rendered.rerender({ view: view({ gamePage: 2 }), name: 'Nintendo', onpage: vi.fn() });
		expect(await screen.findByRole('link', { name: 'Zelda' })).toBeTruthy();
		releaseFirst({ status: 200, body: pageOf([game('g1', 'Mario', 'nes', 'NES')], 25) });
		await new Promise((r) => setTimeout(r, 0));

		expect(screen.queryByRole('link', { name: 'Mario' })).toBeNull();
		expect(screen.getByRole('link', { name: 'Zelda' })).toBeTruthy();
	});
});
