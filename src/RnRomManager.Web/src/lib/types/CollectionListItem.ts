/** `GET /api/collections` item (B6). */
export interface CollectionListItem {
	id: string;
	name: string;
	gameCount: number;
	/** `/api/collections/{id}/cover?v=…`, or null without a cover. */
	coverUrl: string | null;
	updatedAt: string;
}
