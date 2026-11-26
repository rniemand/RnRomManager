import type { SortDir } from '#lib/enums/index.js';

/** A `dir` URL value: `value` when it is exactly `asc` or `desc`, else `fallback`. */
export function parseDir<T>(value: string | null | undefined, fallback: T): SortDir | T {
	return value === 'asc' || value === 'desc' ? value : fallback;
}

/** The other direction. */
export const flip = (dir: SortDir): SortDir => (dir === 'asc' ? 'desc' : 'asc');

/** A click on column `key`: the active column flips its direction, another starts at its default. */
export function nextSort<K>(
	key: K,
	sort: K,
	dir: SortDir,
	defaultDir: (key: K) => SortDir
): { sort: K; dir: SortDir } {
	return { sort: key, dir: key === sort ? flip(dir) : defaultDir(key) };
}
