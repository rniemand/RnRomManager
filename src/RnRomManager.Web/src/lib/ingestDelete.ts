import { api } from './api/client';
import { isApiError } from './api/errors';
import type { IngestDeleteEntry, IngestDeleteResponse } from './api/types';
import { toasts } from './stores/toasts.svelte';

/** D2's per-request cap. */
export const deleteBatchSize = 500;
/** D5: entries listed in the confirmation modal. */
export const modalListSize = 10;

/** Selection key of a file in one system's table. */
export const fileKey = (system: string, name: string) => `${system}/${name}`;

/** D5: mirrors `ArchiveExtractor.IsArchive`; an archive always confirms. */
export const isArchive = (name: string) => /\.(zip|7z)$/i.test(name);

export const deleteTitle = (entries: IngestDeleteEntry[]) =>
	entries.length === 1 ? `Delete ${entries[0].name}` : `Delete ${entries.length} files`;

/**
 * D6: posts `entries` in batches of {@link deleteBatchSize}, stopping at the first error, and toasts the outcome.
 * The caller reloads its lists afterwards.
 */
export async function deleteIngestFiles(entries: IngestDeleteEntry[]): Promise<void> {
	let deleted = 0;
	const failed: IngestDeleteEntry[] = [];
	let error: unknown = null;
	for (let i = 0; i < entries.length; i += deleteBatchSize) {
		try {
			const result = await api.post<IngestDeleteResponse>('/api/ingest/delete', {
				files: entries.slice(i, i + deleteBatchSize)
			});
			deleted += result.deleted;
			failed.push(...result.failed);
		} catch (e) {
			error = e;
			break;
		}
	}
	if (deleted > 0)
		toasts.show(
			entries.length === 1 && deleted === 1
				? `Deleted ${entries[0].name}.`
				: `Deleted ${deleted} files.`,
			'success'
		);
	if (failed.length)
		toasts.show(
			`Couldn't delete: ${failed.map((f) => fileKey(f.system, f.name)).join(', ')}`,
			'danger'
		);
	if (error && !toasts.jobRunning(error))
		toasts.show(isApiError(error) ? error.title : "The files couldn't be deleted.", 'danger');
}
