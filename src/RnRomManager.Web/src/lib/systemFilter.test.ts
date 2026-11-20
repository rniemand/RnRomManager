import { describe, expect, it } from 'vitest';
import type { SystemInfo } from './api/types';
import { systemFilterOptions } from './systemFilter';

const system = (id: string, displayName: string, shortName: string, romCount: number) =>
	({ id, displayName, shortName, romCount }) as SystemInfo;

describe('systemFilterOptions (F1)', () => {
	const systems = [
		system('gb', 'Game Boy', 'GB', 3),
		system('nes', 'NES', 'NES', 0),
		system('snes', 'SNES', 'SNES', 1)
	];

	it('lists systems with ROMs, with a badge and the short name as a search term', () => {
		expect(systemFilterOptions(systems, null)).toEqual([
			{ value: 'gb', label: 'Game Boy', badge: { system: 'gb', name: 'GB' }, search: ['GB'] },
			{ value: 'snes', label: 'SNES', badge: { system: 'snes', name: 'SNES' }, search: ['SNES'] }
		]);
	});

	it('keeps the selected system even without ROMs, in list order', () => {
		expect(systemFilterOptions(systems, 'nes').map((o) => o.value)).toEqual(['gb', 'nes', 'snes']);
	});
});
