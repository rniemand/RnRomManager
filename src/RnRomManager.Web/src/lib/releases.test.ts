import { describe, expect, it } from 'vitest';
import { compareUpdates, isUpdate } from './releases.js';

const rom = (fileName: string, over: Partial<Parameters<typeof compareUpdates>[0]> = {}) => ({
	fileName,
	version: null as string | null,
	revision: null as string | null,
	dateAdded: '2026-11-01T00:00:00Z',
	...over
});
const order = (...roms: ReturnType<typeof rom>[]) =>
	[...roms].sort(compareUpdates).map((r) => r.fileName);

describe('compareUpdates (B11)', () => {
	it('orders by version, higher first, numerically per segment', () => {
		expect(
			order(
				rom('a', { version: '1.9.0' }),
				rom('b', { version: '1.10.0' }),
				rom('c', { version: '2.0' })
			)
		).toEqual(['c', 'b', 'a']);
	});

	it('puts a release with a version before one without', () => {
		expect(order(rom('none', { revision: 'v9' }), rom('versioned', { version: '0.1' }))).toEqual([
			'versioned',
			'none'
		]);
	});

	it('treats missing segments as 0, then falls back to the revision', () => {
		expect(
			order(
				rom('a', { version: '1.0', revision: 'v1' }),
				rom('b', { version: '1.0.0', revision: 'v2' })
			)
		).toEqual(['b', 'a']);
	});

	it('uses the v<N> revision when versions tie, none counting as 0', () => {
		expect(
			order(rom('none'), rom('v65536', { revision: 'v65536' }), rom('v1', { revision: 'v1' }))
		).toEqual(['v65536', 'v1', 'none']);
	});

	it('then the newer dateAdded, then fileName ascending', () => {
		expect(
			order(
				rom('old', { dateAdded: '2026-01-01T00:00:00Z' }),
				rom('new', { dateAdded: '2026-06-01T00:00:00Z' })
			)
		).toEqual(['new', 'old']);
		expect(order(rom('b'), rom('B'), rom('a'))).toEqual(['B', 'a', 'b']);
	});
});

describe('isUpdate', () => {
	it('is true only for the Update flag', () => {
		expect(isUpdate({ flags: ['Update'] })).toBe(true);
		expect(isUpdate({ flags: ['DLC'] })).toBe(false);
		expect(isUpdate({ flags: ['update'] })).toBe(false);
	});
});
