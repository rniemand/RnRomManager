/**
 * Collections discovery B1: the `/collections` tabs and their shareable URL state. User collections keep their own
 * `q` and `page`; the discovery tabs use `tab`, `key` (the selected group), `groupPage` and `gamePage`.
 */

import { api } from './api/client';
import type { DiscoveryGroup, GameListItem, Page } from './api/types';

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

/** B3: `/api/discovery/{kind}` reads. */
export const discoveryApi = {
	groups: (kind: DiscoveryKind, params: { page: number; pageSize: number }) => {
		const search = new URLSearchParams();
		if (params.page > 1) search.set('page', String(params.page));
		search.set('pageSize', String(params.pageSize));
		return api.get<Page<DiscoveryGroup>>(`/api/discovery/${kind}?${search}`);
	},
	/** The group's games across systems; 404 when no game has that value any more. */
	games: (kind: DiscoveryKind, key: string, params: { page: number; pageSize: number }) => {
		const search = new URLSearchParams({ key });
		if (params.page > 1) search.set('page', String(params.page));
		search.set('pageSize', String(params.pageSize));
		return api.get<Page<GameListItem>>(`/api/discovery/${kind}/games?${search}`);
	}
};

/**
 * B4: bundled starter art, by group key. A key is the SHA-256 of the alias's lowercase ASCII bytes, which is B2's
 * key for that publisher name; `discovery.test.ts` recomputes every one. Historical names are their own groups and
 * get art only when listed here. Every file has an entry in `static/publishers/manifest.json`.
 */
export const starterPublishers: readonly { alias: string; key: string; file: string }[] = [
	{
		alias: 'activision',
		key: 'ba995ef4f06ea5a7328c8b9e63f7b406fddd0f3b007896653b66c596c26fcf29',
		file: 'activision.svg'
	},
	{
		alias: 'atari',
		key: 'f3c1f895fdb1ac14c19166f4d606f9c8be3805ec2d4596dbf344d75a8c41e3a6',
		file: 'atari.svg'
	},
	{
		alias: 'bandai namco',
		key: 'f40a789dfb4086388d1a1bc44c847df26c517e19dd0408e5798a27da1668111d',
		file: 'bandai-namco.svg'
	},
	{
		alias: 'bandai namco entertainment',
		key: '6d8cd6cee6f78260148814f0be2e0269e4d06b092f6507138b995183b1f91712',
		file: 'bandai-namco.svg'
	},
	{
		alias: 'bandai namco games',
		key: '264c3049b029f017d4d9927af9c56d4b51e7f2a652b7330c21403e1da01e3cee',
		file: 'bandai-namco.svg'
	},
	{
		alias: 'capcom',
		key: 'c0e687a2d1fc85ee043de90c4bfafc300f927d5ee5439bd195ab9be96cacfae8',
		file: 'capcom.svg'
	},
	{
		alias: 'electronic arts',
		key: '6a5ebc8254dd22f2d54faeddfeee2143a3f7facb2de5aa61a00bb9146f2cca78',
		file: 'electronic-arts.svg'
	},
	{
		alias: 'konami',
		key: 'f2423d071f26082d053141bad42a7cb050310a1c0dfc7be5e6333fa76d32328e',
		file: 'konami.svg'
	},
	{
		alias: 'nintendo',
		key: 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516',
		file: 'nintendo.svg'
	},
	{
		alias: 'sega',
		key: '44cddc3f1be25271c8c2a824d85521ee8087db49c90efed6c5b884c995f4dd90',
		file: 'sega.svg'
	},
	{
		alias: 'square enix',
		key: '3738f9baa6cd8012af244a9e364efd66d989aba9f697ea7f78ff28075b1f3888',
		file: 'square-enix.svg'
	},
	{
		alias: 'ubisoft',
		key: 'ff1a199925e407fa66d7b12bb36a68d12b990ad9b7f203f4690210027b938776',
		file: 'ubisoft.svg'
	}
];

const starterByKey = new Map(starterPublishers.map((p) => [p.key, `/publishers/${p.file}`]));

/**
 * B6: the image for a publisher group, best first: its shared override, then the starter art for its key. Empty when
 * neither applies (text only). Pass the sources that failed to load in `failed` to fall through to the next one.
 */
export function publisherArt(
	group: Pick<DiscoveryGroup, 'key' | 'logoUrl'>,
	failed: ReadonlySet<string> = new Set()
): string[] {
	const sources = [group.logoUrl, starterByKey.get(group.key)].filter((s): s is string => !!s);
	return sources.filter((s) => !failed.has(s));
}

/** B5: a logo override's limits, as the server checks them (it also checks the type and 2048 px per side). */
export const maxLogoBytes = 512 * 1024;
export const maxLogoDimension = 2048;

/** B5's checks the browser can make before uploading; null when the file may be sent. */
export function logoFileError(file: File | null | undefined): string | null {
	if (!file || file.size === 0) return 'Choose an image file.';
	if (file.type !== 'image/png' && file.type !== 'image/jpeg')
		return 'The logo must be a PNG or JPEG image.';
	if (file.size > maxLogoBytes) return 'The logo must be at most 512 KiB.';
	return null;
}

const logoUrl = (key: string) => `/api/discovery/publishers/logo?key=${encodeURIComponent(key)}`;

/** B5: the shared logo override any signed-in user may replace or remove. */
export const publisherLogoApi = {
	replace: (key: string, file: File) => {
		const form = new FormData();
		form.append('file', file);
		return api.put<void>(logoUrl(key), form);
	},
	remove: (key: string) => api.delete<void>(logoUrl(key))
};
