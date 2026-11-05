/** Semantic icon name → icon classes (spec §11). Bootstrap Icons everywhere; Font Awesome only where BI has no match. */
export const icons: Record<string, string> = {
	library: 'bi bi-collection',
	ingest: 'bi bi-box-arrow-in-down',
	review: 'bi bi-clipboard-check',
	integrity: 'bi bi-shield-check',
	admin: 'bi bi-gear',
	user: 'bi bi-person-circle',
	logout: 'bi bi-box-arrow-right',
	password: 'bi bi-key',
	'theme-light': 'bi bi-sun',
	'theme-dark': 'bi bi-moon-stars',
	'theme-auto': 'bi bi-circle-half',
	download: 'bi bi-download',
	copy: 'bi bi-clipboard',
	edit: 'bi bi-pencil',
	delete: 'bi bi-trash',
	move: 'bi bi-arrow-left-right',
	merge: 'bi bi-union',
	retry: 'bi bi-arrow-clockwise',
	search: 'bi bi-search',
	star: 'bi bi-star',
	'star-fill': 'bi bi-star-fill',
	favourite: 'bi bi-heart',
	'favourite-fill': 'bi bi-heart-fill',
	grid: 'bi bi-grid-3x3-gap',
	'sort-asc': 'bi bi-sort-up-alt',
	'sort-desc': 'bi bi-sort-down',
	list: 'bi bi-list-ul',
	info: 'bi bi-info-circle',
	warning: 'bi bi-exclamation-triangle',
	error: 'bi bi-x-octagon',
	success: 'bi bi-check-circle',
	empty: 'bi bi-inbox',
	'not-allowed': 'bi bi-slash-circle',
	'not-found': 'bi bi-signpost-split',
	github: 'fa-brands fa-github'
};

/** An unmapped `system-<id>` falls back to a controller, so a new system needs no UI change. */
export const systemFallback = 'bi bi-controller';

export function iconClass(name: string): string {
	return icons[name] ?? (name.startsWith('system-') ? systemFallback : 'bi bi-question-circle');
}
