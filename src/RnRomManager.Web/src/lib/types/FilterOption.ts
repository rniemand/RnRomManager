/** One menu row; `search` adds texts the filter input also matches (e.g. a system's short name). */
export interface FilterOption {
	value: string;
	label: string;
	/** System menu (L3): a `SystemBadge` before the label. */
	badge?: { system: string; name: string };
	search?: string[];
}
