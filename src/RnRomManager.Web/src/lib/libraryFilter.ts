/** L7: the Library's filter, sort and page, remembered in `localStorage` as `toSearch`'s query string. */

/** A URL with none of these is "bare" and restores the saved filter; other parameters don't count. */
export const libraryFilterParams = [
	'system',
	'letter',
	'region',
	'q',
	'sort',
	'dir',
	'favourite',
	'page'
] as const;

const storageKey = 'rnrm.library.filter';

/** The saved query string, or '' when none or storage is unavailable. */
export function readSavedLibraryFilter(): string {
	try {
		return localStorage.getItem(storageKey) ?? '';
	} catch {
		return '';
	}
}

/** Saves `search` (removes the key when empty); ignored when storage is unavailable. */
export function saveLibraryFilter(search: string): void {
	try {
		if (search) localStorage.setItem(storageKey, search);
		else localStorage.removeItem(storageKey);
	} catch {
		// remembering the filter is a convenience
	}
}

export function clearLibraryFilter(): void {
	saveLibraryFilter('');
}
