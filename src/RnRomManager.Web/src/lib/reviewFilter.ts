/** RV4: the review queue's filter and page, kept in the URL query and remembered in `localStorage`. */
export interface ReviewFilter {
	q: string;
	system: string;
	reason: string;
	page: number;
}

export const reviewFilterParams = ['q', 'system', 'reason', 'page'] as const;

export function parseReviewFilter(params: Pick<URLSearchParams, 'get'>): ReviewFilter {
	const n = Number(params.get('page'));
	return {
		q: params.get('q')?.trim() ?? '',
		system: params.get('system') ?? '',
		reason: params.get('reason') ?? '',
		page: Number.isInteger(n) && n > 1 ? n : 1
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

const storageKey = 'rnrm.review.filter';

/** The saved query string, or '' when none or storage is unavailable. */
export function readSavedReviewFilter(): string {
	try {
		return localStorage.getItem(storageKey) ?? '';
	} catch {
		return '';
	}
}

/** Saves `search` (removes the key when empty); ignored when storage is unavailable. */
export function saveReviewFilter(search: string): void {
	try {
		if (search) localStorage.setItem(storageKey, search);
		else localStorage.removeItem(storageKey);
	} catch {
		// remembering the filter is a convenience
	}
}
