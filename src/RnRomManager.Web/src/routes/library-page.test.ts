import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';

const goto = vi.fn();
const url = { current: new URL('http://localhost/?system=gb&letter=%23') };

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
const games = {
	items: [
		{
			id: 'g1',
			system: 'gb',
			displayTitle: '4-in-1 Fun Pak',
			releaseYear: 1992,
			regions: ['USA'],
			romCount: 1,
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

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		tooltips.length = 0;
		requested = [];
		vi.stubGlobal('fetch', (input: string) => {
			requested.push(input);
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
		expect(art.classList.contains('object-fit-contain')).toBe(true);
		expect(art.closest('.ratio-1x1')).toBeTruthy();
		const badge = document.querySelector('.card .badge')!;
		expect(badge.querySelector('[aria-hidden="true"]')!.textContent).toBe('GB');
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

		expect(screen.getByRole('columnheader', { name: 'Rating' })).toBeTruthy();
		const row = screen.getByRole('row', { name: /4-in-1 Fun Pak/ });
		expect(row.querySelector('.badge')!.textContent).toBe('Game Boy');
		expect(row.querySelector('[aria-label="Rate 1 of 5"]')).toBeTruthy();
	});
});
