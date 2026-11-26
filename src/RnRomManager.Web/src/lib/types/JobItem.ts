/** `GET /api/jobs/{id}/items` (spec §14.1 `job_items`). */
export interface JobItem {
	id: number;
	fileId: string;
	systemId: string | null;
	sourcePath: string;
	archiveEntry: string | null;
	outcome: string;
	reason: string | null;
	details: string | null;
	gameId: string | null;
	sha1: string | null;
	createdAt: string;
}
