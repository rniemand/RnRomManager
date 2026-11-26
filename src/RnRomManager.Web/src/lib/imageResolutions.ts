import type { ImageInfo, ImageResolutionFilter } from '#lib/types/index.js';
import { remembered } from './remembered';
import { pageParam } from '#lib/urlState.js';

type ImageType = ImageInfo['type'];

/** R10: the image type labels, shared by the game's Images tab and the Image Resolutions report. */
export const imageTypeLabels: Record<ImageType, string> = {
	BoxArt: 'Box art',
	Snap: 'Screenshot',
	TitleScreen: 'Title screen',
	Other: 'Other'
};

export const imageResolutionParams = ['system', 'type', 'maxWidth', 'maxHeight', 'page'] as const;

/** A whole number in 1–100000, or null for anything else (R6/R9). */
export function parseLimit(value: string | null | undefined): number | null {
	if (!value || !/^\d+$/.test(value.trim())) return null;
	const n = Number(value.trim());
	return n >= 1 && n <= 100000 ? n : null;
}

export function parseImageResolutionFilter(
	params: Pick<URLSearchParams, 'get'>
): ImageResolutionFilter {
	const type = params.get('type') ?? '';
	return {
		system: params.get('system') ?? '',
		type: Object.hasOwn(imageTypeLabels, type) ? (type as ImageType) : '',
		maxWidth: parseLimit(params.get('maxWidth')),
		maxHeight: parseLimit(params.get('maxHeight')),
		page: pageParam(params, 'page', 'digits')
	};
}

/** The query string for `f` (`?…`, or '' when nothing is set); defaults are left out. */
export function imageResolutionSearch(f: ImageResolutionFilter): string {
	const params = new URLSearchParams();
	if (f.system) params.set('system', f.system);
	if (f.type) params.set('type', f.type);
	if (f.maxWidth) params.set('maxWidth', String(f.maxWidth));
	if (f.maxHeight) params.set('maxHeight', String(f.maxHeight));
	if (f.page > 1) params.set('page', String(f.page));
	const text = params.toString();
	return text ? `?${text}` : '';
}

/** `GET /api/reports/image-resolutions` for this filter. */
export function imageResolutionApiUrl(f: ImageResolutionFilter, pageSize: number): string {
	let url = `/api/reports/image-resolutions?page=${f.page}&pageSize=${pageSize}`;
	if (f.system) url += `&system=${encodeURIComponent(f.system)}`;
	if (f.type) url += `&type=${f.type}`;
	if (f.maxWidth) url += `&maxWidth=${f.maxWidth}`;
	if (f.maxHeight) url += `&maxHeight=${f.maxHeight}`;
	return url;
}

const filter = remembered('rnrm.reports.image-resolutions');

/** The saved query string, or '' when none or storage is unavailable. */
export const readSavedImageResolutionFilter = filter.read;

/** Saves `search` (removes the key when empty); ignored when storage is unavailable. */
export const saveImageResolutionFilter = filter.save;
