/** `GET /api/admin/dats` (spec §23). */
export interface DatVersion {
	datName: string;
	version: string;
	downloadedAt: string;
}
