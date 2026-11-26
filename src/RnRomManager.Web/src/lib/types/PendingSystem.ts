/** `GET /api/ingest/pending` (spec §23). */
export interface PendingSystem {
	system: string;
	files: { name: string; sizeBytes: number; modifiedAt: string }[];
}
