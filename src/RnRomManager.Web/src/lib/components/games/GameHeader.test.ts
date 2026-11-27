import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import type { ComponentProps } from 'svelte';
import type { GameDetail, RomDetail } from '#lib/types/index.js';
import { auth } from '#lib/stores/auth.svelte.js';
import GameHeader from './GameHeader.svelte';

vi.mock('#lib/tooltip.js', () => ({ tooltip: () => {} }));

const rom = (id: number, fileName: string) =>
	({
		id,
		fileName,
		sizeBytes: 1024,
		downloadUrl: `/rom/${id}`,
		externalFileName: null
	}) as RomDetail;

const game = (patch: Partial<GameDetail> = {}) =>
	({
		id: 'g1',
		system: 'gb',
		systemName: 'Game Boy',
		displayTitle: 'Tetris',
		releaseYear: 1989,
		developers: ['Nintendo'],
		publishers: [],
		genres: [],
		description: null,
		rating: null,
		favourite: false,
		igdbId: null,
		igdbConfigured: false,
		roms: [rom(1, 'Tetris.gb')],
		images: [],
		...patch
	}) as GameDetail;

function setup(props: Partial<ComponentProps<typeof GameHeader>> = {}) {
	const handlers = {
		ondownload: vi.fn(),
		onsaved: vi.fn(),
		onstale: vi.fn(),
		onaction: vi.fn(),
		oninfojson: vi.fn(),
		oncollections: vi.fn()
	};
	render(GameHeader, {
		game: game(),
		gameId: 'g1',
		memberOf: [],
		downloadRoms: [rom(1, 'Tetris.gb')],
		fromMissingMetadata: false,
		fromMissingIgdb: false,
		descriptionExpanded: false,
		...handlers,
		...props
	});
	return handlers;
}

describe('GameHeader', () => {
	afterEach(() => auth.set(null));

	it('shows the metadata with links and the collections the game is in', () => {
		setup({ memberOf: [{ id: 'c1', name: 'Favourites' }] });

		expect(screen.getByRole('heading', { level: 1, name: 'Tetris' })).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Nintendo' }).getAttribute('href')).toBe(
			'/collections?tab=developers&name=Nintendo'
		);
		const collections = screen.getByRole('list', { name: 'In collections' });
		expect(within(collections).getByRole('link').getAttribute('href')).toBe('/collections/c1');
	});

	it('downloads the ZIP and the single ROM through ondownload', async () => {
		const { ondownload } = setup();

		await fireEvent.click(screen.getByRole('button', { name: 'Download ZIP' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Download ROM' }));

		expect(ondownload).toHaveBeenNthCalledWith(1, '/api/games/g1/download');
		expect(ondownload).toHaveBeenNthCalledWith(2, '/rom/1', expect.objectContaining({ id: 1 }));
	});

	it('lists several ROMs in a Download ROM menu', () => {
		setup({ downloadRoms: [rom(1, 'A.gb'), rom(2, 'B.gb')] });

		expect(screen.getByRole('button', { name: /^A\.gb — / })).toBeTruthy();
		expect(screen.getByRole('button', { name: /^B\.gb — / })).toBeTruthy();
	});

	it('hands the Manage menu choices to the page', async () => {
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		const { onaction, oninfojson, oncollections } = setup();

		await fireEvent.click(screen.getByRole('button', { name: 'View info.json' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Merge into…' }));
		const quickEdit = screen.getByRole('button', { name: 'Quick edit' });
		// I1: the quick-edit pencil is an IconButton with the same look.
		expect(quickEdit.className).toBe(
			'btn btn-sm btn-link text-body-secondary p-0 ms-auto quick-edit'
		);
		await fireEvent.click(quickEdit);
		await fireEvent.click(screen.getByRole('button', { name: 'Collections' }));

		expect(oninfojson).toHaveBeenCalledOnce();
		expect(onaction.mock.calls).toEqual([[{ kind: 'merge' }], [{ kind: 'edit' }]]);
		expect(oncollections).toHaveBeenCalledOnce();
	});

	it.each([
		['unlinked', null],
		['linked', 42]
	])(
		'an Admin sees Lookup Info for an %s game; it opens Edit Metadata on the lookup (GP1)',
		async (_, igdbId) => {
			auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
			const { onaction } = setup({ game: game({ igdbConfigured: true, igdbId }) });

			const button = screen.getByRole('button', { name: 'Lookup Info for Tetris' });
			expect(button.className).toBe('btn btn-sm btn-outline-primary');
			expect(button.querySelector('i.bi-search')).toBeTruthy();
			await fireEvent.click(button);

			expect(onaction).toHaveBeenCalledWith({ kind: 'edit', lookup: true });
		}
	);

	it('no Lookup Info without IGDB (GP1)', () => {
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		setup();
		expect(screen.queryByRole('button', { name: /^Lookup Info/ })).toBeNull();
	});

	it('no Lookup Info for a Site user (GP1)', () => {
		auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
		setup({ game: game({ igdbConfigured: true }) });
		expect(screen.queryByRole('button', { name: /^Lookup Info/ })).toBeNull();
	});

	it('hides the Admin actions from a Site user', () => {
		auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
		setup();

		expect(screen.queryByRole('button', { name: 'Edit Metadata' })).toBeNull();
		expect(screen.queryByRole('button', { name: 'Quick edit' })).toBeNull();
	});
});
