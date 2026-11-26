import type { IngestDeleteEntry } from './IngestDeleteEntry.js';

/** `POST /api/ingest/delete` response (D2). */
export interface IngestDeleteResponse {
	deleted: number;
	failed: IngestDeleteEntry[];
}
