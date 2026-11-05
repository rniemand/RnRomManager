import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import { auth } from '../stores/auth.svelte';

const goto = vi.fn();
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({ page: { url: new URL('http://localhost/') } }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

// jsdom has no layout/transitions for Bootstrap's Modal; record show/hide instead.
const shown = { count: 0, hidden: 0 };
vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {
			shown.count++;
		}
		hide() {
			shown.hidden++;
		}
		dispose() {}
	}
}));

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
	...extra
});

describe('GlobalSearch', () => {
	let requested: string[];
	let aborted: string[];
	let items: unknown[];
	let fail: boolean;

	beforeEach(() => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		resetClient();
		goto.mockClear();
		shown.count = 0;
		shown.hidden = 0;
		requested = [];
		aborted = [];
		fail = false;
		items = [
			item('g1', 'Tetris', { releaseYear: 1989, boxArtUrl: '/api/games/g1/images/1', rating: 4 }),
			item('g2', 'Tetris 2', { favourite: true })
		];
		auth.set({ id: 1, username: 'plain', role: 'User', mustChangePassword: false });
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			requested.push(url);
			init?.signal?.addEventListener('abort', () => aborted.push(url));
			const body = fail
				? { code: 'HttpError', title: 'Search is down' }
				: { items, page: 1, pageSize: 5, total: items.length };
			return Promise.resolve(new Response(JSON.stringify(body), { status: fail ? 500 : 200 }));
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
		const me = { id: 1, username: 'plain', role: 'User' as const, mustChangePassword: true };
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
			me: { id: 1, username: 'plain', role: 'User', mustChangePassword: false },
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

	it('debounces 250 ms, asks for the top 5 by relevance and aborts the older request', async () => {
		render(GlobalSearch, { open: true });

		await type('tet');
		await vi.advanceTimersByTimeAsync(100);
		expect(requested).toEqual([]);
		await vi.advanceTimersByTimeAsync(150);
		await type('tetris');
		await vi.advanceTimersByTimeAsync(250);

		expect(requested).toEqual([
			'/api/games?q=tet&sort=relevance&pageSize=5',
			'/api/games?q=tetris&sort=relevance&pageSize=5'
		]);
		expect(aborted).toContain('/api/games?q=tet&sort=relevance&pageSize=5');
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
});
