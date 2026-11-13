import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { SvelteSet } from 'svelte/reactivity';
import { resetClient } from '../api/client';
import type { GameDetail } from '../api/types';
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

	it('table: size badges, dashed folder rows, truncated names with a title, download button', async () => {
		await setup();
		await fireEvent.click(tree().getByRole('button', { name: 'roms' }));

		const table = screen.getByRole('table', { name: 'Contents of roms' });
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle');
		const [folderRow, fileRow] = within(table).getAllByRole('row').slice(1);
		const folderCells = [...folderRow.querySelectorAll('td')];
		expect(folderCells.map((td) => td.textContent!.trim())).toEqual(['a', '—', '—', '—', '']);
		expect(folderRow.querySelector('[colspan]')).toBeNull();
		expect(folderCells[0].className).toContain('text-truncate');
		expect(folderCells[0].title).toBe('a');

		const cells = [...fileRow.querySelectorAll('td')];
		expect(cells[0].className).toContain('text-truncate');
		expect(cells[0].className).not.toContain('text-break');
		expect(cells[0].title).toBe('b.gb');
		for (const cell of [cells[1], cells[2]]) {
			expect(cell.className).toBe('text-end text-nowrap');
			expect(cell.querySelector('.badge')!.className).toBe('badge text-bg-light border');
		}
		expect(cells[1].textContent!.trim()).toBe('2.0 KiB');
		const download = within(fileRow).getByRole('link', { name: 'Download b.gb' });
		expect(download.className).toBe('btn btn-sm btn-outline-primary');
		expect(download.closest('td')!.className).toBe('text-end');
	});
});
