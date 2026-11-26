/** A summary entry shown as a two-column table rather than a badge. */
export interface SummaryTable {
	key: string;
	title: string;
	columns: [string, string];
	rows: [string, string][];
}
