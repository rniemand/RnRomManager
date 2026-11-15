import { describe, expect, it } from 'vitest';
import {
	collectionsTab,
	discoverySearch,
	discoveryState,
	isDiscoveryKey,
	tabSearch
} from './discovery';

const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
const url = (search: string) => new URL(`http://localhost/collections${search}`);

describe('collections tab URLs (discovery B1)', () => {
	it.each([
		['', 'collections'],
		['?tab=publishers', 'publishers'],
		['?tab=genres', 'genres'],
		['?tab=Publishers', 'collections'],
		['?tab=developers', 'collections'],
		['?tab=', 'collections']
	])('%s is the %s tab', (search, tab) => {
		expect(collectionsTab(url(search))).toBe(tab);
	});

	it('reads the selection and both pages, one-based', () => {
		expect(discoveryState(url(`?tab=genres&key=${key}&groupPage=3&gamePage=2`))).toEqual({
			kind: 'genres',
			key,
			groupPage: 3,
			gamePage: 2
		});
		expect(discoveryState(url('?tab=publishers&groupPage=0&gamePage=-4'))).toEqual({
			kind: 'publishers',
			key: null,
			groupPage: 1,
			gamePage: 1
		});
		expect(discoveryState(url('?q=zel'))).toBeNull();
	});

	it.each([key.toUpperCase(), key.slice(1), `${key}0`, 'nintendo', ''])(
		'a malformed key %j selects nothing',
		(bad) => {
			expect(isDiscoveryKey(bad)).toBe(false);
			expect(discoveryState(url(`?tab=publishers&key=${bad}`))?.key).toBeNull();
		}
	);

	it('writes only what differs from the defaults, and game paging only with a selection', () => {
		expect(discoverySearch({ kind: 'publishers', key: null, groupPage: 1, gamePage: 1 })).toBe(
			'?tab=publishers'
		);
		expect(discoverySearch({ kind: 'genres', key, groupPage: 2, gamePage: 3 })).toBe(
			`?tab=genres&key=${key}&groupPage=2&gamePage=3`
		);
		expect(discoverySearch({ kind: 'genres', key: null, groupPage: 2, gamePage: 3 })).toBe(
			'?tab=genres&groupPage=2'
		);
	});

	it('tab links clear selection and paging', () => {
		expect(tabSearch('collections')).toBe('');
		expect(tabSearch('publishers')).toBe('?tab=publishers');
		expect(tabSearch('genres')).toBe('?tab=genres');
	});

	it('a written state reads back the same', () => {
		const state = { kind: 'publishers' as const, key, groupPage: 4, gamePage: 2 };
		expect(discoveryState(url(discoverySearch(state)))).toEqual(state);
	});
});
