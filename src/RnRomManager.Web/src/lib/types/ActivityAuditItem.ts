/** UI polish B21: one audit row in the Activity report (the `/api/admin/audit` item plus `gameTitle`). */
export interface ActivityAuditItem {
	id: number;
	occurredAt: string;
	action: string;
	system: string | null;
	gameId: string | null;
	user: string | null;
	jobId: string | null;
	path: string | null;
	targetPath: string | null;
	details: string | null;
	/** Null when the game is gone. */
	gameTitle: string | null;
}
