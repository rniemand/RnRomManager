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
	{
		id: 'gb',
		displayName: 'Game Boy',
		shortName: 'GB',
		gameCount: 1,
		romCount: 1,
		regions: ['USA']
	}
];

/** The FilterDropdown whose toggle reads `<field>: …` (L1). */
const dropdown = (field: string) =>
	screen
		.getByRole('button', { name: new RegExp(`^${field}:`) })
		.closest('.dropdown') as HTMLElement;
const items = (field: string) => [
	...dropdown(field).querySelectorAll<HTMLButtonElement>('.dropdown-item')
];
/** Each row as `label count` (the label includes a system badge's short name). */
const optionTexts = (field: string) =>
	items(field).map((b) => [...b.children].map((c) => c.textContent!.trim()).join(' '));
const choose = (field: string, label: string) =>
	fireEvent.click(
		items(field).find((b) => b.firstElementChild!.textContent!.trim().endsWith(label))!
	);
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

		await choose('Letter', 'T');

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
		await choose('Sort', 'Year');
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

	it('filter options show their counts once the facets load (F3)', async () => {
		render(LibraryPage);

		await vi.waitFor(() =>
			expect(optionTexts('System')).toEqual(['All systems 12', 'GBGame Boy 12'])
		);
		expect(optionTexts('Region')).toEqual(['All regions', 'USA 9']);
		expect(optionTexts('Letter').slice(0, 4)).toEqual(['All letters 3', '# 0', 'A 3', 'B 0']);
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
				await vi.waitFor(() =>
					expect(optionTexts('System')).toEqual(['All systems', 'GBGame Boy'])
				);
				expect(optionTexts('Letter').slice(0, 3)).toEqual(['All letters', '#', 'A']);
			} finally {
				facets = loaded;
			}
		});
	}

	describe('toolbar (L1)', () => {
		it('search, System, Region, Letter, Sort, direction, Favourites, then Grid/List', async () => {
			navigateTo('/');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');

			const form = screen.getByRole('search');
			expect(form.className).toBe('d-flex flex-wrap align-items-center gap-2 mb-2');
			const labels = [...form.children].map(
				(c) =>
					c.getAttribute('aria-label') ??
					c.querySelector('.dropdown-toggle')?.getAttribute('aria-label') ??
					c.id ??
					c.textContent
			);
			expect(labels).toEqual([
				'',
				'q',
				'System: All',
				'Region: All',
				'Letter: All',
				'Sort: Title',
				'Ascending, switch to descending',
				'Favourites',
				'View'
			]);
			const search = screen.getByLabelText('Search') as HTMLInputElement;
			expect(search.id).toBe('q');
			expect(search.placeholder).toBe('At least 2 characters');
			expect(document.querySelector('label[for="q"]')!.className).toBe('visually-hidden');
			expect(search.classList.contains('flex-grow-1')).toBe(true);
		});

		it('choosing a System clears the Region', async () => {
			navigateTo('/?region=USA&page=2');
			render(LibraryPage);
			await vi.waitFor(() => expect(optionTexts('System')).toHaveLength(2));

			await choose('System', 'Game Boy');

			expect(goto).toHaveBeenCalledWith('/?system=gb', { reset: false });
		});

		it('a Sort change resets the direction', async () => {
			navigateTo('/?sort=year&dir=desc');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');
			expect(screen.getByRole('button', { name: 'Sort: Year' }).className).toMatch(
				/\bbtn-secondary\b/
			);

			await choose('Sort', 'Rating');

			expect(goto).toHaveBeenCalledWith('/?sort=rating', { reset: false });
		});

		it('the default sort is outlined; Sort has no counts and no All row', async () => {
			navigateTo('/');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');

			expect(screen.getByRole('button', { name: 'Sort: Title' }).className).toContain(
				'btn-outline-secondary'
			);
			expect(optionTexts('Sort')).toEqual(['Title', 'Year', 'Date added', 'Rating']);
		});

		it('a region from the URL missing from the list is still listed', async () => {
			navigateTo('/?region=Mars');
			render(LibraryPage);
			await vi.waitFor(() => expect(optionTexts('Region')).toHaveLength(3));

			expect(optionTexts('Region')).toEqual(['All regions', 'USA 9', 'Mars 0']);
			expect(screen.getByRole('button', { name: 'Region: Mars' })).toBeTruthy();
		});
	});

	describe('active filters (L6)', () => {
		const row = () =>
			document.querySelector('[data-testid="active-filters"]') as HTMLElement | null;
		const chipTexts = () =>
			[...row()!.querySelectorAll('.badge.rounded-pill')].map((c) => c.textContent!.trim());

		it('lists System, Region, Letter, Search, Favourites in order with facet counts', async () => {
			facets = {
				systems: [{ id: 'gb', count: 1234 }],
				regions: [{ region: 'USA', count: 9 }],
				letters: [{ letter: 'A', count: 3 }]
			};
			navigateTo('/?favourite=true&q=zelda&letter=A&region=USA&system=gb');
			render(LibraryPage);

			await vi.waitFor(() =>
				expect(chipTexts()).toEqual([
					'System: Game Boy (1,234)',
					'Region: USA (9)',
					'Letter: A (3)',
					'Search: "zelda"',
					'Favourites'
				])
			);
			expect(
				[...row()!.querySelectorAll('.btn-close')].map((b) => b.getAttribute('aria-label'))
			).toEqual([
				'Remove System filter',
				'Remove Region filter',
				'Remove Letter filter',
				'Remove Search filter',
				'Remove Favourites filter'
			]);
			facets = {
				systems: [{ id: 'gb', count: 12 }],
				regions: [{ region: 'USA', count: 9 }],
				letters: [{ letter: 'A', count: 3 }]
			};
		});

		it('chips leave out counts while the facets load', async () => {
			const loaded = facets;
			facets = 'pending';
			try {
				navigateTo('/?system=gb&letter=A');
				render(LibraryPage);
				await screen.findByAltText('4-in-1 Fun Pak box art');
				await vi.waitFor(() => expect(chipTexts()).toEqual(['System: Game Boy', 'Letter: A']));
			} finally {
				facets = loaded;
			}
		});

		it.each([
			['System', '/?letter=A&q=zelda&favourite=true'],
			['Region', '/?system=gb&letter=A&q=zelda&favourite=true'],
			['Letter', '/?system=gb&region=USA&q=zelda&favourite=true'],
			['Search', '/?system=gb&letter=A&region=USA&favourite=true'],
			['Favourites', '/?system=gb&letter=A&region=USA&q=zelda']
		])('× on %s clears just that filter (System also clears Region)', async (field, expected) => {
			navigateTo('/?system=gb&region=USA&letter=A&q=zelda&favourite=true&page=3');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');

			await fireEvent.click(screen.getByRole('button', { name: `Remove ${field} filter` }));

			expect(goto).toHaveBeenCalledWith(expected, { reset: false });
		});

		it('Reset filters clears every filter, sort, direction and page, and the saved filter', async () => {
			navigateTo('/?system=gb&sort=year&dir=desc&page=2');
			render(LibraryPage);
			await vi.waitFor(() => expect(store.get('rnrm.library.filter')).toBeTruthy());

			await fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));

			expect(goto).toHaveBeenCalledWith('/', { reset: false });
			expect(store.has('rnrm.library.filter')).toBe(false);
		});

		it('is hidden at the defaults', async () => {
			navigateTo('/');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');

			expect(row()).toBeNull();
			expect(screen.queryByRole('button', { name: 'Reset filters' })).toBeNull();
		});

		it('a non-default sort alone shows Reset without chips', async () => {
			navigateTo('/?sort=rating');
			render(LibraryPage);
			await screen.findByAltText('4-in-1 Fun Pak box art');

			expect(chipTexts()).toEqual([]);
			expect(screen.getByRole('button', { name: 'Reset filters' })).toBeTruthy();
		});
	});

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
