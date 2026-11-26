/** One item of `GET /api/integrity/issues` (spec §14.1 `integrity_issues`). */
export interface IntegrityIssue {
	id: number;
	type: string;
	severity: 'Error' | 'Warn' | 'Info';
	system: string | null;
	zipPath: string | null;
	gameId: string | null;
	entryName: string | null;
	details: string | null;
	status: 'Open' | 'AutoRepaired' | 'Resolved';
	firstSeenJobId: string;
	lastSeenJobId: string;
	detectedAt: string;
	resolvedAt: string | null;
}
