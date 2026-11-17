/**
 * Collections discovery B1: the `/collections` tabs and their shareable URL state. User collections keep their own
 * `q` and `page`; the discovery tabs use `tab`, `key` (the selected group), `groupPage` and `gamePage`.
 */

import { api } from './api/client';
import type { DiscoveryGroup, GameListItem, Page } from './api/types';

export type DiscoveryKind = 'publishers' | 'genres';
export type CollectionsTab = 'collections' | DiscoveryKind | 'systems';

export const collectionsTabs: readonly { tab: CollectionsTab; label: string }[] = [
	{ tab: 'collections', label: 'User collections' },
	{ tab: 'publishers', label: 'Publishers' },
	{ tab: 'genres', label: 'Genres' },
	{ tab: 'systems', label: 'Systems' }
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
	return tab === 'publishers' || tab === 'genres' || tab === 'systems' ? tab : 'collections';
}

/** A discovery tab's state from its URL, or null on User collections and Systems. A malformed key selects nothing. */
export function discoveryState(url: UrlLike): DiscoveryState | null {
	const kind = collectionsTab(url);
	if (kind === 'collections' || kind === 'systems') return null;
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
	if (tab === 'collections') return '';
	if (tab === 'systems') return systemsSearch({ system: null, gamePage: 1 });
	return discoverySearch({ kind: tab, key: null, groupPage: 1, gamePage: 1 });
}

/** Systems tab B2: the selected system id (as given, case-sensitive) and the one-based games page. */
export interface SystemsState {
	system: string | null;
	gamePage: number;
}

/** The Systems tab's state from its URL; an empty or whitespace-only `system` selects nothing. */
export function systemsState(url: UrlLike): SystemsState {
	const system = url.searchParams.get('system');
	return {
		system: system?.trim() ? system : null,
		gamePage: pageParam(url.searchParams, 'gamePage')
	};
}

