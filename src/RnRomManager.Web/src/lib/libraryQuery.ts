import type { LibraryFilters, QueryScope } from '#lib/types/index.js';
import type { SortDir, LibrarySort } from '#lib/enums/index.js';
import { parseDir } from '#lib/sort.js';
import { pageParam } from '#lib/urlState.js';

/** Library filters (spec §11 Library row) ↔ the page URL query ↔ the `GET /api/games` query (§23). */

export const libraryPageSize = 48;

/** The sort the URL leaves out: title for the Library, added-to-collection for a collection (B24). */
export function defaultSort(scope?: QueryScope): LibrarySort {
	return scope ? 'added' : 'title';
}
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
export function parseFilters(
	params: Pick<URLSearchParams, 'get'>,
	scope?: QueryScope
): LibraryFilters {
	const letter = params.get('letter')?.toUpperCase() ?? null;
	const sort = params.get('sort');
	const dir = params.get('dir');
	return {
		system: params.get('system') || null,
		letter: !scope && letter && letters.includes(letter) ? letter : null,
		region: (!scope && params.get('region')) || null,
		q: normaliseSearch(params.get('q') ?? ''),
		sort:
			sort === 'title' || sort === 'year' || sort === 'added' || sort === 'rating'
				? sort
				: defaultSort(scope),
		dir: parseDir(dir, null),
		favourite: params.get('favourite') === 'true',
		page: pageParam(params, 'page', 'integer')
	};
}

/** The page's query string (defaults left out), `''` when nothing is set. */
export function toSearch(filters: LibraryFilters, scope?: QueryScope): string {
	const params = new URLSearchParams();
	if (filters.system) params.set('system', filters.system);
	if (filters.letter && !scope) params.set('letter', filters.letter);
	if (filters.region && !scope) params.set('region', filters.region);
	if (filters.q) params.set('q', filters.q);
	if (filters.sort !== defaultSort(scope)) params.set('sort', filters.sort);
	if (filters.dir && filters.dir !== defaultDir(filters.sort)) params.set('dir', filters.dir);
	if (filters.favourite) params.set('favourite', 'true');
	if (filters.page > 1) params.set('page', String(filters.page));
	const search = params.toString();
	return search ? `?${search}` : '';
}

/** `GET /api/games` for these filters; `URLSearchParams` sends `letter=#` as `%23` (§23). */
export function gamesApiUrl(filters: LibraryFilters, scope?: QueryScope): string {
	const params = new URLSearchParams(toSearch(filters, scope));
	if (scope) {
		params.set('collection', scope.collection);
		// The API's own default is title.
		params.set('sort', filters.sort);
	}
	params.set('pageSize', String(libraryPageSize));
	return `/api/games?${params}`;
}

/** F3: `GET /api/games/facets` for the filters only, so paging and sorting never change it (no refetch). */
export function facetsApiUrl(filters: LibraryFilters): string {
	return `/api/games/facets${toSearch({ ...filters, sort: 'title', dir: null, page: 1 })}`;
}

export function hasFilters(filters: LibraryFilters): boolean {
	return !!(filters.system || filters.letter || filters.region || filters.q || filters.favourite);
}
