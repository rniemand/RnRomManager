import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ComponentProps } from 'svelte';
import { SvelteMap, SvelteSet } from 'svelte/reactivity';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail, ReleaseNoteState, RomDetail } from '#lib/types/index.js';
import { auth } from '#lib/stores/auth.svelte.js';
import ReleaseTable from './ReleaseTable.svelte';

vi.mock('#lib/tooltip.js', () => ({ tooltip: () => {} }));

const rom = (id: number, fileName: string, patch: Partial<RomDetail> = {}) =>
	({
		id,
		fileName,
		originalFileName: fileName,
		regions: ['World'],
		languages: [],
		revision: null,
		flags: [],
		isPrimary: id === 1,
		identificationMethod: 'Dat',
		datName: null,
		datVersion: null,
		datGameName: null,
		header: null,
		sizeBytes: 1024,
		totalSizeBytes: 1024,
		crc32: 'c',
		md5: 'm',
		sha1: `sha${id}`,
		dateAdded: '2026-11-01T00:00:00Z',
		downloadUrl: `/rom/${id}`,
		hasNote: false,
		externalFileName: null,
		version: null,
		files: null,
		discSet: null,
		...patch
	}) as RomDetail;

const game = { id: 'g1', system: 'gb', displayTitle: 'Tetris', browserCore: null } as GameDetail;

function setup(props: Partial<ComponentProps<typeof ReleaseTable>> = {}) {
	const handlers = {
		ondownload: vi.fn(),
		onnote: vi.fn(),
		onheader: vi.fn(),
		onplay: vi.fn(),
		onedit: vi.fn(),
		onaction: vi.fn(),
		onrefresh: vi.fn(() => Promise.resolve())
	};
	const state = {
		openReleases: new SvelteSet<string>(),
		openFiles: new SvelteSet<string>(),
		notes: new SvelteMap<string, ReleaseNoteState>(),
		pendingNotes: new SvelteSet<string>()
	};
	render(ReleaseTable, {
		game,
		gameId: 'g1',
		tab: 'releases',
		roms: [rom(1, 'A.gb'), rom(2, 'B.gb')],
		releaseCount: 2,
		...state,
		...handlers,
		...props
	});
	return { ...handlers, ...state };
}

describe('ReleaseTable', () => {
	beforeEach(() => resetClient());
	afterEach(() => {
		auth.set(null);
		vi.unstubAllGlobals();
	});

	it('renders one collapsed card per release and toggles them in the shared set', async () => {
		const { openReleases } = setup();

		expect(screen.getAllByRole('article')).toHaveLength(2);
		await fireEvent.click(screen.getByRole('button', { name: 'Show details for A.gb' }));
		expect([...openReleases]).toEqual(['A.gb']);

		await fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
		expect([...openReleases].sort()).toEqual(['A.gb', 'B.gb']);
		await fireEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
		expect(openReleases.size).toBe(0);
	});

	it('says when a game has only updates and DLC', () => {
		setup({ roms: [], releaseCount: 0 });

		expect(screen.getByText('No releases other than updates and DLC.')).toBeTruthy();
	});

	it('hands downloads and Admin release actions to the page', async () => {
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		const { ondownload, onedit, onaction } = setup({
			openReleases: new SvelteSet(['A.gb', 'B.gb'])
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Download B.gb' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Edit release B.gb' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Make primary B.gb' }));

		expect(ondownload).toHaveBeenCalledWith('/rom/2', expect.objectContaining({ id: 2 }));
		expect(onedit).toHaveBeenCalledWith(expect.objectContaining({ id: 2 }));
		expect(onaction).toHaveBeenCalledWith({
			kind: 'primary',
			rom: expect.objectContaining({ id: 2 })
		});
		// The primary release offers no Make primary.
		expect(screen.queryByRole('button', { name: 'Make primary A.gb' })).toBeNull();
	});

	it('loads an open card note into the shared map', async () => {
		const fetches: string[] = [];
		vi.stubGlobal('fetch', (url: string) => {
			fetches.push(url);
			return Promise.resolve(new Response('# Hi', { status: 200 }));
		});
		const openReleases = new SvelteSet(['A.gb']);
		const { notes } = setup({ roms: [rom(1, 'A.gb', { hasNote: true })], openReleases });

		await vi.waitFor(() => expect(notes.get('A.gb')).toEqual({ text: '# Hi' }));
		expect(fetches).toEqual(['/api/games/g1/roms/1/note']);
		expect(screen.getByRole('heading', { name: 'Hi' })).toBeTruthy();
	});
});
