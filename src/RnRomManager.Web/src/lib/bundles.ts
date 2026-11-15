import { api } from './api/client';
import { isApiError } from './api/errors';
import type { BundleEntry, BundleQueue, BundleQueueSummary } from './api/types';

/** B2: mirrors `BundleQueueLimits` on the server. */
export const maxQueueNameLength = 100;
export const maxQueuesPerUser = 100;

/** B2's name rule, as the server words it (code points, after trimming); null when valid. */
export function queueNameError(name: string): string | null {
	const trimmed = name.trim();
	if (!trimmed) return 'A name is required.';
	return [...trimmed].length > maxQueueNameLength
		? `The name must be at most ${maxQueueNameLength} characters.`
		: null;
}

/**
 * B2: queue names are unique per account, case-insensitive but accent-sensitive (`utf8mb4_uca1400_as_ci`). This is the
 * browser's approximation, used to offer an overwrite; the server's answer decides.
 */
export function sameQueueName(a: string, b: string): boolean {
	return a.trim().localeCompare(b.trim(), undefined, { sensitivity: 'accent' }) === 0;
}

const base = '/api/bundle-queues';
const one = (id: string) => `${base}/${encodeURIComponent(id)}`;

/** Only the wire fields of each entry. */
const wire = (entries: BundleEntry[]) =>
	entries.map((e) => ({
		gameId: e.gameId,
		releaseSha1: e.releaseSha1,
		member: e.member,
		gameTitle: e.gameTitle,
		fileName: e.fileName
	}));

/** B4: the caller's saved queues. */
export const bundleQueuesApi = {
	list: () => api.get<BundleQueueSummary[]>(base),
	get: (id: string) => api.get<BundleQueue>(one(id)),
	create: (name: string, entries: BundleEntry[]) =>
		api.post<BundleQueue>(base, { name: name.trim(), entries: wire(entries) }),
	replace: (id: string, name: string, entries: BundleEntry[], revision: number) =>
		api.put<BundleQueue>(one(id), { name: name.trim(), entries: wire(entries), revision }),
	remove: (id: string, revision: number) => api.delete<void>(`${one(id)}?revision=${revision}`)
};

/** The message for a failed queue call, by its B4 code. */
export function queueErrorMessage(error: unknown): string {
	if (isApiError(error, 'BundleQueueChanged'))
		return 'This queue was changed in another tab or browser. The list is reloaded; try again.';
	if (isApiError(error, 'BundleQueueNameTaken'))
		return 'You already have a saved queue with this name.';
	if (isApiError(error, 'BundleQueueLimit'))
		return `You can save at most ${maxQueuesPerUser} queues. Delete one first.`;
	if (isApiError(error) && error.status === 404)
		return 'This queue no longer exists. The list is reloaded.';
	if (isApiError(error, 'PayloadTooLarge')) return 'The queue is too large to save.';
	return error instanceof Error ? error.message : 'Something went wrong.';
}
