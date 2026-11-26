/** `GET /api/bundle-queues` (B4). */
export interface BundleQueueSummary {
	id: string;
	name: string;
	revision: number;
	itemCount: number;
}
