/** Library filters (spec §11 Library row) ↔ the page URL query ↔ the `GET /api/games` query (§23). */

export type LibrarySort = 'title' | 'year' | 'added' | 'rating';
export type SortDir = 'asc' | 'desc';

export interface LibraryFilters {
	system: string | null;
	/** `#` or `A`–`Z`; null = All. */
	letter: string | null;
	region: string | null;
	/** Trimmed, ≥ 2 chars, or `''`. */
	q: string;
	sort: LibrarySort;
	/** null = the sort's default (§23: asc for title/year, desc for added and rating). */
	dir: SortDir | null;
	/** Only favourites (`?favourite=true`). */
	favourite: boolean;
	page: number;
}

export const libraryPageSize = 48;
export const letters = ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'];

/** The search box only filters from 2 characters (§11). */
export function normaliseSearch(value: string): string {
	const q = value.trim();
	return q.length >= 2 ? q : '';
}

export function defaultDir(sort: LibrarySort): SortDir {
	return sort === 'added' || sort === 'rating' ? 'desc' : 'asc';
}

/** Hand-edited or stale URLs fall back to defaults instead of failing. */
export function parseFilters(params: Pick<URLSearchParams, 'get'>): LibraryFilters {
	const letter = params.get('letter')?.toUpperCase() ?? null;
	const sort = params.get('sort');
	const dir = params.get('dir');
	const page = Number(params.get('page'));
	return {
		system: params.get('system') || null,
		letter: letter && letters.includes(letter) ? letter : null,
		region: params.get('region') || null,
		q: normaliseSearch(params.get('q') ?? ''),
		sort: sort === 'year' || sort === 'added' || sort === 'rating' ? sort : 'title',
		dir: dir === 'asc' || dir === 'desc' ? dir : null,
		favourite: params.get('favourite') === 'true',
		page: Number.isInteger(page) && page > 1 ? page : 1
	};
}

/** The page's query string (defaults left out), `''` when nothing is set. */
export function toSearch(filters: LibraryFilters): string {
	const params = new URLSearchParams();
	if (filters.system) params.set('system', filters.system);
	if (filters.letter) params.set('letter', filters.letter);
	if (filters.region) params.set('region', filters.region);
	if (filters.q) params.set('q', filters.q);
	if (filters.sort !== 'title') params.set('sort', filters.sort);
	if (filters.dir && filters.dir !== defaultDir(filters.sort)) params.set('dir', filters.dir);
	if (filters.favourite) params.set('favourite', 'true');
	if (filters.page > 1) params.set('page', String(filters.page));
	const search = params.toString();
	return search ? `?${search}` : '';
}

/** `GET /api/games` for these filters; `URLSearchParams` sends `letter=#` as `%23` (§23). */
export function gamesApiUrl(filters: LibraryFilters): string {
	const params = new URLSearchParams(toSearch(filters));
	params.set('pageSize', String(libraryPageSize));
	return `/api/games?${params}`;
}

export function hasFilters(filters: LibraryFilters): boolean {
	return !!(filters.system || filters.letter || filters.region || filters.q || filters.favourite);
}
