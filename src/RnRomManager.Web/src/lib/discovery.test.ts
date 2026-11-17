import { createHash } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
	collectionsTab,
	collectionsTabs,
	discoverySearch,
	discoveryState,
	isDiscoveryKey,
	logoSources,
	logoUrlError,
	starterGenres,
	starterPublishers,
	systemsSearch,
	systemsState,
	tabSearch
} from './discovery';

const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
const url = (search: string) => new URL(`http://localhost/collections${search}`);

describe('collections tab URLs (discovery B1)', () => {
	it.each([
		['', 'collections'],
		['?tab=publishers', 'publishers'],
		['?tab=genres', 'genres'],
		['?tab=systems', 'systems'],
		['?tab=Systems', 'collections'],
		['?tab=Publishers', 'collections'],
		['?tab=developers', 'collections'],
		['?tab=', 'collections']
	])('%s is the %s tab', (search, tab) => {
		expect(collectionsTab(url(search))).toBe(tab);
	});

	it('reads the selection and both pages, one-based', () => {
		expect(discoveryState(url(`?tab=genres&key=${key}&groupPage=3&gamePage=2`))).toEqual({
			kind: 'genres',
			key,
			groupPage: 3,
			gamePage: 2
		});
		expect(discoveryState(url('?tab=publishers&groupPage=0&gamePage=-4'))).toEqual({
			kind: 'publishers',
			key: null,
			groupPage: 1,
			gamePage: 1
		});
		expect(discoveryState(url('?q=zel'))).toBeNull();
	});

	it.each([key.toUpperCase(), key.slice(1), `${key}0`, 'nintendo', ''])(
		'a malformed key %j selects nothing',
		(bad) => {
			expect(isDiscoveryKey(bad)).toBe(false);
			expect(discoveryState(url(`?tab=publishers&key=${bad}`))?.key).toBeNull();
		}
	);

	it('writes only what differs from the defaults, and game paging only with a selection', () => {
		expect(discoverySearch({ kind: 'publishers', key: null, groupPage: 1, gamePage: 1 })).toBe(
			'?tab=publishers'
		);
		expect(discoverySearch({ kind: 'genres', key, groupPage: 2, gamePage: 3 })).toBe(
			`?tab=genres&key=${key}&groupPage=2&gamePage=3`
		);
		expect(discoverySearch({ kind: 'genres', key: null, groupPage: 2, gamePage: 3 })).toBe(
			'?tab=genres&groupPage=2'
		);
	});

	it('tab links clear selection and paging', () => {
		expect(tabSearch('collections')).toBe('');
		expect(tabSearch('publishers')).toBe('?tab=publishers');
		expect(tabSearch('genres')).toBe('?tab=genres');
		expect(tabSearch('systems')).toBe('?tab=systems');
	});

	it('a written state reads back the same', () => {
		const state = { kind: 'publishers' as const, key, groupPage: 4, gamePage: 2 };
		expect(discoveryState(url(discoverySearch(state)))).toEqual(state);
	});

	it('lists the four tabs in order', () => {
		expect(collectionsTabs.map((t) => [t.tab, t.label])).toEqual([
			['collections', 'User collections'],
			['publishers', 'Publishers'],
			['genres', 'Genres'],
			['systems', 'Systems']
		]);
	});
});

