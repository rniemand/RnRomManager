import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import type { SystemInfo } from '../api/types';

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: SystemList } = await import('./SystemList.svelte');

const system = (id: string, displayName: string, gameCount: number): SystemInfo => ({
	id,
	displayName,
	shortName: id.toUpperCase(),
	gameCount,
	romCount: gameCount,
	regions: []
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

		expect(links.map((l) => l.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
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
});
