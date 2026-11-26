import type { GroupSort, SortDir } from '#lib/enums/index.js';

/** Systems tab B2: the selected system id (as given, case-sensitive) and the one-based games page. */
export interface SystemsState {
	system: string | null;
	gamePage: number;
	/** B16: the list's search text; absent = none. */
	find?: string;
	/** G5: as {@link DiscoveryState}'s. */
	sort?: GroupSort;
	dir?: SortDir;
}
