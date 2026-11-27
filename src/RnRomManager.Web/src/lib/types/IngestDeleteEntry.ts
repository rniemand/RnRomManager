/** One entry of `POST /api/ingest/delete` (D2). */
export interface IngestDeleteEntry {
	system: string;
	folder: 'pending' | 'failed' | 'removed' | 'duplicates';
	name: string;
}
