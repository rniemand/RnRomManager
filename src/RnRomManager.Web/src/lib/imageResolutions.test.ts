import { describe, expect, it } from 'vitest';
import {
	imageResolutionApiUrl,
	imageResolutionSearch,
	parseImageResolutionFilter,
	parseLimit
} from './imageResolutions';

const parse = (query: string) => parseImageResolutionFilter(new URLSearchParams(query));

describe('image resolutions filter', () => {
	it('parses a full query', () => {
		expect(parse('system=gb&type=Snap&maxWidth=100&maxHeight=200&page=3')).toEqual({
			system: 'gb',
			type: 'Snap',
			maxWidth: 100,
			maxHeight: 200,
			page: 3
		});
	});

	it('falls back to defaults for bad values (R9)', () => {
		expect(parse('type=boxart&maxWidth=0&maxHeight=100001&page=0')).toEqual({
			system: '',
			type: '',
			maxWidth: null,
			maxHeight: null,
			page: 1
		});
		expect(parse('type=toString&maxWidth=1.5&maxHeight=abc&page=2.5')).toMatchObject({
			type: '',
			maxWidth: null,
			maxHeight: null,
			page: 1
		});
		expect(parse('system=nope').system).toBe('nope');
	});

	it('parseLimit accepts whole numbers 1–100000 only', () => {
		expect([parseLimit('1'), parseLimit('100000'), parseLimit(' 64 ')]).toEqual([1, 100000, 64]);
		expect([parseLimit(''), parseLimit('-1'), parseLimit('1e3'), parseLimit(null)]).toEqual([
			null,
			null,
			null,
			null
		]);
	});

	it('search drops defaults', () => {
		const none = { system: '', type: '', maxWidth: null, maxHeight: null, page: 1 } as const;
		expect(imageResolutionSearch(none)).toBe('');
		expect(imageResolutionSearch({ ...none, type: 'BoxArt', maxWidth: 100, page: 2 })).toBe(
			'?type=BoxArt&maxWidth=100&page=2'
		);
	});

	it('builds the API URL', () => {
		expect(
			imageResolutionApiUrl(
				{ system: 'gb', type: 'TitleScreen', maxWidth: null, maxHeight: 64, page: 2 },
				50
			)
		).toBe(
			'/api/reports/image-resolutions?page=2&pageSize=50&system=gb&type=TitleScreen&maxHeight=64'
		);
	});
});
