import { describe, expect, it } from 'vitest';
import {
	facetsApiUrl,
	gamesApiUrl,
	hasFilters,
	parseFilters,
	toSearch,
	type LibraryFilters
} from './libraryQuery';

const parse = (search: string) => parseFilters(new URLSearchParams(search));

describe('library filters ↔ URL', () => {
	it('empty query → defaults → empty query', () => {
		const filters = parse('');
		expect(filters).toEqual({
			system: null,
			letter: null,
			region: null,
			q: '',
			sort: 'title',
			dir: null,
			favourite: false,
			page: 1
		});
		expect(toSearch(filters)).toBe('');
	});

	it('round trips every filter, letter # encoded as %23', () => {
		const filters: LibraryFilters = {
			system: 'gbc',
			letter: '#',
			region: 'USA, Europe',
			q: 'zelda & co',
			sort: 'year',
			dir: 'desc',
			favourite: true,
			page: 3
		};
		const search = toSearch(filters);

		expect(search).toContain('letter=%23');
		expect(parse(search)).toEqual(filters);
	});

	it('leaves out defaults (title asc, added desc, page 1)', () => {
		expect(toSearch({ ...parse(''), dir: 'asc' })).toBe('');
		expect(toSearch({ ...parse(''), sort: 'added', dir: 'desc' })).toBe('?sort=added');
		expect(toSearch({ ...parse(''), sort: 'added', dir: 'asc' })).toBe('?sort=added&dir=asc');
	});

	it('rating sort defaults to desc; favourite round trips', () => {
		expect(parse('sort=rating').dir).toBeNull();
		expect(toSearch({ ...parse(''), sort: 'rating', dir: 'desc' })).toBe('?sort=rating');
		expect(toSearch({ ...parse(''), sort: 'rating', dir: 'asc' })).toBe('?sort=rating&dir=asc');
		expect(parse('sort=rating&favourite=true')).toMatchObject({ sort: 'rating', favourite: true });
		expect(gamesApiUrl(parse('favourite=true&sort=rating'))).toBe(
			'/api/games?sort=rating&favourite=true&pageSize=48'
		);
	});

	it('hasFilters counts favourite, not the rating sort', () => {
		expect(hasFilters(parse('favourite=true'))).toBe(true);
		expect(hasFilters(parse('sort=rating'))).toBe(false);
	});

	it('bad or stale values fall back to defaults', () => {
		expect(parse('letter=ab&sort=size&dir=up&page=-2&system=&q=%20a%20&favourite=True')).toEqual(
			parse('')
		);
		expect(parse('letter=m&page=2.5').letter).toBe('M');
		expect(parse('page=2.5').page).toBe(1);
	});

	it('search needs 2 characters after trimming', () => {
		expect(parse('q=a').q).toBe('');
		expect(parse('q=%20ab%20').q).toBe('ab');
	});

	it('API URL carries the filters plus pageSize=48', () => {
		expect(gamesApiUrl(parse('system=gb&letter=%23&page=2'))).toBe(
			'/api/games?system=gb&letter=%23&page=2&pageSize=48'
		);
	});

	describe('a collection scope (B24)', () => {
		const scope = { collection: 'c1' };
		const parseIn = (search: string) => parseFilters(new URLSearchParams(search), scope);

		it('defaults to added-to-collection, newest first, and leaves that out of the URL', () => {
			const filters = parseIn('');
			expect(filters.sort).toBe('added');
			expect(toSearch(filters, scope)).toBe('');
			expect(toSearch({ ...filters, sort: 'title' }, scope)).toBe('?sort=title');
		});

		it('ignores letter and region, keeps favourite, system, q and page', () => {
			const filters = parseIn('letter=T&region=USA&favourite=true&system=gb&q=zelda&page=2');
			expect([filters.letter, filters.region]).toEqual([null, null]);
			expect(toSearch({ ...filters, letter: 'T', region: 'USA' }, scope)).toBe(
				'?system=gb&q=zelda&favourite=true&page=2'
			);
		});

		it('API URL carries the collection and the sort explicitly', () => {
			expect(gamesApiUrl(parseIn(''), scope)).toBe(
				'/api/games?collection=c1&sort=added&pageSize=48'
			);
			expect(gamesApiUrl(parseIn('sort=title&dir=desc'), scope)).toBe(
				'/api/games?sort=title&dir=desc&collection=c1&pageSize=48'
			);
		});
	});
});

describe('filter counts (F3)', () => {
	it('facetsApiUrl carries the filters but no sort, direction or page', () => {
		expect(facetsApiUrl(parse(''))).toBe('/api/games/facets');
		expect(
			facetsApiUrl(
				parse('system=gb&letter=%23&region=USA&q=fun&favourite=true&sort=year&dir=desc&page=3')
			)
		).toBe('/api/games/facets?system=gb&letter=%23&region=USA&q=fun&favourite=true');
	});
});
