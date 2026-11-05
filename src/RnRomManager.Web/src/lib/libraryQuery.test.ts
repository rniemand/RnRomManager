import { describe, expect, it } from 'vitest';
import { gamesApiUrl, parseFilters, toSearch, type LibraryFilters } from './libraryQuery';

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

	it('bad or stale values fall back to defaults', () => {
		expect(parse('letter=ab&sort=size&dir=up&page=-2&system=&q=%20a%20')).toEqual(parse(''));
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
});