describe('systems tab URLs (Systems tab B2)', () => {
	it('is not a discovery tab', () => {
		expect(discoveryState(url(`?tab=systems&system=gb&key=${key}`))).toBeNull();
	});

	it('reads the system as given and the one-based game page', () => {
		expect(systemsState(url('?tab=systems&system=gb&gamePage=3'))).toEqual({
			system: 'gb',
			gamePage: 3
		});
		expect(systemsState(url('?tab=systems&system=GB'))).toEqual({ system: 'GB', gamePage: 1 });
		expect(systemsState(url('?tab=systems&system=gb&gamePage=0'))?.gamePage).toBe(1);
		expect(systemsState(url('?tab=systems&system=gb&gamePage=-2'))?.gamePage).toBe(1);
		expect(systemsState(url('?tab=systems&system=gb&gamePage=x'))?.gamePage).toBe(1);
	});

	it.each(['', '%20%20', '?tab=systems'])(
		'an empty or whitespace system %j selects nothing',
		(value) => {
			const search = value.startsWith('?') ? value : `?tab=systems&system=${value}`;
			expect(systemsState(url(search)).system).toBeNull();
		}
	);

	it('writes page 1 and game paging without a system as nothing', () => {
		expect(systemsSearch({ system: null, gamePage: 1 })).toBe('?tab=systems');
		expect(systemsSearch({ system: null, gamePage: 4 })).toBe('?tab=systems');
		expect(systemsSearch({ system: 'gb', gamePage: 1 })).toBe('?tab=systems&system=gb');
		expect(systemsSearch({ system: 'gb', gamePage: 2 })).toBe('?tab=systems&system=gb&gamePage=2');
	});

	it('a written state reads back the same', () => {
		const state = { system: 's32x', gamePage: 5 };
		expect(systemsState(url(systemsSearch(state)))).toEqual(state);
	});
});

