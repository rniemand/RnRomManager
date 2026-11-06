import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail } from '#lib/api/types.js';

vi.mock('$app/state', () => ({ page: { params: { id: 'g1' } } }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: GamePage } = await import('./[id]/+page.svelte');

const rom = (id: number, fileName: string, isPrimary: boolean) => ({
	id,
	fileName,
	originalFileName: fileName,
	baseTitle: 'Tetris',
	regions: ['World'],
	languages: [],
	revision: null,
	flags: [],
	isPrimary,
	identificationMethod: 'Dat' as const,
	datName: null,
	datVersion: null,
	datGameName: null,
	header: { title: 'TETRIS', logoValid: true, romSizeBytes: null },
	sizeBytes: 32768,
	crc32: '46df91ad',
	md5: 'x',
	sha1: 'abc123',
	dateAdded: '2026-11-01T00:00:00Z',
	dateModified: '2026-11-01T00:00:00Z',
	downloadUrl: `/api/games/g1/roms/${id}/download`
});

function game(romIds: number[]): GameDetail {
	return {
		id: 'g1',
		system: 'gb',
		title: 'Tetris',
		displayTitle: 'Tetris',
		releaseYear: 1989,
		developers: ['Nintendo'],
		publishers: [],
		genres: [],
		description: null,
		hasDefaultBoxArt: false,
		rating: null,
		favourite: false,
		metadata: { createdAt: '', updatedAt: '' },
		roms: romIds.map((id, i) => rom(id, `Tetris ${id}.gb`, i === 0)),
		images: [
			{
				id: 9,
				type: 'BoxArt',
				fileName: 'boxart-01.png',
				source: 'libretro-thumbnails',
				isDefault: false,
				isCover: true,
				sizeBytes: 1024,
				width: 512,
				height: 512,
				url: '/img/9'
			}
		]
	};
}

describe('game detail page', () => {
	let answers: { status: number; body?: unknown }[];
	const assign = vi.fn();

	beforeEach(() => {
		resetClient();
		assign.mockClear();
		answers = [];
		vi.stubGlobal('fetch', () => {
			const answer = answers.shift() ?? { status: 404, body: { code: 'NotFound' } };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
		vi.stubGlobal('location', { ...window.location, assign });
	});
	afterEach(() => vi.unstubAllGlobals());

	it('shows box art, releases and the generic header table', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);

		expect(await screen.findByAltText('Tetris cover')).toBeTruthy();
		expect(screen.getByText('Primary')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Header' }));
		expect(screen.getByText('Logo valid')).toBeTruthy();
		expect(screen.getByText('Yes')).toBeTruthy();
	});

	it('downloads a ROM that still exists', async () => {
		answers.push({ status: 200, body: game([1]) }, { status: 200, body: game([1]) });
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Download Tetris 1.gb' }));

		await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/api/games/g1/roms/1/download'));
	});

	it('a stale ROM id reloads the game and asks for a retry', async () => {
		answers.push({ status: 200, body: game([1]) }, { status: 200, body: game([2]) });
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Download Tetris 1.gb' }));

		expect(await screen.findByText('This game changed, please retry.')).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Download Tetris 2.gb' })).toBeTruthy();
		expect(assign).not.toHaveBeenCalled();
	});

	it('any user can rate; after a save the page uses the returned detail (new ROM ids)', async () => {
		answers.push(
			{ status: 200, body: game([1]) },
			{ status: 204 }, // GET /api/auth/antiforgery before the first mutation
			{ status: 200, body: { ...game([7]), rating: 4 } },
			{ status: 200, body: { ...game([7]), rating: 4 } }
		);
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Rate 4 of 5' }));

		expect(await screen.findByRole('button', { name: 'Download Tetris 7.gb' })).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Rate 4 of 5' }).getAttribute('aria-pressed')).toBe(
			'true'
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Download Tetris 7.gb' }));
		await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/api/games/g1/roms/7/download'));
	});

	it('an unknown game shows a not-found state', async () => {
		render(GamePage);

		expect(await screen.findByText("This game doesn't exist any more.")).toBeTruthy();
	});
});
