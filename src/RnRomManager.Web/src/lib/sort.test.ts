import { describe, expect, it } from 'vitest';
import { flip, nextSort, parseDir } from './sort';

describe('parseDir', () => {
	it.each([
		['asc', 'asc'],
		['desc', 'desc'],
		['ASC', null],
		['', null],
		[null, null],
		[undefined, null]
	])('%s → %s', (value, expected) => {
		expect(parseDir(value, null)).toBe(expected);
	});

	it('returns the fallback as given', () => {
		expect(parseDir('up', 'desc')).toBe('desc');
	});
});

describe('flip and nextSort', () => {
	const defaultDir = (key: string) => (key === 'name' ? 'asc' : 'desc');

	it('flip turns the direction around', () => {
		expect([flip('asc'), flip('desc')]).toEqual(['desc', 'asc']);
	});

	it('the active column flips its direction', () => {
		expect(nextSort('name', 'name', 'asc', defaultDir)).toEqual({ sort: 'name', dir: 'desc' });
	});

	it('another column starts at its default', () => {
		expect(nextSort('size', 'name', 'asc', defaultDir)).toEqual({ sort: 'size', dir: 'desc' });
	});
});
