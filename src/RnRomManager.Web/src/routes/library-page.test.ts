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
			boxArtUrl: '/api/games/g1/images/7'
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
});
