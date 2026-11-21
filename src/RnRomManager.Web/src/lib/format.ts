/** Display formats (spec §11): times in the browser's local time zone, sizes in KiB/MiB/GiB. */

const KiB = 1024;
const MiB = KiB * 1024;
const GiB = MiB * 1024;

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

/** L5: a count with thousands separators, `1250` → `1,250` (en-US, as the other counts). */
export function formatCount(n: number): string {
	return n.toLocaleString('en-US');
}

/** `512 B`, `1.5 KiB`, `16.0 MiB`, `8.0 GiB`. U4: `contentSize` in `static/emulator/params.js` copies it: keep the two in step. */
export function formatSize(bytes: number | null | undefined): string {
	if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '';
	if (bytes < KiB) return `${bytes} B`;
	if (bytes < MiB) return `${number.format(bytes / KiB)} KiB`;
	if (bytes < GiB) return `${number.format(bytes / MiB)} MiB`;
	return `${number.format(bytes / GiB)} GiB`;
}

/** B20: a duration in its largest two units, `12s`, `3m 4s`, `1h 2m`; `<1s` under a second (a zero second unit is dropped). */
export function formatDuration(ms: number): string {
	const total = Math.floor(ms / 1000);
	if (total < 1) return '<1s';
	const units: [number, string][] = [
		[Math.floor(total / 3600), 'h'],
		[Math.floor(total / 60) % 60, 'm'],
		[total % 60, 's']
	];
	const first = units.findIndex(([n]) => n > 0);
	return units
		.slice(first, first + 2)
		.filter(([n], i) => i === 0 || n > 0)
		.map(([n, unit]) => `${n}${unit}`)
		.join(' ');
}

/** `logoValid` → `Logo valid` (the generic header table, §11). */
export function formatKey(key: string): string {
	const words = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
	return words.charAt(0).toUpperCase() + words.slice(1);
}

/**
 * A header value for display: booleans Yes/No, null `—`, arrays/objects as JSON. U4: `counts` formats a number with
 * thousands separators (opt-in, so ports and years elsewhere keep plain digits).
 */
export function formatValue(value: unknown, options: { counts?: boolean } = {}): string {
	if (value === null || value === undefined) return '—';
	if (typeof value === 'boolean') return value ? 'Yes' : 'No';
	if (options.counts && typeof value === 'number') return formatCount(value);
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
