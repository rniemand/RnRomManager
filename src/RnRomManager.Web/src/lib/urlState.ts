import { goto } from '$app/navigation';

/**
 * A page number from the URL, 1 when absent or invalid. `floor`: any number, rounded down, at least 1; `integer`: a
 * whole number above 1; `digits`: only digits, at least 1.
 */
export function pageParam(
	params: Pick<URLSearchParams, 'get'>,
	name = 'page',
	mode: 'floor' | 'integer' | 'digits' = 'floor'
): number {
	const value = params.get(name);
	if (mode === 'floor') return Math.max(1, Math.floor(Number(value)) || 1);
	if (mode === 'integer') {
		const n = Number(value);
		return Number.isInteger(n) && n > 1 ? n : 1;
	}
	return value !== null && /^\d+$/.test(value) && Number(value) >= 1 ? Number(value) : 1;
}

/** Replaces the URL's history entry without resetting focus or scroll; each caller builds its own `href`. */
export function replaceUrl(href: string): Promise<void> {
	// eslint-disable-next-line svelte/no-navigation-without-resolve -- callers pass a resolved path or the same page's query
	return goto(href, { replace: true, reset: false });
}
