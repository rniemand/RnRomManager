/**
 * Collections discovery B1: the `/collections` tabs and their shareable URL state. User collections keep their own
 * `q` and `page`; the discovery tabs use `tab`, `key` (the selected group), `groupPage` and `gamePage`.
 */

export type DiscoveryKind = 'publishers' | 'genres';
export type CollectionsTab = 'collections' | DiscoveryKind;

export const collectionsTabs: readonly { tab: CollectionsTab; label: string }[] = [
	{ tab: 'collections', label: 'User collections' },
	{ tab: 'publishers', label: 'Publishers' },
	{ tab: 'genres', label: 'Genres' }
];

/** A discovery tab's URL state; pages are one-based. */
export interface DiscoveryState {
	kind: DiscoveryKind;
	key: string | null;
	groupPage: number;
	gamePage: number;
}

/** What the helpers read of a URL (SvelteKit's `page.url` has read-only search params). */
type UrlLike = { readonly searchParams: { get(name: string): string | null } };

/** B3: a group key is exactly 64 lowercase hexadecimal characters. */
export const isDiscoveryKey = (value: string | null): value is string =>
	value !== null && /^[0-9a-f]{64}$/.test(value);

function pageParam(params: UrlLike['searchParams'], name: string): number {
	return Math.max(1, Math.floor(Number(params.get(name))) || 1);
}

/** The tab a `/collections` URL shows: a bare URL, or any other `tab` value, is User collections. */
export function collectionsTab(url: UrlLike): CollectionsTab {
	const tab = url.searchParams.get('tab');
	return tab === 'publishers' || tab === 'genres' ? tab : 'collections';
}

/** A discovery tab's state from its URL, or null on User collections. A malformed key selects nothing. */
export function discoveryState(url: UrlLike): DiscoveryState | null {
	const kind = collectionsTab(url);
	if (kind === 'collections') return null;
	const key = url.searchParams.get('key');
	return {
		kind,
		key: isDiscoveryKey(key) ? key : null,
		groupPage: pageParam(url.searchParams, 'groupPage'),
		gamePage: pageParam(url.searchParams, 'gamePage')
	};
}

/** The query for a discovery state, page 1 and no selection omitted (`?tab=publishers&key=…&gamePage=2`). */
export function discoverySearch(state: DiscoveryState): string {
	const params = new URLSearchParams({ tab: state.kind });
	if (state.key) params.set('key', state.key);
	if (state.groupPage > 1) params.set('groupPage', String(state.groupPage));
	if (state.key && state.gamePage > 1) params.set('gamePage', String(state.gamePage));
	return `?${params}`;
}

/** A tab link's query: changing tab clears the selection and paging (and User collections' filter). */
export function tabSearch(tab: CollectionsTab): string {
	return tab === 'collections'
		? ''
		: discoverySearch({ kind: tab, key: null, groupPage: 1, gamePage: 1 });
}
