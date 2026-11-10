import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { CollectionDetail } from '#lib/api/types.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

const goto = vi.fn<(url: string, opts?: unknown) => void>((url) => navigateTo(url));
vi.mock('$app/navigation', () => ({ goto: (url: string, opts?: unknown) => goto(url, opts) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: 'c1' },
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
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: CollectionPage } = await import('./[id]/+page.svelte');

type Answer = { status: number; body?: unknown };

const collection = (extra: Partial<CollectionDetail> = {}): CollectionDetail => ({
	id: 'c1',
	name: 'Pokémon',
	notes: '**Gotta** catch them',
	coverUrl: null,
	gameCount: 1,
	createdAt: '',
	updatedAt: '',
	...extra
});
const game = (id: string, displayTitle: string, system = 'gb') => ({
	id,
	system,
	displayTitle,
	releaseYear: null,
	regions: [],
	romCount: 1,
	systemName: system === 'gb' ? 'Game Boy' : 'Nintendo 64',
	systemShortName: system === 'gb' ? 'GB' : 'N64',
	rating: null,
	favourite: false,
	boxArtUrl: null,
	primarySizeBytes: null
});
const pageOf = (items: unknown[]) => ({ items, page: 1, pageSize: 48, total: items.length });

const defaultGames = 'GET /api/games?collection=c1&sort=added&pageSize=48';

let calls: string[];
let answers: Record<string, Answer[]>;

beforeEach(() => {
	resetClient();
	goto.mockClear();
	navigateTo('/collections/c1');
	toasts.items.length = 0;
	calls = [];
	answers = {
		'GET /api/systems': [{ status: 200, body: [] }],
		'GET /api/collections/c1': [{ status: 200, body: collection() }],
		[defaultGames]: [{ status: 200, body: pageOf([game('g1', 'Pokémon Red')]) }]
	};
	vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {} });
	vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
		const key = `${init?.method ?? 'GET'} ${url}`;
		calls.push(key);
		const answer = answers[key]?.shift() ?? { status: 204 };
		const payload = answer.body === undefined ? null : JSON.stringify(answer.body);
		return Promise.resolve(new Response(payload, { status: answer.status }));
	});
});
afterEach(() => {
	vi.useRealTimers();
	vi.unstubAllGlobals();
});

const again = (key: string, body: unknown) => (answers[key] ??= []).push({ status: 200, body });

describe('collection page (B24)', () => {
	it('loads the detail and its games, newest added first by default', async () => {
		render(CollectionPage);

		expect(await screen.findByRole('heading', { name: 'Pokémon' })).toBeTruthy();
		expect(screen.getByTestId('collection-notes').querySelector('strong')?.textContent).toBe(
			'Gotta'
		);
		expect(await screen.findByRole('link', { name: 'Pokémon Red' })).toBeTruthy();
		expect(calls).toContain(defaultGames);
		expect((screen.getByLabelText('Sort by') as HTMLSelectElement).value).toBe('added');
	});

	it('filters and sort changes go into the URL', async () => {
		render(CollectionPage);
		await screen.findByRole('link', { name: 'Pokémon Red' });

		await fireEvent.change(screen.getByLabelText('Sort by'), { target: { value: 'title' } });
		expect(goto).toHaveBeenLastCalledWith('/collections/c1?sort=title', { reset: false });

		await fireEvent.click(screen.getByRole('button', { name: 'Favourites' }));
		expect(goto).toHaveBeenLastCalledWith('/collections/c1?sort=title&favourite=true', {
			reset: false
		});
	});

	it('Remove calls the API and refreshes the list and the count', async () => {
		answers['DELETE /api/collections/c1/games/g1'] = [{ status: 204 }];
		again('GET /api/collections/c1', collection({ gameCount: 0 }));
		again(defaultGames, pageOf([]));
		render(CollectionPage);

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Remove Pokémon Red from collection' })
		);

		expect(await screen.findByText(/This collection has no games yet/)).toBeTruthy();
		expect(calls).toContain('DELETE /api/collections/c1/games/g1');
		expect(toasts.items.map((t) => t.text)).toContain('Removed Pokémon Red');
	});

	it.each([
		[1, 'Added Mario 64'],
		[0, 'Already in this collection']
	])('the typeahead adds the picked game (added %i → %s)', async (added, toast) => {
		answers['GET /api/games?q=mario&notInCollection=c1&pageSize=10'] = [
			{ status: 200, body: pageOf([game('g2', 'Mario 64', 'n64')]) }
		];
		answers['POST /api/collections/c1/games'] = [{ status: 200, body: { added } }];
		again('GET /api/collections/c1', collection({ gameCount: 1 + added }));
		again(defaultGames, pageOf([game('g1', 'Pokémon Red')]));
		render(CollectionPage);
		await screen.findByRole('link', { name: 'Pokémon Red' });

		await fireEvent.input(screen.getByLabelText('Add games'), { target: { value: 'mario' } });
		await fireEvent.click(
			await screen.findByRole('button', { name: /Mario 64/ }, { timeout: 2000 })
		);

		await vi.waitFor(() => expect(toasts.items.map((t) => t.text)).toContain(toast));
		expect(calls.filter((c) => c === defaultGames)).toHaveLength(2);
		expect((screen.getByLabelText('Add games') as HTMLInputElement).value).toBe('');
	});

	it('an unknown or foreign collection shows the not-found state', async () => {
		answers['GET /api/collections/c1'] = [{ status: 404, body: { status: 404, code: 'NotFound' } }];
		answers[defaultGames] = [{ status: 404, body: { status: 404, code: 'NotFound' } }];
		render(CollectionPage);

		expect(await screen.findByText("This collection doesn't exist.")).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Go to Collections' }).getAttribute('href')).toBe(
			'/collections'
		);
		expect(screen.queryByRole('alert')).toBeNull();
	});

	it('Delete confirms, deletes and goes to Collections with a toast', async () => {
		answers['DELETE /api/collections/c1'] = [{ status: 204 }];
		render(CollectionPage);
		await screen.findByRole('heading', { name: 'Pokémon' });

		await fireEvent.click(screen.getByRole('button', { name: /Delete/ }));
		const dialog = within(
			[...document.querySelectorAll<HTMLElement>('.modal')].find((m) =>
				m.textContent?.includes('Its games stay in the Library')
			)!
		);
		await fireEvent.click(dialog.getByRole('button', { name: 'Delete', hidden: true }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/collections', undefined));
		expect(calls).toContain('DELETE /api/collections/c1');
		expect(toasts.items.map((t) => t.text)).toContain('Deleted Pokémon.');
	});
});
