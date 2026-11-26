/** `GET /api/systems` (spec §23). */
/** Timeline T8: `GET /api/timeline`, the IGDB-linked games with a year, counted per year (ascending, years with games only). */
export interface TimelineYears {
	igdbConfigured: boolean;
	total: number;
	years: { year: number; count: number }[];
}
