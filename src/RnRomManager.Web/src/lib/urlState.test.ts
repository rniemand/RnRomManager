import { describe, expect, it, vi } from 'vitest';

const goto = vi.hoisted(() => vi.fn(() => Promise.resolve()));
vi.mock('$app/navigation', () => ({ goto }));

const { pageParam, replaceUrl } = await import('./urlState');

const params = (page: string | null, name = 'page') =>
	new URLSearchParams(page === null ? '' : `${name}=${encodeURIComponent(page)}`);

describe('pageParam', () => {
	it.each([
		[null, 1],
		['', 1],
		['0', 1],
		['-3', 1],
		['1', 1],
		['3', 3],
		['2.5', 2],
		['1e1', 10],
		['abc', 1]
	])('floor: %s → %i', (value, expected) => {
		expect(pageParam(params(value))).toBe(expected);
	});

	it.each([
		[null, 1],
		['', 1],
		['0', 1],
		['1', 1],
		['3', 3],
		['2.5', 1],
		['1e1', 10],
		['abc', 1]
	])('integer: %s → %i', (value, expected) => {
		expect(pageParam(params(value), 'page', 'integer')).toBe(expected);
	});

	it.each([
		[null, 1],
		['', 1],
		['0', 1],
		['1', 1],
		['3', 3],
		['2.5', 1],
		['1e1', 1],
		[' 3', 1]
	])('digits: %s → %i', (value, expected) => {
		expect(pageParam(params(value), 'page', 'digits')).toBe(expected);
	});

	it('reads the named parameter', () => {
		expect(pageParam(params('4', 'gamePage'), 'gamePage')).toBe(4);
		expect(pageParam(params('4', 'gamePage'))).toBe(1);
	});
});

describe('replaceUrl', () => {
	it('replaces the history entry without resetting focus or scroll', async () => {
		await replaceUrl('/review?page=2');
		expect(goto).toHaveBeenCalledWith('/review?page=2', { replace: true, reset: false });
	});
});
