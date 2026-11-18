import { remembered } from './remembered';

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

const filter = remembered('rnrm.library.filter');

/** The saved query string, or '' when none or storage is unavailable. */
export const readSavedLibraryFilter = filter.read;

/** Saves `search` (removes the key when empty); ignored when storage is unavailable. */
export const saveLibraryFilter = filter.save;

export type LibraryView = 'grid' | 'list';

const view = remembered('libraryView');

/** Grid or list: one remembered choice shared by the Library and every collection page. */
export const readLibraryView = (): LibraryView => (view.read() === 'list' ? 'list' : 'grid');

export const saveLibraryView = (value: LibraryView): void => view.save(value);
