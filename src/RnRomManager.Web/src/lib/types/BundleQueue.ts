import type { BundleEntry } from './BundleEntry.js';

/** `GET /api/bundle-queues/{id}` and the create/replace answers (B4). */
export interface BundleQueue {
	id: string;
	name: string;
	revision: number;
	entries: BundleEntry[];
}
