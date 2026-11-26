/** T2: `GET /api/admin/actions`, the Admin Actions tab's counts. */
export interface AdminActions {
	igdbConfigured: boolean;
	igdbLinked: number;
	coversPending: number;
	lastIgdbSyncAt: string | null;
}
