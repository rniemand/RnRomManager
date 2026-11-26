import type { CollectionListItem } from './CollectionListItem.js';

/** `GET /api/collections/{id}` and every collection edit's response (B7). */
export interface CollectionDetail extends CollectionListItem {
	notes: string | null;
	createdAt: string;
}
