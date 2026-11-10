import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail } from '#lib/api/types.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

const goto = vi.fn((...args: [string, unknown?]) => navigateTo(args[0]));
vi.mock('$app/navigation', () => ({ goto: (...args: [string, unknown?]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		// The id follows the test URL (`/games/<id>`), so a navigation to another game changes it like in the app.
		get params() {
			return { id: testUrl.current.pathname.split('/')[2] };
		},
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
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
	downloadUrl: `/api/games/g1/roms/${id}/download`,
	hasNote: false
});

function game(romIds: number[]): GameDetail {
	return {
		id: 'g1',
		system: 'gb',
		systemName: 'Game Boy',
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
	let fetches: number;
	const assign = vi.fn();

	beforeEach(() => {
		resetClient();
		assign.mockClear();
		goto.mockClear();
		navigateTo('/games/g1');
		answers = [];
		fetches = 0;
		vi.stubGlobal('fetch', (url: string) => {
			// B16: the game's collections (the badges); answered here so the queued answers stay as they were.
			if (url.endsWith('/collections')) return Promise.resolve(new Response('[]', { status: 200 }));
			fetches++;
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

	it('shows the original file name under the file name only when it differs (B1)', async () => {
		const detail = game([1, 2]);
		detail.roms[1].originalFileName = 'tetris_rev1_final.gb';
		answers.push({ status: 200, body: detail });
		render(GamePage);

		const original = await screen.findByText('Original: tetris_rev1_final.gb');
		// G2: wraps, never truncated.
		expect(original.classList.contains('text-break')).toBe(true);
		expect(original.classList.contains('text-body-secondary')).toBe(true);
		expect(original.closest('article')!.getAttribute('aria-labelledby')).toBe('release-2-name');
		expect(screen.getAllByText(/^Original:/)).toHaveLength(1);
	});

	it('shows box art, releases with region badges, and the header in a modal', async () => {
		const detail = game([1]);
		detail.roms[0].regions = ['USA', 'Europe'];
		answers.push({ status: 200, body: detail });
		render(GamePage);

		expect(await screen.findByAltText('Tetris cover')).toBeTruthy();
		expect(screen.getByText('Primary')).toBeTruthy();
		const row = screen.getByText('Primary').closest('article')!;
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

	it('one card per release with name, badges, details, copy buttons and actions; no table (G2)', async () => {
		const detail = game([1, 2]);
		detail.roms[0].revision = 'Rev 1';
		detail.roms[0].flags = ['Beta', 'Proto'];
		answers.push({ status: 200, body: detail });
		render(GamePage);

		const card = await screen.findByRole('article', { name: 'Tetris 1.gb' });
		await fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
		expect(screen.getAllByRole('article')).toHaveLength(2);
		expect(card.className).toBe('card');
		const heading = card.querySelector('h3')!;
		expect(heading.className).toBe('h6 mb-0 text-break');
		expect(card.querySelector('.card-header .badge.text-bg-primary')!.textContent).toBe('Primary');
		expect(
			[...card.querySelectorAll('.card-header .badge.text-bg-secondary')].map((b) => b.textContent)
		).toEqual(['World']);
		const body = card.querySelector('.card-body')!;
		expect(body.textContent).toMatch(/Revision\s*Rev 1/);
		expect(body.textContent).toMatch(/Flags\s*Beta, Proto/);
		expect(body.querySelector('.file-size')!.textContent).toBe('32.0 KiB');
		expect(body.querySelectorAll('.font-monospace.text-break')).toHaveLength(2);
		expect(body.querySelector('[aria-label="Copy CRC32"]')).not.toBeNull();
		expect(body.querySelector('[aria-label="Copy SHA1"]')).not.toBeNull();
		expect(body.textContent).toContain('46df91ad');
		expect(body.textContent).toContain('abc123');
		const second = screen.getByRole('article', { name: 'Tetris 2.gb' });
		expect(second.querySelector('.badge.text-bg-primary')).toBeNull();
		expect(second.querySelector('.card-body')!.textContent).toMatch(/Revision\s*—/);

		const labels = [...card.querySelectorAll('.card-header button')].map(
			(b) => b.getAttribute('aria-label') ?? b.textContent?.trim()
		);
		expect(labels).toEqual([
			'Hide details for Tetris 1.gb',
			'Header',
			'Add note for Tetris 1.gb',
			'Download Tetris 1.gb'
		]);
		expect(document.getElementById('releases-panel')!.querySelector('table')).toBeNull();
	});

	const details = (name: string) =>
		screen.getByRole('article', { name }).querySelector<HTMLElement>('.card-body')!;
	const chevron = (name: string) =>
		screen.getByRole('button', { name: new RegExp(`^(Show|Hide) details for ${name}$`) });

	it('release cards start collapsed; the chevron toggles the details and its label (R1, R2)', async () => {
		answers.push({ status: 200, body: game([1, 2]) });
		render(GamePage);
		await screen.findByRole('article', { name: 'Tetris 1.gb' });

		const toggle = chevron('Tetris 1.gb');
		expect(details('Tetris 1.gb').hidden).toBe(true);
		expect(details('Tetris 1.gb').id).toBe('release-1-details');
		expect(toggle.getAttribute('aria-expanded')).toBe('false');
		expect(toggle.getAttribute('aria-controls')).toBe('release-1-details');
		expect(toggle.getAttribute('aria-label')).toBe('Show details for Tetris 1.gb');
		expect(details('Tetris 1.gb').querySelector('[aria-label="Copy CRC32"]')).not.toBeNull();

		await fireEvent.click(toggle);
		expect(details('Tetris 1.gb').hidden).toBe(false);
		expect(toggle.getAttribute('aria-expanded')).toBe('true');
		expect(toggle.getAttribute('aria-label')).toBe('Hide details for Tetris 1.gb');
		expect(details('Tetris 2.gb').hidden).toBe(true);

		await fireEvent.click(toggle);
		expect(details('Tetris 1.gb').hidden).toBe(true);
	});

	it('a click on empty header space toggles; header buttons and a text selection do not (R2)', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);
		const card = await screen.findByRole('article', { name: 'Tetris 1.gb' });
		const header = card.querySelector<HTMLElement>('.card-header')!;

		await fireEvent.click(header);
		expect(details('Tetris 1.gb').hidden).toBe(false);
		await fireEvent.click(card.querySelector('h3')!);
		expect(details('Tetris 1.gb').hidden).toBe(true);

		await fireEvent.click(screen.getByRole('button', { name: 'Header' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Add note for Tetris 1.gb' }));
		expect(details('Tetris 1.gb').hidden).toBe(true);

		vi.stubGlobal('getSelection', () => ({ toString: () => 'Tetris 1' }));
		await fireEvent.click(header);
		expect(details('Tetris 1.gb').hidden).toBe(true);
	});

	it('Download in the header does not toggle (R2)', async () => {
		answers.push({ status: 200, body: game([1]) }, { status: 200, body: game([1]) });
		render(GamePage);
		await screen.findByRole('article', { name: 'Tetris 1.gb' });

		await fireEvent.click(screen.getByRole('button', { name: 'Download Tetris 1.gb' }));
		await vi.waitFor(() => expect(assign).toHaveBeenCalled());
		expect(details('Tetris 1.gb').hidden).toBe(true);
	});

	it('Expand all / Collapse all, shown only with two or more releases (R3)', async () => {
		answers.push({ status: 200, body: game([1, 2]) });
		render(GamePage);
		await screen.findByRole('article', { name: 'Tetris 1.gb' });

		await fireEvent.click(chevron('Tetris 1.gb'));
		await fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
		expect([details('Tetris 1.gb').hidden, details('Tetris 2.gb').hidden]).toEqual([false, false]);
		await fireEvent.click(chevron('Tetris 2.gb'));
		expect(screen.getByRole('button', { name: 'Expand all' })).toBeTruthy();
		await fireEvent.click(chevron('Tetris 2.gb'));
		await fireEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
		expect([details('Tetris 1.gb').hidden, details('Tetris 2.gb').hidden]).toEqual([true, true]);
	});

	it('one release has no Expand all button (R3)', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);
		await screen.findByRole('article', { name: 'Tetris 1.gb' });

		expect(screen.queryByRole('button', { name: /^(Expand|Collapse) all$/ })).toBeNull();
	});

	it('open state survives a same-game reload with new ROM ids, and resets on another game (R4)', async () => {
		const reloaded = { ...game([7, 8]), rating: 4 };
		reloaded.roms.forEach((r, i) => (r.fileName = `Tetris ${i + 1}.gb`));
		answers.push(
			{ status: 200, body: game([1, 2]) },
			{ status: 204 }, // GET /api/auth/antiforgery before the first mutation
			{ status: 200, body: reloaded }
		);
		render(GamePage);
		await screen.findByRole('article', { name: 'Tetris 1.gb' });
		await fireEvent.click(chevron('Tetris 1.gb'));

		await fireEvent.click(screen.getByRole('button', { name: 'Rate 4 of 5' }));
		await vi.waitFor(() => expect(details('Tetris 1.gb').id).toBe('release-7-details'));
		expect(details('Tetris 1.gb').hidden).toBe(false);
		expect(details('Tetris 2.gb').hidden).toBe(true);

		const other = { ...game([1, 2]), id: 'g2' };
		answers.push({ status: 200, body: other });
		navigateTo('/games/g2');
		await vi.waitFor(() => expect(fetches).toBe(3));
		await screen.findByRole('article', { name: 'Tetris 1.gb' });
		expect(details('Tetris 1.gb').hidden).toBe(true);
	});

	it('Releases is the default tab; both panels stay in the DOM (GP1)', async () => {
		answers.push({ status: 200, body: game([1, 2]) });
		render(GamePage);

		const releases = await screen.findByRole('tab', { name: 'Releases (2)' });
		const images = screen.getByRole('tab', { name: 'Images (1)' });
		expect(releases.getAttribute('aria-selected')).toBe('true');
		expect(images.getAttribute('aria-selected')).toBe('false');
		expect(releases.getAttribute('aria-controls')).toBe('releases-panel');
		expect(images.getAttribute('aria-controls')).toBe('images-panel');
		expect(screen.getByRole('tablist')).toBeTruthy();
		const releasesPanel = document.getElementById('releases-panel')!;
		const imagesPanel = document.getElementById('images-panel')!;
		expect(releasesPanel.getAttribute('role')).toBe('tabpanel');
		expect(releasesPanel.hidden).toBe(false);
		expect(imagesPanel.hidden).toBe(true);
		expect(imagesPanel.querySelector('#images-heading')).toBeTruthy();
	});

	it('?tab=images opens Images', async () => {
		navigateTo('/games/g1?tab=images');
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);

		const images = await screen.findByRole('tab', { name: 'Images (1)' });
		expect(images.getAttribute('aria-selected')).toBe('true');
		expect(document.getElementById('images-panel')!.hidden).toBe(false);
		expect(document.getElementById('releases-panel')!.hidden).toBe(true);
	});

	it('switching tabs updates the URL without refetching the game (GP1)', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);
		await screen.findByRole('tab', { name: 'Releases (1)' });
		expect(fetches).toBe(1);

		await fireEvent.click(screen.getByRole('tab', { name: 'Images (1)' }));
		expect(goto).toHaveBeenLastCalledWith('/games/g1?tab=images', { replace: true, reset: false });
		await vi.waitFor(() => expect(document.getElementById('images-panel')!.hidden).toBe(false));

		await fireEvent.click(screen.getByRole('tab', { name: 'Releases (1)' }));
		expect(goto).toHaveBeenLastCalledWith('/games/g1', { replace: true, reset: false });
		await vi.waitFor(() => expect(document.getElementById('releases-panel')!.hidden).toBe(false));
		expect(fetches).toBe(1);
	});

	it('the Google button searches the title and system in a new tab (GP2)', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);

		const link = await screen.findByRole('link', { name: /Google/ });
		expect(link.getAttribute('href')).toBe(
			'https://www.google.com/search?q=%22Tetris%22%20Game%20Boy'
		);
		expect(link.getAttribute('target')).toBe('_blank');
		expect(link.getAttribute('rel')).toBe('noopener noreferrer');
		expect(link.querySelector('.bi-google')).not.toBeNull();
	});

	it('the information table has a hidden caption and System, Year, Developers, Publishers, Genres rows (G1)', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);

		const table = await screen.findByRole('table', { name: 'Game information' });
		expect(table.querySelector('caption')!.className).toBe('visually-hidden');
		expect(table.parentElement!.classList).toContain('card');
		expect(table.closest('.table-responsive')).toBeNull();
		for (const td of table.querySelectorAll('td')) expect(td.classList).toContain('text-break');
		expect(table.querySelector('td .badge')!.classList).toContain('text-wrap');
		const rows = [...table.querySelectorAll('tr')].map((tr) => [
			tr.querySelector('th[scope="row"]')!.textContent!.trim(),
			tr.querySelector('td')!.textContent!.trim()
		]);
		expect(rows).toEqual([
			['System', 'Game Boy'],
			['Year', '1989'],
			['Developers', 'Nintendo'],
			['Publishers', '—'],
			['Genres', '—']
		]);
		expect(table.querySelector('td .badge')).not.toBeNull();
		expect(document.querySelector('dl')).toBeNull();
	});

	it('the action row: ZIP, ROM, Google, info.json, Collections, all default size (B1, B25)', async () => {
		answers.push({ status: 200, body: game([1]) });
		render(GamePage);

		const zip = await screen.findByRole('button', { name: 'Download ZIP' });
		const row = [...zip.parentElement!.children].filter((e) => e.matches('a, button'));
		expect(row.map((e) => e.textContent?.trim())).toEqual([
			'Download ZIP',
			'Download ROM',
			'Google',
			'View info.json',
			'Collections'
		]);
		for (const e of row) {
			expect(e.classList).not.toContain('btn-sm');
			expect(e.classList).not.toContain('align-self-center');
		}
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
