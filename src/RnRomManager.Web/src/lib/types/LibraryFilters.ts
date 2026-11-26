import type { LibrarySort, SortDir } from '#lib/enums/index.js';

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
