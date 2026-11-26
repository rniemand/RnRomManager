export interface TimelineState {
	/** A registered system id, or null for every system. */
	system: string | null;
	/** The shown years, inclusive, always inside the data range with `from <= to`. */
	from: number;
	to: number;
	/** One-based. */
	page: number;
}
