/** Missing Metadata M2: one row of `GET /api/reports/missing-metadata`. */
export interface MissingMetadataItem {
	id: string;
	displayTitle: string;
	system: string;
	systemName: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
}
