/** Missing IGDB IDs R1: one row of `GET /api/reports/missing-igdb-ids`. */
export interface MissingIgdbItem {
	id: string;
	displayTitle: string;
	system: string;
	systemName: string;
	releaseYear: number | null;
}
