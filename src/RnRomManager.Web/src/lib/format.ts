/** Display formats (spec §11): times in the browser's local time zone, sizes in KiB/MiB. */

const KiB = 1024;
const MiB = KiB * 1024;

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const number = new Intl.NumberFormat(undefined, {
	maximumFractionDigits: 1,
	minimumFractionDigits: 1
});

/** An ISO-8601 UTC timestamp from the API, in local time; `''` for null/invalid. */
export function formatDateTime(iso: string | null | undefined): string {
	if (!iso) return '';
	const date = new Date(iso);
	return Number.isNaN(date.getTime()) ? '' : dateTime.format(date);
}

/** `512 B`, `1.5 KiB`, `16.0 MiB`. */
export function formatSize(bytes: number | null | undefined): string {
	if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '';
	if (bytes < KiB) return `${bytes} B`;
	if (bytes < MiB) return `${number.format(bytes / KiB)} KiB`;
	return `${number.format(bytes / MiB)} MiB`;
}
