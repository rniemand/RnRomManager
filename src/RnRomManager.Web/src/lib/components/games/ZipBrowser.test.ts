import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { SvelteSet } from 'svelte/reactivity';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail } from '#lib/types/index.js';
import ZipBrowser from './ZipBrowser.svelte';

const listing = (paths: string[]) => ({
	zipName: 'Tetris.zip',
	entries: paths.map((path) => ({
		path,
		sizeBytes: 2048,
		compressedSizeBytes: 1024,
		modifiedUtc: '2026-11-01T10:00:00Z'
	}))
});
const paths = ['roms/a/deep.gb', 'roms/b.gb', 'images/x.png', 'info.json'];
const game = (id = 'g1') => ({ id }) as GameDetail;

const tree = () => within(screen.getByRole('list', { name: 'Folders' }));
const shown = () =>
	[...screen.getByRole('list', { name: 'Folders' }).querySelectorAll('li')].map((li) =>
		li.querySelector('[aria-current], button + button')!.textContent!.trim()
	);

describe('ZipBrowser (Z1, Z2)', () => {
	let fetches: string[];

	beforeEach(() => {
		resetClient();
		fetches = [];
		vi.stubGlobal('fetch', (url: string) => {
			fetches.push(url);
			return Promise.resolve(new Response(JSON.stringify(listing(paths)), { status: 200 }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	async function setup(expanded = new SvelteSet<string>()) {
		const view = render(ZipBrowser, { game: game(), active: true, selected: '', expanded });
		await screen.findByRole('list', { name: 'Folders' });
		return { ...view, expanded };
	}

	it('expands every folder on the first load', async () => {
		const { expanded } = await setup();

		expect([...expanded].sort()).toEqual(['', 'images', 'roms', 'roms/a']);
		expect(shown()).toEqual(['Tetris.zip', 'images', 'roms', 'a']);
	});

	it('a refetch of the same game keeps the toggles, even everything collapsed', async () => {
		const { expanded, rerender } = await setup();
		await fireEvent.click(tree().getByRole('button', { name: 'Collapse Tetris.zip' }));
		expect(shown()).toEqual(['Tetris.zip']);

		await rerender({ game: game() }); // a new object for the same id: refetched
		await vi.waitFor(() => expect(fetches).toHaveLength(2));
		await screen.findByRole('list', { name: 'Folders' });

		expect(shown()).toEqual(['Tetris.zip']);
		expect(expanded.has('')).toBe(false);
	});

	it('a different game id expands everything again', async () => {
		const { expanded, rerender } = await setup();
		await fireEvent.click(tree().getByRole('button', { name: 'Collapse roms' }));
		expect(expanded.has('roms')).toBe(false);

		await rerender({ game: game('g2') });
		await vi.waitFor(() => expect(fetches).toHaveLength(2));
		await screen.findByRole('list', { name: 'Folders' });

		await vi.waitFor(() => expect(expanded.has('roms')).toBe(true));
	});

	it('selecting from the tree, the breadcrumb or the table never changes expansion', async () => {
		const { expanded } = await setup();
		await fireEvent.click(tree().getByRole('button', { name: 'Collapse roms' }));
		const before = [...expanded].sort();

		await fireEvent.click(tree().getByRole('button', { name: 'roms' }));
		expect([...expanded].sort()).toEqual(before);
		const table = screen.getByRole('table', { name: 'Contents of roms' });
		await fireEvent.click(within(table).getByRole('button', { name: 'a' }));
		expect([...expanded].sort()).toEqual(before);
		const crumbs = within(screen.getByRole('navigation', { name: 'Folder path' }));
		await fireEvent.click(crumbs.getByRole('button', { name: 'roms' }));
		expect([...expanded].sort()).toEqual(before);
		expect(tree().queryByRole('button', { name: 'a' })).toBeNull();
	});

	it('highlights the selected folder row', async () => {
		await setup();

		await fireEvent.click(tree().getByRole('button', { name: 'images' }));

		const current = tree().getByRole('button', { name: 'images' });
		expect(current.getAttribute('aria-current')).toBe('true');
		expect(current.classList.contains('fw-semibold')).toBe(true);
		// Contrast on the subtle highlight (axe color-contrast).
		expect(current.classList.contains('text-primary-emphasis')).toBe(true);
		const row = current.parentElement!;
		expect(row.classList.contains('bg-primary-subtle')).toBe(true);
		expect(row.classList.contains('rounded')).toBe(true);
		const root = tree().getByRole('button', { name: 'Tetris.zip' }).parentElement!;
		expect(root.classList.contains('bg-primary-subtle')).toBe(false);
	});

	it('table in a card: size badges, empty folder cells, truncated names with a title, download button (B12)', async () => {
		await setup();
		await fireEvent.click(tree().getByRole('button', { name: 'roms' }));

		const table = screen.getByRole('table', { name: 'Contents of roms' });
		expect(table.className).toBe(
			'table table-sm table-striped table-hover table-bordered align-middle mb-0 zip-table'
		);
		// No .table-responsive: names truncate, so the table never scrolls sideways.
		expect(table.closest('.table-responsive')).toBeNull();
		expect(table.parentElement!.className).toBe('card mb-2 overflow-hidden');
		const [folderRow, fileRow] = within(table).getAllByRole('row').slice(1);
		const folderCells = [...folderRow.querySelectorAll('td')];
		expect(folderCells.map((td) => td.textContent!.trim())).toEqual(['a', '', '', '', '']);
		expect(folderRow.querySelector('[colspan]')).toBeNull();
		expect(folderCells[0].className).toContain('text-truncate');
		expect(folderCells[0].title).toBe('a');

		const cells = [...fileRow.querySelectorAll('td')];
		expect(cells[0].className).toContain('text-truncate');
		expect(cells[0].className).not.toContain('text-break');
		expect(cells[0].title).toBe('b.gb');
		expect(cells[1].className).toBe('text-end text-nowrap');
		// Compressed and Modified hide on a phone.
		expect(cells[2].className).toBe('text-end text-nowrap d-none d-md-table-cell');
		expect(cells[3].className).toContain('d-none d-md-table-cell');
		expect(folderCells[2].className).toBe('d-none d-md-table-cell');
		for (const cell of [cells[1], cells[2]]) {
			expect(cell.querySelector('.badge')!.className).toBe('badge text-bg-light border');
		}
		expect(cells[1].textContent!.trim()).toBe('2.0 KiB');
		const download = within(fileRow).getByRole('link', { name: 'Download b.gb' });
		expect(download.className).toBe('btn btn-sm btn-outline-primary');
		expect(download.closest('td')!.className).toBe('text-end');
	});

	it('the folder panel is col-md-3 and the detail col-md-9 (B12)', async () => {
		await setup();
		const table = screen.getByRole('table');
		expect(table.closest('.col-md-9')).not.toBeNull();
		expect(tree().getByRole('button', { name: 'roms' }).closest('.col-md-3')).not.toBeNull();
	});
});
