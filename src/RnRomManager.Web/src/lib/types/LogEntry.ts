/** One event of `GET /api/admin/logs/stream` (L2); `time` is UTC ISO 8601. */
export interface LogEntry {
	id: number;
	time: string;
	level: 'Trace' | 'Debug' | 'Info' | 'Warn' | 'Error' | 'Fatal';
	logger: string;
	message: string;
	exception: string | null;
}
