import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

vi.mock('$app/navigation', () => ({ goto: (url: string) => navigateTo(url) }));
vi.mock('$app/state', () => ({
	page: {
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

const { default: LibraryPage } = await import('./+page.svelte');

const game = (id: string, displayTitle: string) => ({
	id,
	system: 'gb',
	displayTitle,
	releaseYear: null,
	regions: [],
	romCount: 1,
	primarySizeBytes: null,
	boxArtUrl: null,
	systemName: 'Game Boy',
	rating: null,
	favourite: false
});

describe('Library bulk add to a collection (B26)', () => {
	let calls: { key: string; body?: string }[];

	beforeEach(() => {
		resetClient();
		navigateTo('/');
		toasts.items.length = 0;
		calls = [];
		vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {} });
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url !== '/api/auth/antiforgery') calls.push({ key, body: init?.body as string });
			let body: unknown = null;
			if (url === '/api/systems') body = [];
			else if (url.startsWith('/api/games?'))
				body = {
					items: [game('g1', 'Alpha'), game('g2', 'Bravo'), game('g3', 'Charlie')],
					page: 1,
					pageSize: 48,
					total: 100
				};
			else if (url.startsWith('/api/collections?'))
				body = {
					items: [{ id: 'c1', name: 'Mine', gameCount: 0, coverUrl: null, updatedAt: '' }],
					page: 1,
					pageSize: 200,
					total: 1
				};
			else if (key === 'POST /api/collections/c1/games') body = { added: 2 };
			return Promise.resolve(
				new Response(body === null ? null : JSON.stringify(body), {
					status: body === null ? 204 : 200
				})
			);
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	const box = (title: string) =>
		screen.getByRole('checkbox', { name: `Select ${title}` }) as HTMLInputElement;
	const addButton = () =>
		screen.getByRole('button', { name: /Add to collection/ }) as HTMLButtonElement;

	async function startSelecting(): Promise<void> {
		render(LibraryPage);
		await screen.findByRole('link', { name: 'Alpha' });
		await fireEvent.click(screen.getByRole('button', { name: /Select$/ }));
	}

	it('Select shows checkboxes; Add is enabled once one is ticked', async () => {
		render(LibraryPage);
		await screen.findByRole('link', { name: 'Alpha' });
		expect(screen.queryByRole('checkbox', { name: 'Select Alpha' })).toBeNull();

		await fireEvent.click(screen.getByRole('button', { name: /Select$/ }));
		expect(addButton().disabled).toBe(true);
		await fireEvent.click(box('Alpha'));

		expect(addButton().disabled).toBe(false);
		expect(screen.getByText('1 selected')).toBeTruthy();
	});

	it('Select all on this page ticks every game on it', async () => {
		await startSelecting();

		await fireEvent.click(screen.getByRole('button', { name: 'Select all on this page' }));

		expect(['Alpha', 'Bravo', 'Charlie'].map((t) => box(t).checked)).toEqual([true, true, true]);
		expect(screen.getByText('3 selected')).toBeTruthy();
	});

	it.each([
		['the page', 'Next'],
		['a filter', 'Favourites']
	])('changing %s clears the selection', async (_, button) => {
		await startSelecting();
		await fireEvent.click(box('Alpha'));

		await fireEvent.click(screen.getByRole('button', { name: button }));

		await vi.waitFor(() => expect(screen.getByText('0 selected')).toBeTruthy());
		expect(addButton().disabled).toBe(true);
	});

	it('turning Select off clears the selection', async () => {
		await startSelecting();
		await fireEvent.click(box('Alpha'));

		await fireEvent.click(screen.getByRole('button', { name: /Select$/ }));
		await fireEvent.click(screen.getByRole('button', { name: /Select$/ }));

		expect(screen.getByText('0 selected')).toBeTruthy();
	});

	it('Add sends the selected ids to the chosen collection, toasts the added count and clears', async () => {
		await startSelecting();
		await fireEvent.click(box('Alpha'));
		await fireEvent.click(box('Charlie'));

		await fireEvent.click(addButton());
		const dialog = within(document.querySelector<HTMLElement>('.modal')!);
		expect(dialog.getByText('Add 2 games to a collection')).toBeTruthy();
		await fireEvent.click(await dialog.findByRole('radio', { name: /^Mine/, hidden: true }));
		await fireEvent.click(dialog.getByRole('button', { name: 'Add', hidden: true }));

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain('Added 2 games to Mine')
		);
		const post = calls.find((c) => c.key === 'POST /api/collections/c1/games')!;
		expect(JSON.parse(post.body!)).toEqual({ gameIds: ['g1', 'g3'] });
		expect(screen.getByText('0 selected')).toBeTruthy();
		expect(
			dialog
				.getByRole('link', { name: 'Create or manage collections', hidden: true })
				.getAttribute('href')
		).toBe('/collections');
	});
});
