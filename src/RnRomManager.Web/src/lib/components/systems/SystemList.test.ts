import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { auth } from '#lib/stores/auth.svelte.js';
import type { SystemInfo } from '#lib/types/index.js';

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: SystemList } = await import('./SystemList.svelte');

afterEach(() => auth.set(null));

/** Text a reader gets: the decorative logo (`aria-hidden`) left out. */
const shownText = (el: Element) => {
	const copy = el.cloneNode(true) as Element;
	copy.querySelectorAll('[aria-hidden="true"]').forEach((n) => n.remove());
	return copy.textContent?.replace(/\s+/g, ' ').trim();
};

/** G2: a row's Games cell (the second). */
const games = (link: Element) => link.closest('tr')!.cells[1].textContent!.trim();

const system = (id: string, displayName: string, gameCount: number): SystemInfo => ({
	id,
	displayName,
	shortName: id.toUpperCase(),
	gameCount,
	romCount: gameCount,
	regions: [],
	logoUrl: null
});
const systems = [
	system('a2600', 'Atari 2600', 0),
	system('gb', 'Game Boy', 1),
	system('nes', 'Nintendo Entertainment System', 1250)
];

describe('SystemList (Systems tab B3)', () => {
	it('lists every system in order, 0 games included, with its count and link', () => {
		render(SystemList, { systems, selected: null });
		const links = within(screen.getByRole('table', { name: 'Systems' })).getAllByRole('link');

		expect(links.map(shownText)).toEqual([
			'Atari 2600',
			'Game Boy',
			'Nintendo Entertainment System'
		]);
		expect(links.map(games)).toEqual(['0', '1', '1,250']);
		expect(links.map((l) => l.getAttribute('href'))).toEqual([
			'/collections?tab=systems&system=a2600',
			'/collections?tab=systems&system=gb',
			'/collections?tab=systems&system=nes'
		]);
		expect(document.querySelector('.table-primary')).toBeNull();
	});

	it('marks the selected system row and link current (G3)', () => {
		render(SystemList, { systems, selected: 'gb' });
		const selected = screen.getByRole('link', { name: 'Game Boy' });

		expect(selected.closest('tr')!.classList).toContain('table-primary');
		expect(selected.classList).toContain('text-reset');
		expect(screen.getByRole('link', { name: 'Atari 2600' }).classList).not.toContain('text-reset');
		expect(selected.getAttribute('aria-current')).toBe('true');
		expect(screen.getByRole('link', { name: /Atari/ }).getAttribute('aria-current')).toBeNull();
	});

	it("shows each system's shared logo, else a text banner of its name (B18)", () => {
		const shared = '/api/discovery/systems/logo?key=gb&v=2';
		render(SystemList, {
			systems: [{ ...systems[1], logoUrl: shared }, systems[2]],
			selected: null
		});

		expect(
			screen
				.getByRole('link', { name: /Game Boy/ })
				.querySelector('img')!
				.getAttribute('src')
		).toBe(shared);
		const nes = screen.getByRole('link', { name: /Nintendo/ });
		expect(nes.querySelector('img')).toBeNull();
		expect(nes.querySelector('.group-logo-banner')!.textContent).toBe(
			'Nintendo Entertainment System'
		);
	});

	it.each([
		[['admin'] as 'admin'[], 3],
		[[] as 'admin'[], 0]
	])('Change logo shows only for an Admin (roles %j), beside each row', async (roles, count) => {
		auth.set({ id: 1, username: 'u', roles, mustChangePassword: false });
		const onedit = vi.fn();
		render(SystemList, { systems, selected: null, onedit });

		const buttons = screen.queryAllByRole('button', { name: /^Change logo for / });
		expect(buttons).toHaveLength(count);
		if (count) {
			await fireEvent.click(screen.getByRole('button', { name: 'Change logo for Game Boy' }));
			expect(onedit).toHaveBeenCalledWith(systems[1]);
		}
	});

	it('names truncate on one line with the full name as title; the link can shrink (B13)', () => {
		render(SystemList, { systems, selected: null });
		const name = screen
			.getAllByTitle('Nintendo Entertainment System')
			.find((el) => !el.classList.contains('group-logo-banner'))!;
		expect(name.className).toBe('text-truncate');
		expect(name.parentElement!.classList).toContain('min-w-0');
		const cell = name.closest('td')!;
		expect(cell.className).toBe('w-100 text-truncate');
		expect(cell.getAttribute('style')).toBe('max-width: 0');
	});

	it('is a table in the reports style, with an Actions column only for an Admin with onedit (G1, G2)', () => {
		auth.set({ id: 1, username: 'u', roles: ['admin'], mustChangePassword: false });
		const { unmount } = render(SystemList, { systems, selected: null });
		const table = screen.getByRole('table', { name: 'Systems' });
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.closest('.card')).not.toBeNull();
		expect(table.querySelector('thead')!.className).toBe('table-dark');
		expect(screen.getAllByRole('columnheader')).toHaveLength(2);
		unmount();

		render(SystemList, { systems, selected: null, onedit: vi.fn() });
		const headers = screen.getAllByRole('columnheader');
		expect(headers).toHaveLength(3);
		expect(headers[2].querySelector('.visually-hidden')!.textContent).toBe('Actions');
		expect(screen.getByRole('button', { name: 'Change logo for Game Boy' }).className).toBe(
			'btn btn-link btn-sm p-0'
		);
	});

	it('a filter matching nothing hides the card', () => {
		render(SystemList, { systems, selected: null, find: 'zzz' });
		expect(document.querySelector('.card')!.hasAttribute('hidden')).toBe(true);
	});

	describe('sorting (G4) and Clear (G7)', () => {
		const tied = [
			system('b', 'beta', 5),
			system('a', 'Alpha', 5),
			system('c', 'Gamma', 9),
			system('d', 'delta', 1)
		];
		const order = () =>
			within(screen.getByRole('table', { name: 'Systems' }))
				.getAllByRole('link')
				.map(shownText);
		const header = (name: string) => screen.getByRole('button', { name: new RegExp(`^${name}`) });

		it.each([
			[undefined, undefined, ['Alpha', 'beta', 'delta', 'Gamma']],
			['name', 'desc', ['Gamma', 'delta', 'beta', 'Alpha']],
			['games', undefined, ['Gamma', 'Alpha', 'beta', 'delta']],
			['games', 'asc', ['delta', 'Alpha', 'beta', 'Gamma']]
		] as const)('sort %s %s orders in the browser, games ties by name', (sort, dir, expected) => {
			render(SystemList, { systems: tied, selected: null, sort, dir });
			expect(order()).toEqual(expected);
			const active = header(sort === 'games' ? 'Games' : 'Name').closest('th')!;
			expect(active.getAttribute('aria-sort')).toBe(
				(dir ?? (sort === 'games' ? 'desc' : 'asc')) === 'asc' ? 'ascending' : 'descending'
			);
			expect(active.querySelector('i')).not.toBeNull();
		});

		it.each([
			[{}, 'Name', ['name', 'desc']],
			[{}, 'Games', ['games', 'desc']],
			[{ sort: 'games' as const }, 'Games', ['games', 'asc']],
			[{ sort: 'games' as const, dir: 'asc' as const }, 'Name', ['name', 'asc']]
		])('from %j clicking %s calls onsort(%j)', async (extra, click, expected) => {
			const onsort = vi.fn();
			render(SystemList, { systems, selected: null, onsort, ...extra });
			await fireEvent.click(header(click));
			expect(onsort).toHaveBeenCalledWith(...expected);
		});

		it('keeps sort and dir in the selection links', () => {
			render(SystemList, { systems, selected: null, sort: 'games', dir: 'asc' });
			expect(screen.getByRole('link', { name: 'Game Boy' }).getAttribute('href')).toBe(
				'/collections?tab=systems&system=gb&sort=games&dir=asc'
			);
		});

		it('no Clear at the default; Clear cancels a pending search, empties the box and calls onclear', async () => {
			const { unmount } = render(SystemList, { systems, selected: null });
			expect(screen.queryByRole('button', { name: /Clear/ })).toBeNull();
			unmount();

			const onclear = vi.fn();
			const onfind = vi.fn();
			render(SystemList, { systems, selected: null, sort: 'games', onclear, onfind });
			const box = screen.getByLabelText('Search systems') as HTMLInputElement;
			await fireEvent.input(box, { target: { value: 'nes' } });
			const button = screen.getByRole('button', { name: /Clear/ });
			expect(button.className).toBe('btn btn-sm btn-outline-secondary text-nowrap');
			await fireEvent.click(button);
			await new Promise((done) => setTimeout(done, 350));
			expect(onclear).toHaveBeenCalledOnce();
			expect(onfind).not.toHaveBeenCalled();
			expect(box.value).toBe('');
		});
	});

	describe('search (B16)', () => {
		const names = () =>
			[...document.querySelectorAll('table[aria-label="Systems"] a')].map((l) => shownText(l));

		it('filters by id, case-insensitive, keeping find in the links', () => {
			render(SystemList, { systems, selected: null, find: 'NES' });
			expect(names()).toEqual(['Nintendo Entertainment System']);
			expect(screen.getByRole('link', { name: /Nintendo/ }).getAttribute('href')).toBe(
				'/collections?tab=systems&system=nes&find=NES'
			);
		});

		it('filters by display name too', () => {
			render(SystemList, { systems, selected: null, find: 'atari' });
			expect(names()).toEqual(['Atari 2600']);
		});

		it('no match shows its own empty state', () => {
			render(SystemList, { systems, selected: null, find: 'zzz' });
			expect(screen.getByText('No systems match “zzz”.')).toBeTruthy();
			expect(names()).toEqual([]);
		});

		it('typing calls onfind after a pause', async () => {
			const onfind = vi.fn();
			render(SystemList, { systems, selected: null, onfind });
			const box = screen.getByLabelText('Search systems') as HTMLInputElement;
			expect(box.maxLength).toBe(100);

			await fireEvent.input(box, { target: { value: ' gb ' } });
			await new Promise((done) => setTimeout(done, 350));
			expect(onfind).toHaveBeenCalledWith('gb');
		});

		it('a pending search is dropped on unmount', async () => {
			const onfind = vi.fn();
			const { unmount } = render(SystemList, { systems, selected: null, onfind });

			await fireEvent.input(screen.getByLabelText('Search systems'), { target: { value: 'nes' } });
			unmount();
			await new Promise((done) => setTimeout(done, 350));
			expect(onfind).not.toHaveBeenCalled();
		});
	});
});
