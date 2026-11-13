import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { navigateTo, testUrl as url } from '#lib/testing/url.svelte.js';

const goto = vi.fn();

vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		get url() {
			return url.current;
		}
	}
}));

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const tooltips: { node: Element; text: string }[] = [];
vi.mock('#lib/tooltip.js', () => ({
	tooltip: (node: Element, text: string) => {
		tooltips.push({ node, text });
		return {};
	}
}));

const { default: LibraryPage } = await import('./+page.svelte');

const systems = [
	{ id: 'gb', displayName: 'Game Boy', gameCount: 1, romCount: 1, regions: ['USA'] }
];
let facets: unknown = {
	systems: [{ id: 'gb', count: 12 }],
	regions: [{ region: 'USA', count: 9 }],
	letters: [{ letter: 'A', count: 3 }]
};
const games = {
	items: [
		{
			id: 'g1',
			system: 'gb',
			displayTitle: '4-in-1 Fun Pak',
			releaseYear: 1992,
			regions: ['USA'],
			romCount: 1,
			primarySizeBytes: 131072,
			boxArtUrl: '/api/games/g1/images/7',
			systemName: 'Game Boy',
			rating: null,
			favourite: false
		}
	],
	page: 1,
	pageSize: 48,
	total: 1
};

describe('library page', () => {
	let requested: string[];
	let store: Map<string, string>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		url.current = new URL('http://localhost/?system=gb&letter=%23');
		tooltips.length = 0;
		requested = [];
		// Isolate saved views between tests, independent of Node's localStorage implementation.
		store = new Map();
		vi.stubGlobal('localStorage', {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value),
			removeItem: (key: string) => void store.delete(key)
		});
		vi.stubGlobal('fetch', (input: string) => {
			requested.push(input);
			if (input.startsWith('/api/games/facets'))
				return facets === 'pending'
					? new Promise(() => {})
					: Promise.resolve(
							new Response(JSON.stringify(facets ?? {}), { status: facets ? 200 : 500 })
						);
			const body = input === '/api/systems' ? systems : games;
			return Promise.resolve(new Response(JSON.stringify(body), { status: 200 }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	it('loads games for the URL filters and shows box art with alt text', async () => {
		render(LibraryPage);

		expect(await screen.findByAltText('4-in-1 Fun Pak box art')).toBeTruthy();
		expect(requested).toContain('/api/games?system=gb&letter=%23&pageSize=48');
	});

	it('changing a filter writes it to the URL and resets the page', async () => {
		url.current = new URL('http://localhost/?system=gb&page=3');
		render(LibraryPage);
		await screen.findByAltText('4-in-1 Fun Pak box art');

		await fireEvent.change(screen.getByLabelText('Letter'), { target: { value: 'T' } });

		expect(goto).toHaveBeenCalledWith('/?system=gb&letter=T', { reset: false });
	});
	it('a search still pending when the page closes does not navigate', async () => {
		url.current = new URL('http://localhost/');
		const { unmount } = render(LibraryPage);
		await screen.findByAltText('4-in-1 Fun Pak box art');
		vi.useFakeTimers();
		try {
			await fireEvent.input(screen.getByLabelText('Search'), { target: { value: 'fun' } });
			unmount();
			await vi.advanceTimersByTimeAsync(500);
			expect(goto).not.toHaveBeenCalled();
		} finally {
			vi.useRealTimers();
		}
	});

	it('grid card: square contained box art, system badge, one-line title with a tooltip, link to the game', async () => {
		url.current = new URL('http://localhost/');
		render(LibraryPage);

		const art = await screen.findByAltText('4-in-1 Fun Pak box art');
		expect(art.classList.contains('object-fit-cover')).toBe(true);
		expect(art.closest('.card')!.classList.contains('game-card')).toBe(true);
		expect(art.closest('.ratio-1x1')).toBeTruthy();
		const badge = document.querySelector('.card .badge')!;
		expect(badge.querySelector('[aria-hidden="true"]')!.textContent).toBe('GB');
		expect(badge.classList.contains('text-bg-success')).toBe(true);
		expect(badge.querySelector('.visually-hidden')!.textContent).toBe('Game Boy');
		const link = screen.getByRole('link', { name: '4-in-1 Fun Pak' });
		expect(link.getAttribute('href')).toBe('/games/g1');
		expect(link.classList.contains('text-truncate')).toBe(true);
		expect(link.classList.contains('stretched-link')).toBe(true);
		expect(tooltips).toEqual([{ node: link, text: '4-in-1 Fun Pak' }]);
		const rate = screen.getByRole('button', { name: 'Rate 3 of 5' });
		expect(rate.closest('.z-2')).toBeTruthy();
		expect(link.contains(rate)).toBe(false);
	});

	it('Favourites toggle writes favourite=true and resets the page', async () => {
		url.current = new URL('http://localhost/?system=gb&page=3');
		render(LibraryPage);
		await screen.findByAltText('4-in-1 Fun Pak box art');

		const toggle = screen.getByRole('button', { name: 'Favourites' });
		expect(toggle.getAttribute('aria-pressed')).toBe('false');
		await fireEvent.click(toggle);

		expect(goto).toHaveBeenCalledWith('/?system=gb&favourite=true', { reset: false });
	});

	it('Rating sort writes sort=rating and the API call carries it', async () => {
		url.current = new URL('http://localhost/?favourite=true&sort=rating');
		render(LibraryPage);
		await screen.findByAltText('4-in-1 Fun Pak box art');

		expect(requested).toContain('/api/games?sort=rating&favourite=true&pageSize=48');
		expect(screen.getByRole('button', { name: 'Favourites' }).getAttribute('aria-pressed')).toBe(
			'true'
		);
		await fireEvent.change(screen.getByLabelText('Sort by'), { target: { value: 'year' } });
		expect(goto).toHaveBeenCalledWith('/?sort=year&favourite=true', { reset: false });
	});

	it('list view shows the full system name badge and the rating control', async () => {
		url.current = new URL('http://localhost/');
		render(LibraryPage);
		await screen.findByAltText('4-in-1 Fun Pak box art');

		await fireEvent.click(screen.getByRole('button', { name: 'List view' }));

		expect(store.get('libraryView')).toBe('list');
		expect(screen.getByRole('columnheader', { name: 'Rating' })).toBeTruthy();
		const row = screen.getByRole('row', { name: /4-in-1 Fun Pak/ });
		expect(row.querySelector('.badge')!.textContent!.trim()).toBe('Game Boy');
		expect(row.querySelector('.badge')!.classList.contains('text-bg-success')).toBe(true);
		expect(row.querySelector('[aria-label="Rate 1 of 5"]')).toBeTruthy();
	});

	describe('saved filter (L7)', () => {
		const savedKey = 'rnrm.library.filter';
		const replace = { replace: true, reset: false };

		it.each(['/', '/?view=list'])('%s on mount restores the saved filter', async (path) => {
			navigateTo(path);
			store.set(savedKey, '?system=gb&page=2');
			render(LibraryPage);

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/?system=gb&page=2', replace));
		});

		it('an explicit URL wins and is saved', async () => {
			navigateTo('/?region=USA');
			store.set(savedKey, '?system=gb');
			render(LibraryPage);

			await vi.waitFor(() => expect(store.get(savedKey)).toBe('?region=USA'));
			expect(goto).not.toHaveBeenCalled();
		});

		it('a bare URL with nothing saved stays put and saves nothing', async () => {
			navigateTo('/');
			render(LibraryPage);

			await screen.findByAltText('4-in-1 Fun Pak box art');
			expect(goto).not.toHaveBeenCalled();
			expect(store.has(savedKey)).toBe(false);
		});

		it('going to bare / while mounted clears the saved filter', async () => {
			navigateTo('/?system=gb');
			render(LibraryPage);
			await vi.waitFor(() => expect(store.get(savedKey)).toBe('?system=gb'));

			navigateTo('/');

			await vi.waitFor(() => expect(store.has(savedKey)).toBe(false));
			expect(goto).not.toHaveBeenCalled();
		});

		it('a saved bad sort, dir, letter or page falls back to the defaults', async () => {
			navigateTo('/');
			store.set(savedKey, '?system=gb&sort=nope&dir=up&letter=%3F&page=-3');
			render(LibraryPage);

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/?system=gb', replace));
			expect(store.get(savedKey)).toBe('?system=gb');
		});

		it('every later filter change is saved', async () => {
			goto.mockImplementation((path: string) => navigateTo(path));
			navigateTo('/?system=gb');
			render(LibraryPage);

			await fireEvent.click(await screen.findByRole('button', { name: /Favourites/ }));

			await vi.waitFor(() => expect(store.get(savedKey)).toBe('?system=gb&favourite=true'));
			goto.mockReset();
		});
	});

	it('restores a saved list view on mount', async () => {
		store.set('libraryView', 'list');
		render(LibraryPage);

		expect(await screen.findByRole('columnheader', { name: 'Size' })).toBeTruthy();
		expect(screen.queryByAltText('4-in-1 Fun Pak box art')).toBeNull();
	});

	it('shows the primary release size in the grid card and the list Size column (LP1)', async () => {
		url.current = new URL('http://localhost/');
		render(LibraryPage);
		await screen.findByAltText('4-in-1 Fun Pak box art');
		const badge = document.querySelector('.card .badge.position-absolute.top-0.end-0')!;
		expect(badge.querySelector('.file-size')!.getAttribute('title')).toBe(
			`${(131072).toLocaleString()} bytes`
		);
		// B5: the size is a badge only; the body line has year and regions.
		expect(document.querySelector('.card .card-body .file-size')).toBeNull();

		await fireEvent.click(screen.getByRole('button', { name: 'List view' }));

		expect(screen.getByRole('columnheader', { name: 'Size' })).toBeTruthy();
		const row = screen.getByRole('row', { name: /4-in-1 Fun Pak/ });
		expect(row.querySelector('.file-size')!.textContent).toBe('128.0 KiB');
	});

	const optionTexts = (label: string) =>
		[...(screen.getByLabelText(label) as HTMLSelectElement).options].map((o) =>
			o.textContent!.trim()
		);

	it('filter options show their counts once the facets load (F3)', async () => {
		render(LibraryPage);

		await vi.waitFor(() => expect(optionTexts('System')).toEqual(['All systems', 'Game Boy (12)']));
		expect(optionTexts('Region')).toEqual(['Any region', 'USA (9)']);
		expect(optionTexts('Letter').slice(0, 4)).toEqual(['All', '# (0)', 'A (3)', 'B (0)']);
		expect(requested).toContain('/api/games/facets?system=gb&letter=%23');
	});

	for (const [state, value] of [
		['pending', 'pending'],
		['failed', null]
	] as const) {
		it(`filter options have no counts while the facets are ${state} (F3)`, async () => {
			const loaded = facets;
			facets = value;
			try {
				render(LibraryPage);
				await screen.findByAltText('4-in-1 Fun Pak box art');
				await vi.waitFor(() => expect(optionTexts('System')).toEqual(['All systems', 'Game Boy']));
				expect(optionTexts('Letter').slice(0, 3)).toEqual(['All', '#', 'A']);
			} finally {
				facets = loaded;
			}
		});
	}

	it('an unknown system gets the neutral callout colour (L4)', async () => {
		games.items[0].system = 'sg1000';
		try {
			url.current = new URL('http://localhost/');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');
			expect(document.querySelector('.card .badge')!.classList.contains('text-bg-secondary')).toBe(
				true
			);
		} finally {
			games.items[0].system = 'gb';
		}
	});

	it('a game without a primary size has no size badge (B5)', async () => {
		games.items[0].primarySizeBytes = null as unknown as number;
		try {
			url.current = new URL('http://localhost/');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');
			expect(document.querySelector('.card .badge.end-0')).toBeNull();
		} finally {
			games.items[0].primarySizeBytes = 131072;
		}
	});
});
