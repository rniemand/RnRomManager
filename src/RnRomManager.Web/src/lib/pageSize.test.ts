import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { pageCount, readPageSize, writePageSize } from './pageSize';

// Stub storage instead of using jsdom's: Node 25's own localStorage global shadows it (as in theme.svelte.test.ts).
let store: Map<string, string>;
const storage = {
	getItem: (key: string) => store.get(key) ?? null,
	setItem: (key: string, value: string) => void store.set(key, value)
};

describe('pageSize', () => {
	beforeEach(() => {
		store = new Map();
		vi.stubGlobal('localStorage', storage);
	});
	afterEach(() => vi.unstubAllGlobals());

	it('defaults to 20 and round-trips a valid size per table', () => {
		expect(readPageSize('review')).toBe(20);
		writePageSize('review', 50);
		expect(localStorage.getItem('rnrm.pageSize.review')).toBe('50');
		expect(readPageSize('review')).toBe(50);
		expect(readPageSize('other')).toBe(20);
	});

	it.each(['7', 'abc', '', '1000'])('an invalid stored value (%s) reads as 20', (value) => {
		localStorage.setItem('rnrm.pageSize.review', value);
		expect(readPageSize('review')).toBe(20);
	});

	it('storage throwing (private mode) reads 20 and writes nothing', () => {
		const deny = () => {
			throw new Error('denied');
		};
		vi.stubGlobal('localStorage', { getItem: deny, setItem: deny });
		expect(readPageSize('review')).toBe(20);
		expect(() => writePageSize('review', 50)).not.toThrow();
	});

	it('a fallback replaces 20 when nothing valid is stored (IN5)', () => {
		expect(readPageSize('integrity-issues', 10)).toBe(10);
		localStorage.setItem('rnrm.pageSize.integrity-issues', 'abc');
		expect(readPageSize('integrity-issues', 10)).toBe(10);
		writePageSize('integrity-issues', 50);
		expect(readPageSize('integrity-issues', 10)).toBe(50);
		vi.stubGlobal('localStorage', {
			getItem: () => {
				throw new Error('denied');
			}
		});
		expect(readPageSize('integrity-issues', 10)).toBe(10);
	});

	it('pageCount rounds up and is at least 1', () => {
		expect([pageCount(0, 20), pageCount(20, 20), pageCount(21, 20), pageCount(45, 20)]).toEqual([
			1, 1, 2, 3
		]);
	});
});
