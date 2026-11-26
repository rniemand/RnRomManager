/** `GET /api/ingest/failed` (D1): the files in each system's `_failed/` folder; `reason` from its `.error.txt`. */
export interface FailedSystem {
	system: string;
	files: { name: string; sizeBytes: number; modifiedAt: string; reason: string | null }[];
}
