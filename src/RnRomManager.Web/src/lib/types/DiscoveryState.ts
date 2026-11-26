import type { DiscoveryKind, GroupSort, SortDir } from '#lib/enums/index.js';

/** A discovery tab's URL state; pages are one-based. */
export interface DiscoveryState {
	kind: DiscoveryKind;
	key: string | null;
	groupPage: number;
	gamePage: number;
	/** B16: the list's search text (URL `find`, since `q` is User collections'); absent = none. */
	find?: string;
	/** G5: the sort column; absent = `name`. */
	sort?: GroupSort;
	/** G5: the direction; absent = the column's default ({@link defaultDir}). */
	dir?: SortDir;
}
