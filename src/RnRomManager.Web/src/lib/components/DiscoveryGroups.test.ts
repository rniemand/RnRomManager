import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { DiscoveryGroup } from '../api/types';
import type { DiscoveryState } from '../discovery';
import { auth } from '../stores/auth.svelte';

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: DiscoveryGroups } = await import('./DiscoveryGroups.svelte');

const keyOf = (n: number) => n.toString(16).padStart(64, '0');
const group = (n: number, name: string, gameCount = 2): DiscoveryGroup => ({
	key: keyOf(n),
	name,
	gameCount
});
const pageOf = (items: DiscoveryGroup[], total = items.length, page = 1) => ({
	items,
	page,
	pageSize: 20,
	total
});
const state = (extra: Partial<DiscoveryState> = {}): DiscoveryState => ({
	kind: 'publishers',
	key: null,
	groupPage: 1,
	gamePage: 1,
	...extra
});

type Answer = { status: number; body?: unknown } | Promise<{ status: number; body?: unknown }>;
let calls: string[];
let answers: Record<string, Answer[]>;

beforeEach(() => {
	resetClient();
	calls = [];
	answers = {};
	const store = new Map<string, string>();
	vi.stubGlobal('localStorage', {
		getItem: (key: string) => store.get(key) ?? null,
		setItem: (key: string, value: string) => void store.set(key, value)
	});
	vi.stubGlobal('fetch', async (url: string) => {
		calls.push(url);
		const answer = await (answers[url]?.shift() ?? { status: 404, body: { status: 404 } });
		const payload = answer.body === undefined ? null : JSON.stringify(answer.body);
		return new Response(payload, { status: answer.status });
	});
});
afterEach(() => {
	vi.unstubAllGlobals();
	auth.set(null);
});

const list = (name: string) => within(screen.getByRole('list', { name }));

