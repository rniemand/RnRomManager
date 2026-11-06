/** Page sizes offered by the `Pagination` selector (T2). */
export const pageSizes = [10, 20, 50, 100] as const;

export const defaultPageSize = 20;

const key = (table: string) => `rnrm.pageSize.${table}`;

/** The stored page size for `table`, or 20 when missing, invalid or storage is unavailable (T3). */
export function readPageSize(table: string): number {
	try {
		const size = Number(localStorage.getItem(key(table)));
		return (pageSizes as readonly number[]).includes(size) ? size : defaultPageSize;
	} catch {
		return defaultPageSize;
	}
}

/** Remembers the page size per browser; ignored when storage is unavailable (private mode). */
export function writePageSize(table: string, size: number): void {
	try {
		localStorage.setItem(key(table), String(size));
	} catch {
		// Not persisted; the size still applies for this visit.
	}
}

/** Number of pages for `total` rows; at least 1 so "Page 1 of 1" stays meaningful. */
export function pageCount(total: number, size: number): number {
	return Math.max(1, Math.ceil(total / size));
}
