import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail } from '#lib/api/types.js';

vi.mock('$app/state', () => ({ page: { params: { id: 'g1' } } }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

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

	it('shows box art, releases with region badges, and the header in a modal', async () => {
		const detail = game([1]);
		detail.roms[0].regions = ['USA', 'Europe'];
		answers.push({ status: 200, body: detail });
		render(GamePage);

		expect(await screen.findByAltText('Tetris cover')).toBeTruthy();
		expect(screen.getByText('Primary')).toBeTruthy();
		const row = screen.getByText('Primary').closest('tr')!;
		expect([...row.querySelectorAll('.badge.text-bg-secondary')].map((b) => b.textContent)).toEqual(
			['USA', 'Europe']
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Header' }));
		const caption = screen.getByText('Header of Tetris 1.gb');
		const modal = caption.closest('.modal')!;
		expect(modal.querySelector('.modal-title')?.textContent).toBe('Header — Tetris 1.gb');
		expect(modal.querySelector('.modal-dialog')!.classList).toContain('modal-lg');
		expect(screen.getByText('Logo valid')).toBeTruthy();
		expect(screen.getByText('Yes')).toBeTruthy();
	});

	it('one ROM gets a Download ROM button', async () => {
		answers.push({ status: 200, body: game([1]) }, { status: 200, body: game([1]) });
		render(GamePage);

		const button = await screen.findByRole('button', { name: 'Download ROM' });
		expect(button.classList).not.toContain('dropdown-toggle');
		await fireEvent.click(button);

		await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/api/games/g1/roms/1/download'));
	});

	it('several ROMs get a dropdown with sizes, primary first', async () => {
		const detail = game([1, 2, 3]);
		// The primary isn't first in the Releases order here.
		detail.roms[0].isPrimary = false;
		detail.roms[2].isPrimary = true;
		detail.roms[1].sizeBytes = 1024;
		answers.push({ status: 200, body: detail }, { status: 200, body: detail });
		render(GamePage);

		const toggle = await screen.findByRole('button', { name: 'Download ROM' });
		expect(toggle.classList).toContain('dropdown-toggle');
		const items = [...toggle.parentElement!.querySelectorAll('.dropdown-item')];
		expect(items.map((i) => i.textContent?.trim())).toEqual([
			'Tetris 3.gb — 32.0 KiB',
			'Tetris 1.gb — 32.0 KiB',
			'Tetris 2.gb — 1.0 KiB'
		]);
		await fireEvent.click(items[2]);

		await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/api/games/g1/roms/2/download'));
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