describe('DiscoveryGroups (discovery B2)', () => {
	it.each([
		['publishers', ['admin'] as 'admin'[], 1],
		['publishers', [] as 'admin'[], 0],
		['genres', ['admin'] as 'admin'[], 1],
		['genres', [] as 'admin'[], 0]
	] as const)('Change logo shows only for an Admin (%s, roles %j)', async (kind, roles, count) => {
		auth.set({ id: 1, username: 'u', roles: [...roles], mustChangePassword: false });
		answers[`/api/discovery/${kind}?pageSize=20`] = [
			{ status: 200, body: pageOf([group(1, 'Capcom')]) }
		];
		render(DiscoveryGroups, { view: state({ kind }), onpage: vi.fn() });

		await screen.findByRole('link', { name: /Capcom/ });
		expect(screen.queryAllByRole('button', { name: /Change logo/ })).toHaveLength(count);
	});

	it('lists each group with its count, linking to its selection', async () => {
		answers['/api/discovery/publishers?pageSize=20'] = [
			{ status: 200, body: pageOf([group(1, 'Capcom', 1), group(2, 'Nintendo', 1250)]) }
		];
		render(DiscoveryGroups, { view: state({ key: keyOf(2) }), onpage: vi.fn() });

		const nintendo = await screen.findByRole('link', { name: /Nintendo/ });
		expect(nintendo.getAttribute('href')).toBe(`/collections?tab=publishers&key=${keyOf(2)}`);
		expect(nintendo.getAttribute('aria-current')).toBe('true');
		expect(nintendo.textContent).toContain('1,250 games');
		const capcom = list('Publishers').getByRole('link', { name: /Capcom/ });
		expect(capcom.getAttribute('aria-current')).toBeNull();
		expect(capcom.textContent).toContain('1 game');
	});

	it('publishers show their override, else starter art, else a text banner', async () => {
		const nintendo = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
		const sega = '44cddc3f1be25271c8c2a824d85521ee8087db49c90efed6c5b884c995f4dd90';
		const override = `/api/discovery/publishers/logo?key=${sega}&v=5`;
		answers['/api/discovery/publishers?pageSize=20'] = [
			{
				status: 200,
				body: pageOf([
					{ key: keyOf(1), name: 'Hudson', gameCount: 1, logoUrl: null },
					{ key: nintendo, name: 'Nintendo', gameCount: 3, logoUrl: null },
					{ key: sega, name: 'Sega', gameCount: 2, logoUrl: override }
				])
			}
		];
		render(DiscoveryGroups, { view: state(), onpage: vi.fn() });

		const art = (name: RegExp) =>
			screen.getByRole('link', { name }).querySelector('img')?.getAttribute('src') ?? null;
		const banner = (name: RegExp) =>
			screen.getByRole('link', { name }).querySelector('.group-logo-banner')?.textContent ?? null;
		await screen.findByRole('link', { name: /Nintendo/ });
		expect(art(/Hudson/)).toBeNull();
		expect(banner(/Hudson/)).toBe('Hudson');
		expect(
			screen.getByRole('link', { name: /Hudson/ }).querySelector('[aria-hidden="true"]')
		).not.toBeNull();
		expect(art(/Nintendo/)).toBe('/publishers/nintendo.svg');
		expect(art(/Sega/)).toBe(override);
		expect(
			screen.getByRole('link', { name: /Sega/ }).querySelector('img')!.getAttribute('alt')
		).toBe('');

		// A broken override falls back to the starter art, which here doesn't exist either.
		await fireEvent.error(screen.getByRole('link', { name: /Sega/ }).querySelector('img')!);
		expect(art(/Sega/)).toBe('/publishers/sega.svg');
		await fireEvent.error(screen.getByRole('link', { name: /Sega/ }).querySelector('img')!);
		expect(art(/Sega/)).toBeNull();
		expect(banner(/Sega/)).toBe('Sega');
	});

	it('genres show their shared logo, else starter art, else a text banner', async () => {
		const puzzle = 'd4fba09b856cc0aa0f2b643f7d9ca20d309dc89b5e562f1229809c51f1e20dcc';
		const shared = `/api/discovery/genres/logo?key=${keyOf(2)}&v=3`;
		answers['/api/discovery/genres?pageSize=20'] = [
			{
				status: 200,
				body: pageOf([
					{ key: puzzle, name: 'Puzzle', gameCount: 1, logoUrl: null },
					{ key: keyOf(2), name: 'Pinball', gameCount: 1, logoUrl: shared },
					{ key: keyOf(3), name: 'Quiz', gameCount: 1, logoUrl: null }
				])
			}
		];
		render(DiscoveryGroups, { view: state({ kind: 'genres' }), onpage: vi.fn() });

		const link = async (name: RegExp) => await screen.findByRole('link', { name });
		expect((await link(/Puzzle/)).querySelector('img')!.getAttribute('src')).toBe(
			'/genres/puzzle.svg'
		);
		expect((await link(/Pinball/)).querySelector('img')!.getAttribute('src')).toBe(shared);
		expect((await link(/Quiz/)).querySelector('img')).toBeNull();
		expect((await link(/Quiz/)).querySelector('.group-logo-banner')!.textContent).toBe('Quiz');
	});

	it('pages through the URL, keeping the group page in selection links', async () => {
		const onpage = vi.fn();
		answers['/api/discovery/genres?page=2&pageSize=20'] = [
			{ status: 200, body: pageOf([group(21, 'Puzzle')], 45, 2) }
		];
		render(DiscoveryGroups, { view: state({ kind: 'genres', groupPage: 2 }), onpage });

		expect(await screen.findByText('Page 2 of 3')).toBeTruthy();
		expect(screen.getByRole('link', { name: /Puzzle/ }).getAttribute('href')).toBe(
			`/collections?tab=genres&key=${keyOf(21)}&groupPage=2`
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
		expect(onpage).toHaveBeenCalledWith(3);
	});

	it('remembers rows per page and goes back to page 1', async () => {
		const onpage = vi.fn();
		answers['/api/discovery/publishers?page=3&pageSize=20'] = [
			{ status: 200, body: pageOf([group(1, 'A')], 100, 3) }
		];
		answers['/api/discovery/publishers?page=3&pageSize=50'] = [
			{ status: 200, body: pageOf([group(1, 'A')], 100, 3) }
		];
		render(DiscoveryGroups, { view: state({ groupPage: 3 }), onpage });
		await screen.findByRole('link', { name: /A/ });

		await fireEvent.change(screen.getByLabelText('Rows per page'), { target: { value: '50' } });

		expect(localStorage.getItem('rnrm.pageSize.discoveryGroups')).toBe('50');
		expect(onpage).toHaveBeenCalledWith(1);
	});

	it.each([
		['publishers', 'No games have publishers yet.'],
		['genres', 'No games have genres yet.']
	] as const)('an empty %s index says so', async (kind, message) => {
		answers[`/api/discovery/${kind}?pageSize=20`] = [{ status: 200, body: pageOf([]) }];
		render(DiscoveryGroups, { view: state({ kind }), onpage: vi.fn() });

		expect(await screen.findByText(message)).toBeTruthy();
	});

	it('a page past the end says so and still offers paging', async () => {
		answers['/api/discovery/genres?page=9&pageSize=20'] = [
			{ status: 200, body: pageOf([], 25, 9) }
		];
		render(DiscoveryGroups, { view: state({ kind: 'genres', groupPage: 9 }), onpage: vi.fn() });

		expect(await screen.findByText('There are no genres on this page.')).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Previous' })).toBeTruthy();
	});

	it('a failed load shows the error and retries', async () => {
		answers['/api/discovery/publishers?pageSize=20'] = [
			{ status: 500, body: { status: 500, title: 'Boom' } },
			{ status: 200, body: pageOf([group(1, 'Sega')]) }
		];
		render(DiscoveryGroups, { view: state(), onpage: vi.fn() });

		await fireEvent.click(await screen.findByRole('button', { name: /Retry/ }));

		expect(await screen.findByRole('link', { name: /Sega/ })).toBeTruthy();
		expect(screen.queryByRole('alert')).toBeNull();
	});

	it('a late answer for the previous kind or page never replaces the current one', async () => {
		let releasePublishers!: (answer: { status: number; body?: unknown }) => void;
		answers['/api/discovery/publishers?pageSize=20'] = [
			new Promise((resolve) => (releasePublishers = resolve))
		];
		answers['/api/discovery/genres?pageSize=20'] = [
			{ status: 200, body: pageOf([group(3, 'Racing')]) }
		];
		answers['/api/discovery/genres?page=2&pageSize=20'] = [
			new Promise(() => {}) // never answers
		];
		const view = render(DiscoveryGroups, { view: state(), onpage: vi.fn() });

		await view.rerender({ view: state({ kind: 'genres' }), onpage: vi.fn() });
		expect(await screen.findByRole('link', { name: /Racing/ })).toBeTruthy();
		releasePublishers({ status: 200, body: pageOf([group(1, 'Nintendo')]) });
		await new Promise((r) => setTimeout(r, 0));
		expect(screen.queryByRole('link', { name: /Nintendo/ })).toBeNull();
		expect(screen.getByRole('list', { name: 'Genres' })).toBeTruthy();

		await view.rerender({ view: state({ kind: 'genres', groupPage: 2 }), onpage: vi.fn() });
		expect(screen.queryByRole('link', { name: /Racing/ })).toBeNull();
		expect(screen.getByText('Loading genres…')).toBeTruthy();
		expect(calls).toEqual([
			'/api/discovery/publishers?pageSize=20',
			'/api/discovery/genres?pageSize=20',
			'/api/discovery/genres?page=2&pageSize=20'
		]);
	});

	it('truncates names with a title, and highlights the active row with a light badge (B13, B15)', async () => {
		answers['/api/discovery/publishers?pageSize=20'] = [
			{ status: 200, body: pageOf([group(1, 'Capcom', 1), group(2, 'Nintendo', 1250)]) }
		];
		render(DiscoveryGroups, { view: state({ key: keyOf(2) }), onpage: vi.fn() });

		const nintendo = await screen.findByRole('link', { name: /Nintendo/ });
		const name = within(nintendo)
			.getAllByTitle('Nintendo')
			.find((el) => !el.classList.contains('group-logo-banner'))!;
		expect(name.className).toBe('text-truncate');
		expect(name.parentElement!.classList).toContain('min-w-0');
		expect(nintendo.classList).toContain('active');
		expect(nintendo.querySelector('.count-badge')!.className).toBe(
			'badge rounded-pill count-badge text-bg-light'
		);
		const capcom = list('Publishers').getByRole('link', { name: /Capcom/ });
		expect(capcom.querySelector('.count-badge')!.className).toBe(
			'badge rounded-pill count-badge text-bg-primary'
		);
	});
});
