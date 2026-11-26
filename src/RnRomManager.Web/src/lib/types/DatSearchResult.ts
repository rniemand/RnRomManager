/** `GET /api/dat/search` (spec §23). */
export interface DatSearchResult {
	gameName: string;
	romName: string;
	sizeBytes: number;
	crc32: string;
}