describe('starter publisher art (discovery B4, B6)', () => {
	const folder = join(process.cwd(), 'static', 'publishers');
	const manifest = JSON.parse(readFileSync(join(folder, 'manifest.json'), 'utf8')) as {
		assets: { file: string; publisher: string; kind: string; source: string; license: string }[];
	};

	it("each key is B2's key for its alias: SHA-256 of the lowercase ASCII name", () => {
		for (const { alias, key } of starterPublishers) {
			expect(alias).toMatch(/^[a-z0-9]+( [a-z0-9]+)*$/);
			expect(key, alias).toBe(createHash('sha256').update(alias, 'utf8').digest('hex'));
		}
		expect(new Set(starterPublishers.map((p) => p.key)).size).toBe(starterPublishers.length);
	});

	it('covers the starter publishers', () => {
		expect(new Set(starterPublishers.map((p) => p.alias))).toEqual(
			new Set([
				'nintendo',
				'sega',
				'capcom',
				'konami',
				'bandai namco',
				'bandai namco entertainment',
				'bandai namco games',
				'electronic arts',
				'square enix',
				'ubisoft',
				'activision',
				'atari'
			])
		);
	});

	it('every asset is in the manifest with its source and license, and every entry has its file', () => {
		const files = readdirSync(folder).filter((f) => f !== 'manifest.json');
		expect(new Set(manifest.assets.map((a) => a.file))).toEqual(new Set(files));
		for (const asset of manifest.assets)
			expect(
				[asset.publisher, asset.kind, asset.source, asset.license].every((v) => v.length > 0)
			).toBe(true);
		expect(new Set(starterPublishers.map((p) => p.file))).toEqual(new Set(files));
	});

	it('the art makes no request of its own', () => {
		for (const file of readdirSync(folder).filter((f) => f.endsWith('.svg'))) {
			const svg = readFileSync(join(folder, file), 'utf8');
			expect(svg.replace('xmlns="http://www.w3.org/2000/svg"', ''), file).not.toMatch(
				/https?:|href|<script|@import|url\(/i
			);
		}
	});

	const nintendo = starterPublishers.find((p) => p.alias === 'nintendo')!.key;
	const override = `/api/discovery/publishers/logo?key=${nintendo}&v=1`;

	it('prefers the shared override, then the starter art, then the text banner', () => {
		expect(logoSources('publishers', { key: nintendo, logoUrl: override })).toEqual([
			override,
			'/publishers/nintendo.svg'
		]);
		expect(logoSources('publishers', { key: nintendo, logoUrl: null })).toEqual([
			'/publishers/nintendo.svg'
		]);
		expect(logoSources('publishers', { key: '0'.repeat(64), logoUrl: null })).toEqual([]);
		expect(logoSources('publishers', { key: '0'.repeat(64) })).toEqual([]);
	});

	it('a source that failed to load falls through to the next', () => {
		expect(
			logoSources('publishers', { key: nintendo, logoUrl: override }, new Set([override]))
		).toEqual(['/publishers/nintendo.svg']);
		expect(
			logoSources(
				'publishers',
				{ key: nintendo, logoUrl: override },
				new Set([override, '/publishers/nintendo.svg'])
			)
		).toEqual([]);
	});

	it('starter art belongs to its own kind; systems have none', () => {
		const puzzle = starterGenres.find((g) => g.alias === 'puzzle')!.key;
		expect(logoSources('genres', { key: puzzle })).toEqual(['/genres/puzzle.svg']);
		expect(logoSources('publishers', { key: puzzle })).toEqual([]);
		expect(logoSources('genres', { key: nintendo })).toEqual([]);
		expect(logoSources('systems', { key: 'gb', logoUrl: null })).toEqual([]);
		expect(
			logoSources('systems', { key: 'gb', logoUrl: '/api/discovery/systems/logo?key=gb&v=1' })
		).toEqual(['/api/discovery/systems/logo?key=gb&v=1']);
	});
});

describe('starter genre art (B17)', () => {
	const folder = join(process.cwd(), 'static', 'genres');
	const manifest = JSON.parse(readFileSync(join(folder, 'manifest.json'), 'utf8')) as {
		assets: {
			file: string;
			genre: string;
			kind: string;
			glyph: string;
			source: string;
			license: string;
		}[];
	};

	it("each key is B2's key for its alias: SHA-256 of the lowercase ASCII name", () => {
		for (const { alias, key } of starterGenres) {
			expect(alias).toBe(alias.toLowerCase());
			expect(key, alias).toBe(createHash('sha256').update(alias, 'ascii').digest('hex'));
		}
		expect(new Set(starterGenres.map((g) => g.key)).size).toBe(starterGenres.length);
	});

	it('covers the most common libretro genres', () => {
		expect(new Set(starterGenres.map((g) => g.alias))).toEqual(
			new Set([
				'action',
				'adventure',
				'platform',
				'shooter',
				"shoot'em up",
				'fighting',
				"beat'em up",
				'sports',
				'racing',
				'driving',
				'puzzle',
				'role-playing (rpg)',
				'strategy',
				'simulation',
				'board game'
			])
		);
	});

	it('every asset is in the manifest with its glyph, source and license, and every entry has its file', () => {
		const files = readdirSync(folder).filter((f) => f !== 'manifest.json');
		expect(new Set(manifest.assets.map((a) => a.file))).toEqual(new Set(files));
		for (const asset of manifest.assets)
			expect(
				[asset.genre, asset.kind, asset.glyph, asset.source, asset.license].every(
					(v) => v.length > 0
				),
				asset.file
			).toBe(true);
		expect(new Set(starterGenres.map((g) => g.file))).toEqual(new Set(files));
	});

	it('the art makes no request of its own', () => {
		for (const file of readdirSync(folder).filter((f) => f.endsWith('.svg'))) {
			const svg = readFileSync(join(folder, file), 'utf8');
			expect(svg.replace('xmlns="http://www.w3.org/2000/svg"', ''), file).not.toMatch(
				/https?:|href|<script|@import|url\(/i
			);
		}
	});
});

describe('logo URL check (B19)', () => {
	it.each([
		['https://example.com/logo.png', null],
		['  http://example.com/a.jpg  ', null],
		['ftp://example.com/a.png', 'Enter an http or https image URL.'],
		['https://user:pw@example.com/a.png', 'Enter an http or https image URL.'],
		['logo.png', 'Enter an http or https image URL.'],
		['', 'Enter an http or https image URL.'],
		[`https://example.com/${'a'.repeat(1010)}`, 'Enter an http or https image URL.']
	])('%j', (url, error) => {
		expect(logoUrlError(url)).toBe(error);
	});
});
