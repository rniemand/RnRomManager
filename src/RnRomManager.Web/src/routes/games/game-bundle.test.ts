import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail, ReleaseFile } from '#lib/types/index.js';
import { bundle } from '#lib/stores/bundle.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

vi.mock('$app/navigation', () => ({ goto: (url: string) => navigateTo(url) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: '8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f' },
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: GamePage } = await import('./[id]/+page.svelte');

const gameId = '8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
const mainSha = 'a'.repeat(40);
const cueSha = 'c'.repeat(40);
const trackSha = 'd'.repeat(40);

const files: ReleaseFile[] = [
	{
		id: 11,
		fileName: 'Disc (USA).cue',
		sizeBytes: 100,
		crc32: 'c1',
		md5: 'm1',
		sha1: cueSha,
		downloadUrl: '/f/11'
	},
	{
		id: 12,
		fileName: 'Disc (USA) (Track 2).bin',
		sizeBytes: 2048,
		crc32: 'c2',
		md5: 'm2',
		sha1: trackSha,
		downloadUrl: '/f/12'
	}
];

function game(): GameDetail {
	return {
		id: gameId,
		system: 'psx',
		systemName: 'PlayStation',
		browserCore: null,
		fileSets: false,
		title: 'Disc Runner',
		displayTitle: 'Disc Runner',
		releaseYear: null,
		developers: [],
		publishers: [],
		genres: [],
		description: null,
		hasDefaultBoxArt: false,
		rating: null,
		favourite: false,
		saveCount: 0,
		igdbId: null,
		igdbConfigured: false,
		metadata: { createdAt: '', updatedAt: '' },
		roms: [
			{
				id: 1,
				fileName: 'Disc (USA) (Track 1).bin',
				originalFileName: 'Disc (USA) (Track 1).bin',
				baseTitle: 'Disc Runner',
				regions: ['USA'],
				languages: [],
				revision: null,
				flags: [],
				isPrimary: true,
				identificationMethod: 'Dat',
				datName: null,
				datVersion: null,
				datGameName: null,
				header: null,
				sizeBytes: 3000,
				crc32: 'x',
				md5: 'x',
				sha1: mainSha,
				dateAdded: '2026-11-01T00:00:00Z',
				dateModified: '2026-11-01T00:00:00Z',
				downloadUrl: '/api/games/g/roms/1/download',
				hasNote: false,
				externalFileName: 'Disc (USA) (Track 1).bin',
				version: null,
				files,
				totalSizeBytes: 5148,
				discSet: null
			}
		],
		images: []
	} as GameDetail;
}

describe('game page add-to-bundle controls (B1, B3)', () => {
	let store: Map<string, string>;

	beforeEach(() => {
		resetClient();
		navigateTo(`/games/${gameId}`);
		toasts.items.length = 0;
		store = new Map();
		vi.stubGlobal('localStorage', {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value),
			removeItem: (key: string) => void store.delete(key)
		});
		vi.stubGlobal('fetch', (url: string) =>
			Promise.resolve(
				url === `/api/games/${gameId}`
					? new Response(JSON.stringify(game()), { status: 200 })
					: url === `/api/games/${gameId}/collections`
						? new Response('[]', { status: 200 })
						: new Response(null, { status: 204 })
			)
		);
		bundle.follow({ id: 3, username: 'u', roles: [], mustChangePassword: false });
	});
	afterEach(() => {
		bundle.follow(null);
		vi.unstubAllGlobals();
	});

	/** Opens the release's file list (B20), where the set files' controls are. */
	async function openFiles(): Promise<void> {
		const details = await screen.findByRole('button', {
			name: /details for Disc \(USA\) \(Track 1\)\.bin/
		});
		if (details.getAttribute('aria-expanded') !== 'true') await fireEvent.click(details);
		const toggle = document.querySelector<HTMLElement>('[aria-controls="release-1-files"]')!;
		await fireEvent.click(toggle);
	}

	it("adds a release's main file with the rendered metadata", async () => {
		render(GamePage);

		// The release card's control; the file list is still closed.
		await fireEvent.click(
			await screen.findByRole('button', { name: 'Add Disc (USA) (Track 1).bin to the bundle' })
		);

		expect(bundle.entries).toEqual([
			{
				gameId,
				releaseSha1: mainSha,
				member: null,
				gameTitle: 'Disc Runner',
				fileName: 'Disc (USA) (Track 1).bin'
			}
		]);
		expect(toasts.items.at(-1)).toMatchObject({
			text: 'Added Disc (USA) (Track 1).bin to the bundle.',
			kind: 'success'
		});
		// Both the card and the file list's main row now show it as added.
		await openFiles();
		expect(
			screen.getAllByRole('button', { name: 'Disc (USA) (Track 1).bin is in the bundle' })
		).toHaveLength(2);
	});

	it('adds a set file by its SHA-1 and stored name within the release', async () => {
		render(GamePage);
		await openFiles();

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Add Disc (USA) (Track 2).bin to the bundle' })
		);

		expect(bundle.entries).toEqual([
			{
				gameId,
				releaseSha1: mainSha,
				member: { sha1: trackSha, fileName: 'Disc (USA) (Track 2).bin' },
				gameTitle: 'Disc Runner',
				fileName: 'Disc (USA) (Track 2).bin'
			}
		]);
		expect(screen.getByRole('button', { name: 'Add Disc (USA).cue to the bundle' })).toBeTruthy();
		expect(
			screen.getByRole('button', { name: 'Disc (USA) (Track 2).bin is in the bundle' })
		).toBeTruthy();
	});

	it('a second click says the file is already there and adds nothing', async () => {
		render(GamePage);
		await openFiles();
		const add = await screen.findByRole('button', { name: 'Add Disc (USA).cue to the bundle' });

		await fireEvent.click(add);
		await fireEvent.click(screen.getByRole('button', { name: 'Disc (USA).cue is in the bundle' }));

		expect(bundle.count).toBe(1);
		expect(toasts.items.at(-1)).toMatchObject({
			text: 'Disc (USA).cue is already in the bundle.',
			kind: 'info'
		});
	});
});
