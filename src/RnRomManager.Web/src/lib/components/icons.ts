/** Semantic icon name → icon classes (spec §11). Bootstrap Icons everywhere; Font Awesome only where BI has no match. */
export const icons: Record<string, string> = {
	library: 'bi bi-collection',
	collections: 'bi bi-bookmarks',
	ingest: 'bi bi-box-arrow-in-down',
	review: 'bi bi-clipboard-check',
	integrity: 'bi bi-shield-check',
	reports: 'bi bi-bar-chart',
	admin: 'bi bi-gear',
	user: 'bi bi-person-circle',
	logout: 'bi bi-box-arrow-right',
	password: 'bi bi-key',
	add: 'bi bi-plus-lg',
	select: 'bi bi-check2-square',
	'add-user': 'bi bi-person-plus',
	enable: 'bi bi-person-check',
	disable: 'bi bi-person-slash',
	'theme-light': 'bi bi-sun',
	'theme-dark': 'bi bi-moon-stars',
	'theme-auto': 'bi bi-circle-half',
	download: 'bi bi-download',
	upload: 'bi bi-upload',
	copy: 'bi bi-clipboard',
	edit: 'bi bi-pencil',
	delete: 'bi bi-trash',
	move: 'bi bi-arrow-left-right',
	merge: 'bi bi-union',
	retry: 'bi bi-arrow-clockwise',
	search: 'bi bi-search',
	google: 'bi bi-google',
	code: 'bi bi-code-slash',
	note: 'bi bi-sticky',
	'note-filled': 'bi bi-sticky-fill',
	star: 'bi bi-star',
	'star-fill': 'bi bi-star-fill',
	favourite: 'bi bi-heart',
	'favourite-fill': 'bi bi-heart-fill',
	grid: 'bi bi-grid-3x3-gap',
	'sort-asc': 'bi bi-sort-up-alt',
	'sort-desc': 'bi bi-sort-down',
	list: 'bi bi-list-ul',
	/** Points right; rotated 90° (down) when its toggle is expanded (`.chevron-toggle`). */
	chevron: 'bi bi-chevron-right',
	folder: 'bi bi-folder',
	info: 'bi bi-info-circle',
	help: 'bi bi-question-circle',
	warning: 'bi bi-exclamation-triangle',
	error: 'bi bi-x-octagon',
	success: 'bi bi-check-circle',
	empty: 'bi bi-inbox',
	'not-allowed': 'bi bi-slash-circle',
	'not-found': 'bi bi-signpost-split',
	github: 'fa-brands fa-github',
	// Disc systems B22: the brand marks Bootstrap Icons has, a disc for the rest.
	'system-psx': 'bi bi-playstation',
	'system-psp': 'bi bi-playstation',
	'system-x360': 'bi bi-xbox',
	'system-gc': 'bi bi-disc',
	'system-wii': 'bi bi-disc',
	'system-saturn': 'bi bi-disc'
};

/** An unmapped `system-<id>` falls back to a controller, so a new system needs no UI change. */
export const systemFallback = 'bi bi-controller';

export function iconClass(name: string): string {
	return icons[name] ?? (name.startsWith('system-') ? systemFallback : 'bi bi-question-circle');
}
