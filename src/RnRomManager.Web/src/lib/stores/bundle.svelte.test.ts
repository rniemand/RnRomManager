import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from '../api/client';
import type { BundleEntry, Me } from '#lib/types/index.js';
import { bundle, cartStorageKey, cleanEntries, entryKey, maxBundleEntries } from './bundle.svelte';

const alice: Me = { id: 7, username: 'alice', roles: [], mustChangePassword: false };
const bob: Me = { id: 8, username: 'bob', roles: [], mustChangePassword: false };

const game = '8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
const sha = (n: number) => n.toString(16).padStart(40, '0');

const main = (n: number, fileName = `game ${n}.gb`): BundleEntry => ({
	gameId: game,
	releaseSha1: sha(n),
	member: null,
	gameTitle: `Game ${n}`,
	fileName
});

const setFile = (name: string, n = 1): BundleEntry => ({
	gameId: game,
	releaseSha1: sha(n),
	member: { sha1: 'f'.repeat(40), fileName: name },
	gameTitle: 'Disc Runner',
	fileName: name
});

describe('bundle cart store (B1–B3, B5)', () => {
	let resolve: ((body: unknown) => void) | null;
	let resolveBodies: unknown[];
	let fail: boolean;
	let store: Map<string, string>;

	beforeEach(() => {
		resetClient();
		store = new Map();
		vi.stubGlobal('localStorage', {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value),
			removeItem: (key: string) => void store.delete(key)
		});
		resolve = null;
		resolveBodies = [];
		fail = false;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (url === '/api/bundle-files/resolve') {
				resolveBodies.push(JSON.parse(String(init?.body)));
				if (fail) return Promise.resolve(new Response(null, { status: 500 }));
				return new Promise<Response>((done) => {
					resolve = (body) => done(new Response(JSON.stringify(body), { status: 200 }));
				});
			}
			return Promise.resolve(new Response(null, { status: 204 }));
		});
		bundle.follow(null);
	});
	afterEach(() => {
		bundle.follow(null);
		vi.unstubAllGlobals();
		vi.restoreAllMocks();
	});

	/** Lets the client's antiforgery fetch and the POST start. */
	const settle = () => new Promise((r) => setTimeout(r, 0));

	it('takes the cart another tab wrote, so the next add keeps both tabs’ entries', () => {
		bundle.follow(alice);
		bundle.add(main(1));
		// The other tab adds game 2 to its copy and stores it.
		store.set(
			cartStorageKey(alice.id),
			JSON.stringify({ version: 1, entries: [main(1), main(2)] })
		);
		window.dispatchEvent(new StorageEvent('storage', { key: cartStorageKey(alice.id) }));

		bundle.add(main(3));

		expect(bundle.entries.map((e) => e.fileName)).toEqual(['game 1.gb', 'game 2.gb', 'game 3.gb']);
		expect(JSON.parse(store.get(cartStorageKey(alice.id))!).entries).toHaveLength(3);
	});

	it('ignores storage events for another account’s cart', () => {
		bundle.follow(alice);
		bundle.add(main(1));
		store.set(cartStorageKey(bob.id), JSON.stringify({ version: 1, entries: [main(9)] }));
		window.dispatchEvent(new StorageEvent('storage', { key: cartStorageKey(bob.id) }));

		expect(bundle.entries.map((e) => e.fileName)).toEqual(['game 1.gb']);
	});

	it('keeps insertion order and rejects duplicate identities', () => {
		bundle.follow(alice);

		expect(bundle.add(main(2))).toBe('added');
		expect(bundle.add(setFile('Track 2.bin'))).toBe('added');
		expect(bundle.add(main(1))).toBe('added');
		expect(bundle.add({ ...main(2), fileName: 'relabelled.gb' })).toBe('duplicate');
		expect(bundle.add({ ...main(2), gameId: game.toUpperCase() })).toBe('duplicate');

		expect(bundle.entries.map((e) => e.fileName)).toEqual([
			'game 2.gb',
			'Track 2.bin',
			'game 1.gb'
		]);
		expect(bundle.count).toBe(3);
	});

	it('treats main and set files, and set-file names in another case, as different files', () => {
		bundle.follow(alice);

		expect(bundle.add(main(1))).toBe('added');
		expect(bundle.add(setFile('Track 2.bin'))).toBe('added');
		expect(bundle.add(setFile('track 2.bin'))).toBe('added');
		expect(bundle.add(main(3, 'game 1.gb'))).toBe('added');

		expect(new Set(bundle.entries.map(entryKey)).size).toBe(4);
		expect(bundle.has(setFile('Track 2.bin'))).toBe(true);
		expect(bundle.has(setFile('TRACK 2.bin'))).toBe(false);
	});

	it('holds at most 500 files', () => {
		bundle.follow(alice);
		for (let i = 0; i < maxBundleEntries; i++) expect(bundle.add(main(i))).toBe('added');

		expect(bundle.full).toBe(true);
		expect(bundle.add(main(9999))).toBe('full');
		expect(bundle.add(main(0))).toBe('duplicate');
		expect(bundle.count).toBe(maxBundleEntries);
	}, 20_000); // 500 adds: slow on a loaded machine (also on main)

	it('refuses adds while signed out and malformed entries', () => {
		expect(bundle.add(main(1))).toBe('signedOut');

		bundle.follow(alice);
		expect(bundle.add({ ...main(1), releaseSha1: 'NOPE' })).toBe('invalid');
		expect(bundle.add({ ...main(1), gameId: '00000000-0000-0000-0000-000000000000' })).toBe(
			'invalid'
		);
		expect(bundle.add({ ...main(1), gameTitle: '' })).toBe('invalid');
		expect(bundle.count).toBe(0);
	});

	it('caps labels at 255 code points', () => {
		bundle.follow(alice);
		const long = '🎮'.repeat(300);

		bundle.add({ ...main(1), gameTitle: long, fileName: long });

		expect([...bundle.entries[0].gameTitle].length).toBe(255);
		expect([...bundle.entries[0].fileName].length).toBe(255);
	});

	it('restores the cart for the same account after a reload, and keeps accounts apart', () => {
		bundle.follow(alice);
		bundle.add(main(1));
		bundle.add(setFile('Track 2.bin'));

		bundle.follow(bob);
		expect(bundle.entries).toEqual([]);
		bundle.add(main(5));

		bundle.follow(null);
		expect(bundle.entries).toEqual([]);
		bundle.follow(alice);
		expect(bundle.entries.map((e) => e.fileName)).toEqual(['game 1.gb', 'Track 2.bin']);
		expect(JSON.parse(localStorage.getItem(cartStorageKey(bob.id))!).entries).toHaveLength(1);
	});

	it('removes one file or clears the cart, and an empty cart leaves no storage behind', () => {
		bundle.follow(alice);
		bundle.add(main(1));
		bundle.add(main(2));

		bundle.remove(entryKey(main(1)));
		expect(bundle.entries.map((e) => e.releaseSha1)).toEqual([sha(2)]);

		bundle.clear();
		expect(bundle.entries).toEqual([]);
		expect(localStorage.getItem(cartStorageKey(alice.id))).toBeNull();
	});

	it('drops malformed, duplicate and excess stored entries, and ignores unreadable storage', () => {
		localStorage.setItem(
			cartStorageKey(alice.id),
			JSON.stringify({
				version: 1,
				entries: [
					main(1),
					{ nope: true },
					main(1),
					{ ...main(2), member: { sha1: 'x', fileName: 'a' } },
					main(3)
				]
			})
		);
		bundle.follow(alice);
		expect(bundle.entries.map((e) => e.releaseSha1)).toEqual([sha(1), sha(3)]);

		bundle.follow(null);
		localStorage.setItem(cartStorageKey(alice.id), '{ not json');
		bundle.follow(alice);
		expect(bundle.entries).toEqual([]);

		expect(cleanEntries(Array.from({ length: 600 }, (_, i) => main(i)))).toHaveLength(
			maxBundleEntries
		);
	});

	it('works for the page when storage throws', () => {
		const blocked = () => {
			throw new Error('blocked');
		};
		vi.stubGlobal('localStorage', { getItem: blocked, setItem: blocked, removeItem: blocked });

		bundle.follow(alice);
		expect(bundle.add(main(1))).toBe('added');
		expect(bundle.count).toBe(1);
	});

	it('shows unavailable entries with their last-known labels, and available ones with current names', async () => {
		bundle.follow(alice);
		bundle.add(main(1));
		bundle.add(setFile('Track 2.bin'));
		expect(bundle.items.map((i) => i.status)).toEqual(['unknown', 'unknown']);

		const refreshing = bundle.refresh();
		await settle();
		expect(bundle.refreshing).toBe(true);
		expect(resolveBodies[0]).toEqual({
			items: [
				{ gameId: game, releaseSha1: sha(1), member: null },
				{
					gameId: game,
					releaseSha1: sha(1),
					member: { sha1: 'f'.repeat(40), fileName: 'Track 2.bin' }
				}
			]
		});
		resolve!({
			items: [
				{ available: false, gameTitle: null, fileName: null, downloadUrl: null },
				{
					available: true,
					gameTitle: 'Disc Runner (Renamed)',
					fileName: 'Track 2.bin',
					downloadUrl: '/api/games/x/roms/3/files/4/download'
				}
			]
		});
		await refreshing;

		const [gone, track] = bundle.items;
		expect(gone).toMatchObject({
			status: 'unavailable',
			gameTitle: 'Game 1',
			fileName: 'game 1.gb',
			downloadUrl: null
		});
		expect(track).toMatchObject({
			status: 'available',
			gameTitle: 'Disc Runner (Renamed)',
			downloadUrl: '/api/games/x/roms/3/files/4/download'
		});
		expect(bundle.refreshing).toBe(false);
		// The new label is remembered for later.
		expect(JSON.parse(localStorage.getItem(cartStorageKey(alice.id))!).entries[1].gameTitle).toBe(
			'Disc Runner (Renamed)'
		);
	});

	it('ignores a refresh answer after an account switch', async () => {
		bundle.follow(alice);
		bundle.add(main(1));
		const refreshing = bundle.refresh();
		await settle();

		bundle.follow(bob);
		resolve!({
			items: [{ available: true, gameTitle: 'Stale', fileName: 'stale.gb', downloadUrl: '/x' }]
		});
		await refreshing;

		expect(bundle.entries).toEqual([]);
		expect(bundle.resolved).toEqual({});
		bundle.follow(alice);
		expect(bundle.entries[0].gameTitle).toBe('Game 1');
	});

	it('ignores a refresh answer after the cart changed', async () => {
		bundle.follow(alice);
		bundle.add(main(1));
		const refreshing = bundle.refresh();
		await settle();

		bundle.add(main(2));
		resolve!({ items: [{ available: false, gameTitle: null, fileName: null, downloadUrl: null }] });
		await refreshing;

		expect(bundle.items.map((i) => i.status)).toEqual(['unknown', 'unknown']);
	});

	it('keeps the cart and flags a failed refresh', async () => {
		fail = true;
		bundle.follow(alice);
		bundle.add(main(1));

		await bundle.refresh();

		expect(bundle.refreshError).toBe(true);
		expect(bundle.count).toBe(1);
	});

	it('replaces the cart with a queue and compares identities in order', () => {
		bundle.follow(alice);
		bundle.add(main(1));
		bundle.add(main(2));

		expect(bundle.sameAs([main(1), main(2)])).toBe(true);
		expect(bundle.sameAs([main(2), main(1)])).toBe(false);

		bundle.replace([main(3), main(3), main(4)]);
		expect(bundle.entries.map((e) => e.releaseSha1)).toEqual([sha(3), sha(4)]);
	});
});
