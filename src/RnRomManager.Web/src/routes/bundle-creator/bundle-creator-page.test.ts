import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { BundleEntry } from '#lib/api/types.js';
import { bundle } from '#lib/stores/bundle.svelte.js';

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

const { default: BundlePage } = await import('./+page.svelte');

const gameId = '8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
const entry = (n: number, fileName: string): BundleEntry => ({
	gameId,
	releaseSha1: n.toString(16).padStart(40, '0'),
	member: null,
	gameTitle: `Game ${n}`,
	fileName
});

describe('Bundle Creator page: the working cart (B1, B5)', () => {
	let store: Map<string, string>;
	let resolveBodies: unknown[];
	let answer: () => Response;

	beforeEach(() => {
		resetClient();
		store = new Map();
		resolveBodies = [];
		vi.stubGlobal('localStorage', {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value),
			removeItem: (key: string) => void store.delete(key)
		});
		answer = () =>
			new Response(
				JSON.stringify({
					items: [
						{ available: true, gameTitle: 'Game 1', fileName: 'one.gb', downloadUrl: '/d/1' },
						{ available: false, gameTitle: null, fileName: null, downloadUrl: null }
					]
				}),
				{ status: 200 }
			);
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (url === '/api/bundle-files/resolve') {
				resolveBodies.push(JSON.parse(String(init?.body)));
				return Promise.resolve(answer());
			}
			if (url === '/api/bundle-queues') return Promise.resolve(new Response('[]', { status: 200 }));
			return Promise.resolve(new Response(null, { status: 204 }));
		});
		bundle.follow({ id: 4, username: 'u', roles: [], mustChangePassword: false });
	});
	afterEach(() => {
		bundle.follow(null);
		vi.unstubAllGlobals();
	});

	const rows = () => screen.getAllByRole('row').slice(1);

	it('lists the files in cart order and refreshes availability on open', async () => {
		bundle.add(entry(1, 'one.gb'));
		bundle.add(entry(2, 'two.gb'));

		render(BundlePage);

		await waitFor(() => expect(screen.getByText('Unavailable')).toBeTruthy());
		expect(resolveBodies).toHaveLength(1);
		expect(rows().map((r) => within(r).getAllByRole('cell')[1].textContent)).toEqual([
			'one.gb',
			'two.gb'
		]);
		expect(within(rows()[0]).getByText('Available')).toBeTruthy();
		expect(within(rows()[1]).getByText('Game 2').closest('a')).toBeNull();
		expect(within(rows()[0]).getByRole('link', { name: 'Game 1' }).getAttribute('href')).toBe(
			`/games/${gameId}`
		);
		expect(screen.getByRole('status').textContent!.replace(/\s+/g, ' ')).toContain(
			'1 file is no longer in the library'
		);
	});

	it('refreshes once the account is known, as after a reload, and not on every cart change', async () => {
		bundle.add(entry(1, 'one.gb'));
		bundle.add(entry(2, 'two.gb'));
		bundle.follow(null);
		render(BundlePage);
		await Promise.resolve();
		expect(resolveBodies).toHaveLength(0);

		bundle.follow({ id: 4, username: 'u', roles: [], mustChangePassword: false });

		await waitFor(() => expect(screen.getByText('Unavailable')).toBeTruthy());
		expect(resolveBodies).toHaveLength(1);
		bundle.remove(bundle.items[1].key);
		await Promise.resolve();
		expect(resolveBodies).toHaveLength(1);
	});

	it('removes one file', async () => {
		bundle.add(entry(1, 'one.gb'));
		bundle.add(entry(2, 'two.gb'));
		render(BundlePage);

		await fireEvent.click(screen.getByRole('button', { name: 'Remove one.gb from the bundle' }));

		expect(bundle.entries.map((e) => e.fileName)).toEqual(['two.gb']);
		expect(rows()).toHaveLength(1);
	});

	it('clears the cart after confirmation', async () => {
		bundle.add(entry(1, 'one.gb'));
		bundle.add(entry(2, 'two.gb'));
		render(BundlePage);

		await fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
		expect(bundle.count).toBe(2);
		const dialog = [...document.querySelectorAll<HTMLElement>('.modal')].find((m) =>
			m.textContent?.includes('Clear the bundle?')
		)!;
		expect(dialog.textContent).toContain('This removes all 2 files');
		await fireEvent.click(within(dialog).getByRole('button', { name: 'Clear', hidden: true }));

		expect(bundle.count).toBe(0);
		expect(screen.getByText('The bundle is empty.')).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Clear' }).hasAttribute('disabled')).toBe(true);
	});

	it('refreshes on demand and says when the check failed', async () => {
		bundle.add(entry(1, 'one.gb'));
		render(BundlePage);
		await waitFor(() => expect(resolveBodies).toHaveLength(1));
		answer = () => new Response(null, { status: 500 });

		await waitFor(() =>
			expect(screen.getByRole('button', { name: 'Refresh' }).hasAttribute('disabled')).toBe(false)
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

		await waitFor(() => expect(screen.getByRole('alert').textContent).toContain("Couldn't check"));
		expect(resolveBodies).toHaveLength(2);
		expect(bundle.count).toBe(1);
	});

	it('shows an empty state without a request when the cart is empty', () => {
		render(BundlePage);

		expect(screen.getByText('The bundle is empty.')).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Browse the library' }).getAttribute('href')).toBe('/');
		expect(resolveBodies).toHaveLength(0);
	});
});
