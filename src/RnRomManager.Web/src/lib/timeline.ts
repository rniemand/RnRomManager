/**
 * Timeline T3/T4: the `/timeline` URL state (`?system=&from=&to=&page=`) and the zoom and pan math over the data range
 * (the min and max year `GET /api/timeline` returns for the current system).
 */

/** The years with games for the current system, inclusive. */
export interface YearRange {
	min: number;
	max: number;
}

export interface TimelineState {
	/** A registered system id, or null for every system. */
	system: string | null;
	/** The shown years, inclusive, always inside the data range with `from <= to`. */
	from: number;
	to: number;
	/** One-based. */
	page: number;
}

/** What the helpers read of a URL (SvelteKit's `page.url` has read-only search params). */
type UrlLike = { readonly searchParams: { get(name: string): string | null } };

/** T3: the `system` param when it names a registered system, else null (treated as absent). */
export function timelineSystem(url: UrlLike, isSystem: (id: string) => boolean): string | null {
	const system = url.searchParams.get('system');
	return system && isSystem(system) ? system : null;
}

/** A whole year, or null when missing or not a whole number. */
function year(value: string | null): number | null {
	if (value === null || value.trim() === '') return null;
	const n = Number(value);
	return Number.isInteger(n) ? n : null;
}

const clamp = (n: number, range: YearRange) => Math.min(range.max, Math.max(range.min, n));

/**
 * T3: the state from the URL for `system`'s data range. A missing or non-numeric bound is the range's; a bound outside
 * it is clamped; `from` after `to` is swapped. `page` is a whole number ≥ 1 (default 1).
 */
export function timelineState(
	url: UrlLike,
	system: string | null,
	range: YearRange
): TimelineState {
	let from = clamp(year(url.searchParams.get('from')) ?? range.min, range);
	let to = clamp(year(url.searchParams.get('to')) ?? range.max, range);
	if (from > to) [from, to] = [to, from];
	const page = Math.max(1, Math.floor(Number(url.searchParams.get('page'))) || 1);
	return { system, from, to, page };
}

/** T3: the query for a state; no system, the full data range and page 1 are left out (`''` when all are). */
export function timelineSearch(state: TimelineState, range: YearRange): string {
	const params = new URLSearchParams();
	if (state.system) params.set('system', state.system);
	if (state.from !== range.min || state.to !== range.max) {
		params.set('from', String(state.from));
		params.set('to', String(state.to));
	}
	if (state.page > 1) params.set('page', String(state.page));
	const search = params.toString();
	return search ? `?${search}` : '';
}

type Years = Pick<TimelineState, 'from' | 'to'>;

const span = ({ from, to }: Years) => to - from + 1;

/** T4 Zoom in: half the span (rounded up), centred. 1990–1999 → 1992–1996. */
export function zoomIn(years: Years): Years {
	const n = Math.max(1, Math.ceil(span(years) / 2));
	const from = years.from + Math.floor((span(years) - n) / 2);
	return { from, to: from + n - 1 };
}

/** T4 Zoom out: twice the span (at most the range), centred, then shifted inside the range. */
export function zoomOut(years: Years, range: YearRange): Years {
	const n = Math.min(range.max - range.min + 1, span(years) * 2);
	const centred = years.from - Math.floor((n - span(years)) / 2);
	const from = Math.max(range.min, Math.min(centred, range.max - n + 1));
	return { from, to: from + n - 1 };
}

/** T4 Earlier/Later: the window moves by half its span (at least 1), keeping its span, shifted back inside the range. */
export function pan(years: Years, range: YearRange, direction: -1 | 1): Years {
	const n = span(years);
	const step = Math.max(1, Math.floor(n / 2));
	const from = Math.max(range.min, Math.min(years.from + direction * step, range.max - n + 1));
	return { from, to: from + n - 1 };
}

/** T4: which controls are disabled for the shown years. */
export function timelineDisabled(years: Years, range: YearRange) {
	const full = years.from === range.min && years.to === range.max;
	return {
		zoomIn: years.from === years.to,
		zoomOut: full,
		showAll: full,
		earlier: years.from === range.min,
		later: years.to === range.max
	};
}

/** T4: the live label beside the controls, `<from>–<to>` or one year. */
export const rangeLabel = ({ from, to }: Years) => (from === to ? String(from) : `${from}–${to}`);
