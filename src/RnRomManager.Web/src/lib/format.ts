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

/** `logoValid` → `Logo valid` (the generic header table, §11). */
export function formatKey(key: string): string {
	const words = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
	return words.charAt(0).toUpperCase() + words.slice(1);
}

/** A header value for display: booleans Yes/No, null `—`, arrays/objects as JSON. */
export function formatValue(value: unknown): string {
	if (value === null || value === undefined) return '—';
	if (typeof value === 'boolean') return value ? 'Yes' : 'No';
	if (typeof value === 'object') return JSON.stringify(value);
	return String(value);
}

/** `{ paths: { roms: '/app/roms' } }` → `[['paths.roms', '/app/roms']]`; arrays stay whole. */
export function flattenSettings(value: Record<string, unknown>, prefix = ''): [string, unknown][] {
	return Object.entries(value).flatMap(([key, child]): [string, unknown][] =>
		child !== null && typeof child === 'object' && !Array.isArray(child)
			? flattenSettings(child as Record<string, unknown>, `${prefix}${key}.`)
			: [[`${prefix}${key}`, child]]
	);
}
