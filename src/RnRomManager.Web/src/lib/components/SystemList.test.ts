import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { auth } from '../stores/auth.svelte';
import type { SystemInfo } from '../api/types';

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: SystemList } = await import('./SystemList.svelte');

afterEach(() => auth.set(null));

/** Text a reader gets: the decorative logo (`aria-hidden`) left out. */
const shownText = (el: Element) => {
	const copy = el.cloneNode(true) as Element;
	copy.querySelectorAll('[aria-hidden="true"]').forEach((n) => n.remove());
	return copy.textContent?.replace(/\s+/g, ' ').trim();
};

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
	system('nes', 'Nintendo Entertainment System', 12)
];

describe('SystemList (Systems tab B3)', () => {
	it('lists every system in order, 0 games included, with its count and link', () => {
		render(SystemList, { systems, selected: null });
		const links = within(screen.getByRole('list', { name: 'Systems' })).getAllByRole('link');

		expect(links.map(shownText)).toEqual([
			'Atari 2600 0 games',
			'Game Boy 1 game',
			'Nintendo Entertainment System 12 games'
		]);
		expect(links.map((l) => l.getAttribute('href'))).toEqual([
			'/collections?tab=systems&system=a2600',
			'/collections?tab=systems&system=gb',
			'/collections?tab=systems&system=nes'
		]);
		expect(links.some((l) => l.classList.contains('active'))).toBe(false);
	});

	it('marks the selected system active and current', () => {
		render(SystemList, { systems, selected: 'gb' });
		const selected = screen.getByRole('link', { name: /Game Boy/ });

		expect(selected.classList.contains('active')).toBe(true);
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
});