/** The query for a Systems state, page 1 and `gamePage` without a system omitted (`?tab=systems&system=gb`). */
export function systemsSearch(state: SystemsState): string {
	const params = new URLSearchParams({ tab: 'systems' });
	if (state.system) params.set('system', state.system);
	if (state.system && state.gamePage > 1) params.set('gamePage', String(state.gamePage));
	return `?${params}`;
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

/**
 * B17: bundled starter genre art (a Bootstrap Icons glyph plus the name), keyed like `starterPublishers`: the SHA-256 of
 * the alias's lowercase ASCII bytes. `discovery.test.ts` recomputes every key; every file has an entry in
 * `static/genres/manifest.json`.
 */
export const starterGenres: readonly { alias: string; key: string; file: string }[] = [
	{
		alias: 'action',
		key: 'bd938c688f49b77c7fc537c6b9222e2c97ebddd63076b87f2feaec66fb9c05d0',
		file: 'action.svg'
	},
	{
		alias: 'adventure',
		key: '47360b8c5f58c5ba04c8ce848545342c0490cfab23ce9f50e16e40b1df254ef8',
		file: 'adventure.svg'
	},
	{
		alias: 'platform',
		key: 'd294fcce0cc88587843099d85dd805aeef1b09a63b0db1dd3e4dc62a343c1db5',
		file: 'platform.svg'
	},
	{
		alias: 'shooter',
		key: 'c3e8e999bf52bb9aa3309c8e81233f1815029e6067c3c534d6eccebe3650e737',
		file: 'shooter.svg'
	},
	{
		alias: "shoot'em up",
		key: 'fbb78489e52b977613aaf723667af28840530e6075ed7b21acf331c891efbdef',
		file: 'shoot-em-up.svg'
	},
	{
		alias: 'fighting',
		key: 'b84707239ea1484db0110700aa7f4298d7212d69618f17167409790efa290b08',
		file: 'fighting.svg'
	},
	{
		alias: "beat'em up",
		key: '3da283867ce306e7912b13d05363acebc7b27f8c441273a3a82fff4d0171015e',
		file: 'beat-em-up.svg'
	},
	{
		alias: 'sports',
		key: '1cb542228c76558789d114d3cb273a75850cca54ec3ee9a41100f2dc56ee561e',
		file: 'sports.svg'
	},
	{
		alias: 'racing',
		key: '0323eeb8b2e7c8900df1fadcf87b06f40d9c6d1f57aeb95d30f7d6e3abc7f2ec',
		file: 'racing.svg'
	},
	{
		alias: 'driving',
		key: 'd9c1acf117657bb04a8309ecbd6f3771e4f5e0bbeb437019748ffb5028bda1b7',
		file: 'driving.svg'
	},
	{
		alias: 'puzzle',
		key: 'd4fba09b856cc0aa0f2b643f7d9ca20d309dc89b5e562f1229809c51f1e20dcc',
		file: 'puzzle.svg'
	},
	{
		alias: 'role-playing (rpg)',
		key: 'a8eeb595a8ffaf114a4483c59fb2f561082d80922291c55859646a3f9f3ad9f9',
		file: 'role-playing-rpg.svg'
	},
	{
		alias: 'strategy',
		key: '73dff70e25ad51ca765a584eef93a1cd909527e2ef4860e0b12f0a7e7ae1979a',
		file: 'strategy.svg'
	},
	{
		alias: 'simulation',
		key: '32e4bc02a7ccf34d72692db7f08aa945102e290beb4832d5673b987015d8cb4f',
		file: 'simulation.svg'
	},
	{
		alias: 'board game',
		key: '3c1d39cd6c8133f7be430e318602688bd4d58cff039331340fe81322142fb07a',
		file: 'board-game.svg'
	}
];

/** B13: the lists that can have a shared logo; systems are keyed by their id. */
export type LogoKind = DiscoveryKind | 'systems';

const starterByKind: Record<LogoKind, Map<string, string>> = {
	publishers: new Map(starterPublishers.map((p) => [p.key, `/publishers/${p.file}`])),
	genres: new Map(starterGenres.map((g) => [g.key, `/genres/${g.file}`])),
	systems: new Map()
};

/**
 * B16: a group's or system's logo sources, best first: its shared logo, then its starter art (publishers and genres).
 * Empty when neither applies (the text banner shows). Sources in `failed` are left out, so a broken image falls through.
 */
export function logoSources(
	kind: LogoKind,
	target: { key: string; logoUrl?: string | null },
	failed: ReadonlySet<string> = new Set()
): string[] {
	const sources = [target.logoUrl, starterByKind[kind].get(target.key)].filter(
		(s): s is string => !!s
	);
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

/** B14's URL rules the browser can check: http or https, at most 1024 characters, no user info; null when valid. */
export function logoUrlError(text: string): string | null {
	const value = text.trim();
	let parsed: URL | null;
	try {
		parsed = value.length > 0 && value.length <= 1024 ? new URL(value) : null;
	} catch {
		parsed = null;
	}
	if (
		!parsed ||
		(parsed.protocol !== 'http:' && parsed.protocol !== 'https:') ||
		parsed.username ||
		parsed.password
	)
		return 'Enter an http or https image URL.';
	return null;
}

const logoRoute = (kind: LogoKind, key: string, suffix = '') =>
	`/api/discovery/${kind}/logo${suffix}?key=${encodeURIComponent(key)}`;

/** B14: a shared logo; only Admins may replace or remove it. Systems pass their id as the key. */
export const logoApi = {
	replace: (kind: LogoKind, key: string, file: File) => {
		const form = new FormData();
		form.append('file', file);
		return api.put<void>(logoRoute(kind, key), form);
	},
	fromUrl: (kind: LogoKind, key: string, url: string) =>
		api.put<void>(logoRoute(kind, key, '/from-url'), { url }),
	remove: (kind: LogoKind, key: string) => api.delete<void>(logoRoute(kind, key))
};
