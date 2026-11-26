/** `POST /api/ingest/restore` (Deleted ROMs D5); `jobId` is null when no ingest job started. */
export interface RemovedRestoreResponse {
	restored: number;
	failed: { system: string; name: string }[];
	jobId: string | null;
}
