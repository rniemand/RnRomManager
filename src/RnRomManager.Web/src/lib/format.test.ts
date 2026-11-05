import { describe, expect, it } from 'vitest';
import { formatDateTime, formatSize } from './format';

// The number part follows the browser's locale, so expectations are built the same way.
const oneDecimal = (n: number) =>
	new Intl.NumberFormat(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(
		n
	);

describe('formatSize', () => {
	it.each([
		[0, '0 B'],
		[512, '512 B'],
		[1023, '1023 B']
	])('%d -> %s', (bytes, expected) => expect(formatSize(bytes)).toBe(expected));

	it.each([
		[1024, 1, 'KiB'],
		[1536, 1.5, 'KiB'],
		[1024 * 1024 - 1, 1024, 'KiB'],
		[1024 * 1024, 1, 'MiB'],
		[1.25 * 1024 * 1024, 1.25, 'MiB'],
		[16 * 1024 * 1024, 16, 'MiB']
	])('%d bytes -> %d %s', (bytes, value, unit) =>
		expect(formatSize(bytes)).toBe(`${oneDecimal(value)} ${unit}`)
	);

	it('is empty for null, negative or non-numbers', () => {
		expect([
			formatSize(null),
			formatSize(undefined),
			formatSize(-1),
			formatSize(Number.NaN)
		]).toEqual(['', '', '', '']);
	});
});

describe('formatDateTime', () => {
	it('shows a UTC timestamp in local time', () => {
		const iso = '2026-11-04T12:30:00Z';
		const expected = new Intl.DateTimeFormat(undefined, {
			dateStyle: 'medium',
			timeStyle: 'short'
		}).format(new Date(iso));

		expect(formatDateTime(iso)).toBe(expected);
		expect(formatDateTime(iso)).not.toBe('');
	});

	it('is empty for null or garbage', () => {
		expect([formatDateTime(null), formatDateTime(''), formatDateTime('not a date')]).toEqual([
			'',
			'',
			''
		]);
	});
});
