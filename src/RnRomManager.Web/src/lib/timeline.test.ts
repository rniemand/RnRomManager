import { describe, expect, it } from 'vitest';
import {
	pan,
	rangeLabel,
	timelineDisabled,
	timelineSearch,
	timelineState,
	timelineSystem,
	zoomIn,
	zoomOut
} from './timeline';

const url = (search: string) => new URL(`http://localhost/timeline${search}`);
const range = { min: 1980, max: 2020 };
const years = (from: number, to: number) => ({ from, to });

describe('timeline URL state (T3)', () => {
	it.each([
		['', null],
		['?system=gb', 'gb'],
		['?system=nope', null],
		['?system=', null]
	])('%s has system %s', (search, system) => {
		expect(timelineSystem(url(search), (id) => id === 'gb')).toBe(system);
	});

	it.each([
		['', 1980, 2020],
		['?from=1990&to=1999', 1990, 1999],
		['?from=abc&to=1999', 1980, 1999],
		['?from=1990.5', 1980, 2020],
		['?from=1970&to=2030', 1980, 2020],
		['?from=1999&to=1990', 1990, 1999],
		['?from=2030&to=1970', 1980, 2020],
		['?to=1975', 1980, 1980]
	])('%s shows %i–%i', (search, from, to) => {
		expect(timelineState(url(search), null, range)).toMatchObject({ from, to });
	});

	it.each([
		['', 1],
		['?page=3', 3],
		['?page=0', 1],
		['?page=-2', 1],
		['?page=x', 1],
		['?page=2.7', 2]
	])('%s is page %i', (search, page) => {
		expect(timelineState(url(search), 'gb', range)).toEqual({
			system: 'gb',
			from: 1980,
			to: 2020,
			page
		});
	});

	it('leaves out no system, the full range and page 1', () => {
		expect(timelineSearch({ system: null, from: 1980, to: 2020, page: 1 }, range)).toBe('');
		expect(timelineSearch({ system: 'gb', from: 1990, to: 1999, page: 2 }, range)).toBe(
			'?system=gb&from=1990&to=1999&page=2'
		);
		expect(timelineSearch({ system: null, from: 1980, to: 1999, page: 1 }, range)).toBe(
			'?from=1980&to=1999'
		);
	});

	it('a written state reads back the same', () => {
		const state = { system: 'gb', from: 1992, to: 1996, page: 3 };
		expect(timelineState(url(timelineSearch(state, range)), 'gb', range)).toEqual(state);
	});
});

describe('timeline zoom and pan (T4)', () => {
	it('zoom in halves the span, centred', () => {
		expect(zoomIn(years(1990, 1999))).toEqual(years(1992, 1996));
		expect(zoomIn(years(1990, 1990))).toEqual(years(1990, 1990));
		expect(zoomIn(years(1990, 1991))).toEqual(years(1990, 1990));
		expect(zoomIn(years(1990, 1992))).toEqual(years(1990, 1991));
	});

	it('zoom out doubles the span, centred and kept inside the range', () => {
		expect(zoomOut(years(1992, 1996), range)).toEqual(years(1990, 1999));
		expect(zoomOut(years(1980, 1984), range)).toEqual(years(1980, 1989));
		expect(zoomOut(years(2016, 2020), range)).toEqual(years(2011, 2020));
		expect(zoomOut(years(1990, 2019), range)).toEqual(years(1980, 2020));
	});

	it('earlier and later move by half the span, keeping it, inside the range', () => {
		expect(pan(years(1982, 1991), range, -1)).toEqual(years(1980, 1989));
		expect(pan(years(1990, 1999), range, -1)).toEqual(years(1985, 1994));
		expect(pan(years(1990, 1999), range, 1)).toEqual(years(1995, 2004));
		expect(pan(years(2008, 2017), range, 1)).toEqual(years(2011, 2020));
		expect(pan(years(1990, 1990), range, 1)).toEqual(years(1991, 1991));
	});

	it('disables what would do nothing', () => {
		expect(timelineDisabled(years(1980, 2020), range)).toEqual({
			zoomIn: false,
			zoomOut: true,
			showAll: true,
			earlier: true,
			later: true
		});
		expect(timelineDisabled(years(1990, 1990), range)).toEqual({
			zoomIn: true,
			zoomOut: false,
			showAll: false,
			earlier: false,
			later: false
		});
		expect(timelineDisabled(years(1980, 1989), range)).toMatchObject({
			earlier: true,
			later: false
		});
		expect(timelineDisabled(years(2011, 2020), range)).toMatchObject({
			earlier: false,
			later: true
		});
		expect(timelineDisabled(years(1995, 1995), { min: 1995, max: 1995 })).toEqual({
			zoomIn: true,
			zoomOut: true,
			showAll: true,
			earlier: true,
			later: true
		});
	});

	it('labels a range or a single year', () => {
		expect(rangeLabel(years(1990, 1999))).toBe('1990–1999');
		expect(rangeLabel(years(1990, 1990))).toBe('1990');
	});
});
