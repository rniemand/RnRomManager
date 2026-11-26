import type { ZipEntry } from './ZipEntry.js';

/** `GET /api/games/{id}/zip-entries` (Z2): the ZIP's file name and its file entries, ordered by path. */
export interface ZipEntries {
	zipName: string;
	entries: ZipEntry[];
}
