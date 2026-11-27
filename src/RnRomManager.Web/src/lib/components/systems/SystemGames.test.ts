import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { tick } from 'svelte';
import { auth } from '#lib/stores/auth.svelte.js';
import { resetClient } from '#lib/api/client.js';
import type { GameListItem, SystemInfo } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: SystemGames } = await import('./SystemGames.svelte');

const gb: SystemInfo = {
	id: 'gb',
	displayName: 'Game Boy',
	shortName: 'GB',
	gameCount: 3,
	romCount: 3,
	regions: [],
	logoUrl: null
};
const game = (id: string, title: string): GameListItem => ({
	id,
	system: 'gb',
	displayTitle: title,
	releaseYear: 1990,
	regions: ['USA'],
	romCount: 1,
	primarySizeBytes: 1024,
	boxArtUrl: null,
	systemName: 'Game Boy',
	systemShortName: 'GB',
	rating: null,
	favourite: false,
	matchedOn: null,
	updateCount: 0,
	dlcCount: 0,
	saveCount: 0,
	ownSaveCount: 0,
	ownStateSaveCount: null,
	igdbRating: null,
	playRomId: null,
	playRomFileName: null,
	playDisc: false,
	browserCore: null,
	igdbId: null
});
const pageOf = (items: GameListItem[], total = items.length, page = 1) => ({
	items,
	page,
	pageSize: 20,
	total
});
const url = (page = 1, size = 20, system = 'gb') =>
	`/api/games?system=${system}&page=${page}&pageSize=${size}`;
const hidden = { hidden: true };
/** Text a reader gets: the decorative logo (`aria-hidden`) left out. */
const shownText = (el: Element) => {
	const copy = el.cloneNode(true) as Element;
	copy.querySelectorAll('[aria-hidden="true"]').forEach((n) => n.remove());
	return copy.textContent?.replace(/\s+/g, ' ').trim();
};

type Reply = { status: number; body?: unknown };
let calls: string[];
let answers: Record<string, (Reply | Promise<Reply>)[]>;
let store: Map<string, string>;

beforeEach(() => {
	resetClient();
	calls = [];
	answers = {};
	store = new Map<string, string>();
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
afterEach(() => {
	vi.unstubAllGlobals();
	auth.set(null);
});

describe('SystemGames (Systems tab B5)', () => {
	it("shows the system's games with the heading and count from total", async () => {
		answers[url()] = [
			{ status: 200, body: pageOf([game('g1', 'Tetris'), game('g2', 'Orbit Blocks')], 2) }
		];
		render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn() });

		expect((await screen.findByRole('link', { name: 'Tetris' })).getAttribute('href')).toBe(
			'/games/g1'
		);
		expect(calls).toEqual([url()]);
		expect(shownText(screen.getByRole('heading', { level: 2, name: /Game Boy/ }))).toBe(
			'Game Boy · 2 games'
		);
	});

	it('pages through the URL and remembers the page size under systemGames', async () => {
		const onpage = vi.fn();
		store.set('rnrm.pageSize.systemGames', '10');
		answers[url(2, 10)] = [{ status: 200, body: pageOf([game('g11', 'Zelda')], 25, 2) }];
		render(SystemGames, { system: gb, gamePage: 2, onpage });

		expect(await screen.findByText('Page 2 of 3')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
		expect(onpage).toHaveBeenCalledWith(3);
	});

	it('says when the system has no games, and still offers the emulators', async () => {
		answers[url()] = [{ status: 200, body: pageOf([], 0) }];
		render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn() });

		expect(await screen.findByText('No games for Game Boy yet.')).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Emulators' })).toBeTruthy();
	});

	it('says when the page is past the end', async () => {
		answers[url(9)] = [{ status: 200, body: pageOf([], 3, 9) }];
		render(SystemGames, { system: gb, gamePage: 9, onpage: vi.fn() });

		expect(await screen.findByText('There are no games on this page.')).toBeTruthy();
	});

	it('shows a failure and retries', async () => {
		answers[url()] = [
			{ status: 500, body: { status: 500, title: 'Boom' } },
			{ status: 200, body: pageOf([game('g1', 'Tetris')]) }
		];
		render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn() });

		await fireEvent.click(await screen.findByRole('button', { name: /Retry/ }));
		expect(await screen.findByRole('link', { name: 'Tetris' })).toBeTruthy();
	});

	it('a late answer for the previous system never replaces the current one', async () => {
		let releaseFirst!: (reply: Reply) => void;
		answers[url()] = [new Promise((resolve) => (releaseFirst = resolve))];
		answers[url(1, 20, 'nes')] = [{ status: 200, body: pageOf([game('n1', 'Mario')]) }];
		const nes = { ...gb, id: 'nes', displayName: 'NES' };
		const rendered = render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn() });

		await rendered.rerender({ system: nes, gamePage: 1, onpage: vi.fn() });
		expect(await screen.findByRole('link', { name: 'Mario' })).toBeTruthy();
		releaseFirst({ status: 200, body: pageOf([game('g1', 'Tetris')]) });
		await new Promise((r) => setTimeout(r, 0));

		expect(screen.queryByRole('link', { name: 'Tetris' })).toBeNull();
		expect(screen.getByRole('link', { name: 'Mario' })).toBeTruthy();
	});

	it('the heading shows the shared logo, else a text banner of the name', async () => {
		answers[url()] = [
			{ status: 200, body: pageOf([]) },
			{ status: 200, body: pageOf([]) }
		];
		const shared = '/api/discovery/systems/logo?key=gb&v=4';
		const view = render(SystemGames, {
			system: { ...gb, logoUrl: shared },
			gamePage: 1,
			onpage: vi.fn()
		});
		const heading = () => screen.getByRole('heading', { level: 2, name: /Game Boy/ });

		expect(heading().querySelector('img')!.getAttribute('src')).toBe(shared);
		view.unmount();
		render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn() });
		expect(heading().querySelector('img')).toBeNull();
		expect(heading().querySelector('.group-logo-banner')!.textContent).toBe('Game Boy');
	});

	it.each([
		[['admin'] as 'admin'[], 1],
		[[] as 'admin'[], 0]
	])(
		'Change logo shows only for an Admin (roles %j) and opens it for this system',
		async (roles, count) => {
			auth.set({ id: 1, username: 'u', roles, mustChangePassword: false });
			answers[url()] = [{ status: 200, body: pageOf([]) }];
			const onedit = vi.fn();
			render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn(), onedit });

			const buttons = screen.queryAllByRole('button', { name: /Change logo/ });
			expect(buttons).toHaveLength(count);
			if (count) {
				await fireEvent.click(buttons[0]);
				expect(onedit).toHaveBeenCalledWith(gb);
			}
		}
	);

	it('opens the emulators modal and returns focus to the button when it closes', async () => {
		answers[url()] = [{ status: 200, body: pageOf([game('g1', 'Tetris')]) }];
		render(SystemGames, { system: gb, gamePage: 1, onpage: vi.fn() });
		await screen.findByRole('link', { name: 'Tetris' });
		const button = screen.getByRole('button', { name: 'Emulators' });

		await fireEvent.click(button);
		const close = screen.getAllByRole('button', { name: 'Close', ...hidden }).at(-1)!;
		expect(screen.getByRole('heading', { name: 'Game Boy Emulators', ...hidden })).toBeTruthy();
		close.focus();
		await fireEvent.click(close);
		await tick();

		expect(document.activeElement).toBe(button);
	});
});
