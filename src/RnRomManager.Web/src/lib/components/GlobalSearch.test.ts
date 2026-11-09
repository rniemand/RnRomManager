import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import { auth } from '../stores/auth.svelte';
import type { Role } from '../api/types';

const goto = vi.fn();
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({ page: { url: new URL('http://localhost/') } }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

// jsdom has no layout/transitions for Bootstrap's Modal; record show/hide instead.
const shown = { count: 0, hidden: 0, options: [] as unknown[], calls: [] as string[] };
vi.mock('../bootstrap', () => ({
	Modal: class {
		constructor(_element: Element, config: unknown) {
			shown.options.push(config);
		}
		show() {
			shown.count++;
		}
		hide() {
			shown.hidden++;
		}
		dispose() {
			shown.calls.push('dispose');
		}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => shown.calls.push('releaseModal') }));

const { default: GlobalSearch } = await import('./GlobalSearch.svelte');
const { default: Navbar } = await import('./Navbar.svelte');

const item = (id: string, displayTitle: string, extra = {}) => ({
	id,
	system: 'gb',
	systemName: 'Game Boy',
	displayTitle,
	releaseYear: null as number | null,
	regions: [],
	romCount: 1,
	boxArtUrl: null as string | null,
	rating: null as number | null,
	favourite: false,
	matchedOn: null as { field: string; value: string } | null,
	...extra
});

describe('GlobalSearch', () => {
	let requested: string[];
	let aborted: string[];
	let items: unknown[];
	let collections: unknown[];
	let fail: boolean;
	let failCollections: boolean;

	beforeEach(() => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		resetClient();
		goto.mockClear();
		shown.count = 0;
		shown.hidden = 0;
		requested = [];
		aborted = [];
		fail = false;
		failCollections = false;
		collections = [];
		items = [
			item('g1', 'Tetris', { releaseYear: 1989, boxArtUrl: '/api/games/g1/images/1', rating: 4 }),
			item('g2', 'Tetris 2', { favourite: true })
		];
		auth.set({ id: 1, username: 'plain', roles: [], mustChangePassword: false });
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			requested.push(url);
			init?.signal?.addEventListener('abort', () => aborted.push(url));
			const isCollections = url.startsWith('/api/collections');
			const failed = isCollections ? failCollections : fail;
			const list = isCollections ? collections : items;
			const body = failed
				? { code: 'HttpError', title: isCollections ? 'Collections are down' : 'Search is down' }
				: { items: list, page: 1, pageSize: 10, total: list.length };
			return Promise.resolve(new Response(JSON.stringify(body), { status: failed ? 500 : 200 }));
		});
	});
	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
		auth.set(null);
		document.body.innerHTML = '';
	});

	const input = () => screen.getByRole('combobox', { hidden: true }) as HTMLInputElement;
	const press = (init: KeyboardEventInit, target: EventTarget = window) => {
		const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
		target.dispatchEvent(event);
		return event;
	};
	async function type(value: string): Promise<void> {
		await fireEvent.input(input(), { target: { value } });
	}
	async function typeAndWait(value: string): Promise<void> {
		await type(value);
		await vi.advanceTimersByTimeAsync(250);
		await vi.waitFor(() =>
			expect(screen.queryAllByRole('option', { hidden: true })).not.toEqual([])
		);
	}

	it.each([
		['Ctrl+Space', { ctrlKey: true, code: 'Space', key: ' ' }],
		['Ctrl+K', { ctrlKey: true, key: 'k' }],
		['Meta+K', { metaKey: true, key: 'k' }]
	])('%s opens it and focuses the input, also from inside another input', async (_, init) => {
		const other = document.body.appendChild(document.createElement('input'));
		other.focus();
		render(GlobalSearch, { open: false });

		const event = press(init, other);

		expect(event.defaultPrevented).toBe(true);
		await vi.waitFor(() => expect(shown.count).toBe(1));
		await vi.waitFor(() => expect(document.activeElement).toBe(input()));
	});

	it('shows a matched-on line only for a non-title match', async () => {
		items = [
			item('g1', 'Tetris'),
			item('g3', 'Pinball', { matchedOn: { field: 'publisher', value: 'Nintendo' } })
		];
		render(GlobalSearch, { open: true });

		await typeAndWait('nintendo');

		const options = screen.getAllByRole('option', { hidden: true });
		expect(options[1].textContent).toContain('Publisher: Nintendo');
		expect(options[0].textContent).not.toMatch(/Developer:|Publisher:|Genre:/);
	});

	it('uses a static backdrop with Escape enabled (M1)', () => {
		render(GlobalSearch);
		expect(shown.options.at(-1)).toEqual({ backdrop: 'static', keyboard: true });
	});

	it('releases <body> before disposing on unmount (G1)', () => {
		const { unmount } = render(GlobalSearch, { open: true });
		shown.calls.length = 0;
		unmount();
		expect(shown.calls).toEqual(['releaseModal', 'dispose']);
	});

	it('a shortcut while open just refocuses the input', async () => {
		render(GlobalSearch, { open: true });
		await vi.waitFor(() => expect(shown.count).toBe(1));
		input().blur();

		press({ ctrlKey: true, key: 'k' });

		expect(document.activeElement).toBe(input());
		expect(shown.count).toBe(1);
	});

	it('shortcuts do nothing while another modal is open', () => {
		const other = document.body.appendChild(document.createElement('div'));
		other.className = 'modal show';
		render(GlobalSearch, { open: false });

		const event = press({ ctrlKey: true, key: 'k' });

		expect(event.defaultPrevented).toBe(false);
		expect(shown.count).toBe(0);
	});

	it('shortcuts are off and the navbar button is hidden while the password must be changed', () => {
		const me = { id: 1, username: 'plain', roles: [] as Role[], mustChangePassword: true };
		auth.set(me);
		render(GlobalSearch, { open: false });
		render(Navbar, { me, reviewCount: null, onlogout: () => {}, onsearch: () => {} });

		expect(press({ ctrlKey: true, key: 'k' }).defaultPrevented).toBe(false);
		expect(shown.count).toBe(0);
		expect(screen.queryByRole('button', { name: /Search/ })).toBeNull();
	});

	it('the navbar Search button asks to open it', async () => {
		const onsearch = vi.fn();
		render(Navbar, {
			me: { id: 1, username: 'plain', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {},
			onsearch
		});

		await fireEvent.click(screen.getByRole('button', { name: /Search/ }));

		expect(onsearch).toHaveBeenCalledOnce();
	});

	it('under 2 characters shows the hint and sends nothing', async () => {
		render(GlobalSearch, { open: true });

		await type(' t ');
		await vi.advanceTimersByTimeAsync(500);

		expect(screen.getByText('Type at least 2 characters')).toBeTruthy();
		expect(requested).toEqual([]);
	});

	it('debounces 250 ms, asks for 5 collections and the top 10 games by relevance, aborts the older requests (B27)', async () => {
		render(GlobalSearch, { open: true });

		await type('tet');
		await vi.advanceTimersByTimeAsync(100);
		expect(requested).toEqual([]);
		await vi.advanceTimersByTimeAsync(150);
		await type('tetris');
		await vi.advanceTimersByTimeAsync(250);

		expect(requested).toEqual([
			'/api/games?q=tet&sort=relevance&pageSize=10',
			'/api/collections?q=tet&pageSize=5',
			'/api/games?q=tetris&sort=relevance&pageSize=10',
			'/api/collections?q=tetris&pageSize=5'
		]);
		expect(aborted).toContain('/api/games?q=tet&sort=relevance&pageSize=10');
		expect(aborted).toContain('/api/collections?q=tet&pageSize=5');
	});

	it('results show title, system badge, year, box art or placeholder and the read-only rating', async () => {
		render(GlobalSearch, { open: true });

		await typeAndWait('tetris');

		const [first, second] = screen.getAllByRole('option', { hidden: true });
		expect(first.textContent).toContain('Tetris');
		expect(first.querySelector('.badge')!.textContent).toBe('Game Boy');
		expect(first.textContent).toContain('1989');
		expect(first.querySelector('img')!.getAttribute('src')).toBe('/api/games/g1/images/1');
		expect(first.textContent).toContain('Rated 4 of 5');
		expect(second.querySelector('img')).toBeNull();
		expect(second.querySelector('.bi-controller')).toBeTruthy();
		expect(second.textContent).toContain('Favourite');
		expect(first.querySelector('button')).toBeNull();
	});

	it('Up/Down move the highlight, Enter opens it, navigates and closes', async () => {
		render(GlobalSearch, { open: true });
		await typeAndWait('tetris');
		expect(input().getAttribute('aria-activedescendant')).toBe('global-search-results-0');

		await fireEvent.keyDown(input(), { key: 'ArrowDown' });
		expect(input().getAttribute('aria-activedescendant')).toBe('global-search-results-1');
		await fireEvent.keyDown(input(), { key: 'ArrowDown' });
		await fireEvent.keyDown(input(), { key: 'ArrowUp' });
		expect(screen.getAllByRole('option', { hidden: true })[1].getAttribute('aria-selected')).toBe(
			'true'
		);
		await fireEvent.keyDown(input(), { key: 'Enter' });

		expect(goto).toHaveBeenCalledWith('/games/g2');
		expect(shown.hidden).toBeGreaterThan(0);
		expect(input().value).toBe('');
	});

	it('Enter opens the first result by default; clicking a result opens it', async () => {
		const { unmount } = render(GlobalSearch, { open: true });
		await typeAndWait('tetris');
		await fireEvent.keyDown(input(), { key: 'Enter' });
		expect(goto).toHaveBeenLastCalledWith('/games/g1');
		unmount();

		render(GlobalSearch, { open: true });
		await typeAndWait('tetris');
		await fireEvent.click(screen.getAllByRole('option', { hidden: true })[1]);
		expect(goto).toHaveBeenLastCalledWith('/games/g2');
	});

	it('no results and errors show inline, the modal stays open', async () => {
		items = [];
		render(GlobalSearch, { open: true });
		await type('zzz');
		await vi.advanceTimersByTimeAsync(250);
		await vi.waitFor(() => expect(screen.getByText('No games match')).toBeTruthy());

		fail = true;
		await type('zzzz');
		await vi.advanceTimersByTimeAsync(250);
		await vi.waitFor(() => expect(screen.getByText('Search is down')).toBeTruthy());
		expect(shown.hidden).toBe(0);
	});

	it('closing returns focus to the element that had it', async () => {
		const trigger = document.body.appendChild(document.createElement('button'));
		trigger.focus();
		render(GlobalSearch, { open: false });
		press({ ctrlKey: true, key: 'k' });
		await vi.waitFor(() => expect(document.activeElement).toBe(input()));

		await fireEvent.click(screen.getByRole('button', { name: 'Close', hidden: true }));

		expect(document.activeElement).toBe(trigger);
	});

	describe('collections group (B27)', () => {
		const collection = (id: string, name: string, extra = {}) => ({
			id,
			name,
			gameCount: 3,
			coverUrl: null as string | null,
			updatedAt: '',
			...extra
		});

		beforeEach(() => {
			collections = [
				collection('c1', 'Tetris family', { coverUrl: '/api/collections/c1/cover?v=1' }),
				collection('c2', 'Puzzles', { gameCount: 1 })
			];
		});

		const options = () => screen.getAllByRole('option', { hidden: true });

		it('shows Collections first, then Games, with cover, name and game count', async () => {
			render(GlobalSearch, { open: true });
			await typeAndWait('tetris');

			const listbox = screen.getByRole('listbox', { hidden: true });
			const headings = [...listbox.querySelectorAll('[role="presentation"]')].map((h) =>
				h.textContent?.trim()
			);
			expect(headings).toEqual(['Collections', 'Games']);
			expect(options().map((o) => o.querySelector('.fw-semibold')?.textContent)).toEqual([
				'Tetris family',
				'Puzzles',
				'Tetris',
				'Tetris 2'
			]);
			expect(options()[0].querySelector('img')!.getAttribute('src')).toBe(
				'/api/collections/c1/cover?v=1'
			);
			expect(options()[0].textContent).toContain('3 games');
			expect(options()[1].textContent).toContain('1 game');
			expect(options()[1].querySelector('.bi-bookmarks')).toBeTruthy();
		});

		it('arrow keys cross from collections into games and wrap around', async () => {
			render(GlobalSearch, { open: true });
			await typeAndWait('tetris');

			await fireEvent.keyDown(input(), { key: 'ArrowDown' });
			await fireEvent.keyDown(input(), { key: 'ArrowDown' });
			expect(input().getAttribute('aria-activedescendant')).toBe('global-search-results-2');
			expect(options()[2].textContent).toContain('Tetris');
			await fireEvent.keyDown(input(), { key: 'ArrowUp' });
			await fireEvent.keyDown(input(), { key: 'ArrowUp' });
			await fireEvent.keyDown(input(), { key: 'ArrowUp' });
			expect(input().getAttribute('aria-activedescendant')).toBe('global-search-results-3');

			await fireEvent.keyDown(input(), { key: 'Enter' });
			expect(goto).toHaveBeenLastCalledWith('/games/g2');
		});

		it('Enter on a collection opens it', async () => {
			render(GlobalSearch, { open: true });
			await typeAndWait('tetris');

			await fireEvent.keyDown(input(), { key: 'Enter' });

			expect(goto).toHaveBeenLastCalledWith('/collections/c1');
			expect(shown.hidden).toBeGreaterThan(0);
		});

		it('a failing collections request shows its error and keeps the games', async () => {
			failCollections = true;
			render(GlobalSearch, { open: true });
			await typeAndWait('tetris');

			expect(screen.getByText('Collections are down')).toBeTruthy();
			expect(options().map((o) => o.querySelector('.fw-semibold')?.textContent)).toEqual([
				'Tetris',
				'Tetris 2'
			]);
		});

		it('a failing games request shows its error and keeps the collections', async () => {
			fail = true;
			render(GlobalSearch, { open: true });
			await typeAndWait('tetris');

			expect(screen.getByText('Search is down')).toBeTruthy();
			expect(options().map((o) => o.querySelector('.fw-semibold')?.textContent)).toEqual([
				'Tetris family',
				'Puzzles'
			]);
		});

		it('only collections match: no Games heading, no "no results" text', async () => {
			items = [];
			render(GlobalSearch, { open: true });
			await typeAndWait('tetris');

			expect(screen.getByRole('listbox', { hidden: true }).textContent).not.toContain('Games');
			expect(screen.queryByText('No games match')).toBeNull();
		});
	});
});
