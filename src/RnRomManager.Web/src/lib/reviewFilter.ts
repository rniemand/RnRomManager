import { remembered } from './remembered';
import type { ReviewFilter } from '#lib/types/index.js';
import { pageParam } from '#lib/urlState.js';

export const reviewFilterParams = ['q', 'system', 'reason', 'page'] as const;

export function parseReviewFilter(params: Pick<URLSearchParams, 'get'>): ReviewFilter {
	return {
		q: params.get('q')?.trim() ?? '',
		system: params.get('system') ?? '',
		reason: params.get('reason') ?? '',
		page: pageParam(params, 'page', 'integer')
	};
}

/** The query string for `f` (`?…`, or '' when nothing is set). */
export function reviewFilterSearch(f: ReviewFilter): string {
	const params = new URLSearchParams();
	if (f.q) params.set('q', f.q);
	if (f.system) params.set('system', f.system);
	if (f.reason) params.set('reason', f.reason);
	if (f.page > 1) params.set('page', String(f.page));
	const text = params.toString();
	return text ? `?${text}` : '';
}

/** `GET /api/review` for this filter (Pending items only). */
export function reviewApiUrl(f: ReviewFilter, pageSize: number): string {
	let url = `/api/review?status=Pending&page=${f.page}&pageSize=${pageSize}`;
	if (f.q) url += `&q=${encodeURIComponent(f.q)}`;
	if (f.system) url += `&system=${encodeURIComponent(f.system)}`;
	if (f.reason) url += `&reason=${encodeURIComponent(f.reason)}`;
	return url;
}

const filter = remembered('rnrm.review.filter');

/** The saved query string, or '' when none or storage is unavailable. */
export const readSavedReviewFilter = filter.read;

/** Saves `search` (removes the key when empty); ignored when storage is unavailable. */
export const saveReviewFilter = filter.save;
