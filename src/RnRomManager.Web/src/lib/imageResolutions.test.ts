import { describe, expect, it } from 'vitest';
import {
	imageResolutionApiUrl,
	imageResolutionSearch,
	parseImageResolutionFilter,
	parseLimit,
	splitImages
} from './imageResolutions';
import type { ImageInfo } from '#lib/types/index.js';

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

describe('splitImages (IM2)', () => {
	const img = (id: number, type: ImageInfo['type'], isCover = false) =>
		({ id, type, isCover }) as ImageInfo;

	it('puts box art and the cover (any type) in Covers, cover first; the rest in Screenshots', () => {
		const images = [
			img(1, 'BoxArt'),
			img(2, 'Snap'),
			img(3, 'TitleScreen', true),
			img(4, 'BoxArt'),
			img(5, 'Other')
		];
		const { covers, screenshots } = splitImages(images);
		expect(covers.map((i) => i.id)).toEqual([3, 1, 4]);
		expect(screenshots.map((i) => i.id)).toEqual([2, 5]);
	});

	it('returns empty lists for no images', () => {
		expect(splitImages([])).toEqual({ covers: [], screenshots: [] });
	});
});
