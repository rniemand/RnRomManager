import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';
import { createRawSnippet } from 'svelte';
import { expectOnePageTitle } from '#lib/testing/pageTitle.js';

const goto = vi.fn((...args: [string, unknown?]) => navigateTo(args[0]));
vi.mock('$app/navigation', () => ({ goto: (...args: [string, unknown?]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		get url() {
			return testUrl.current;
		}
	}
}));
// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	},
	Dropdown: { getOrCreateInstance: () => ({ hide() {} }) }
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { default: ReportsIndex } = await import('./+page.svelte');
const { default: ReportsLayout } = await import('./+layout.svelte');
const { default: MissingImages } = await import('./missing-images/+page.svelte');
const { default: LibrarySize } = await import('./library-size/+page.svelte');
const { default: Duplicates } = await import('./duplicates/+page.svelte');
const { default: MultipleReleases } = await import('./multiple-releases/+page.svelte');
const { default: Hacks } = await import('./hacks/+page.svelte');
const { default: NewlyAdded } = await import('./newly-added/+page.svelte');
const { default: MissingMetadata } = await import('./missing-metadata/+page.svelte');

type Answer = { status: number; body?: unknown };

const systems = [
	{
		id: 'gb',
		displayName: 'Game Boy',
		shortName: 'GB',
		logoUrl: null,
		gameCount: 2,
		romCount: 2,
		regions: []
	},
	{
		id: 'gbc',
		displayName: 'Game Boy Color',
		shortName: 'GBC',
		logoUrl: null,
		gameCount: 1,
		romCount: 1,
		regions: []
	}
];

/** F2: the System menu toggle, named after the chosen system (FilterDropdown's aria-label). */
const systemToggle = () => screen.getByRole('button', { name: /^System: / });
/** F2: clicks a System menu row by its value, once `/api/systems` has filled the menu. */
async function pickSystem(value: string): Promise<void> {
	const row = await vi.waitFor(() => {
		const found = systemToggle()
			.closest('.dropdown')!
			.querySelector<HTMLButtonElement>(`.dropdown-item[data-value="${value}"]`);
		if (!found) throw new Error(`no System row ${value}`);
		return found;
	});
	await fireEvent.click(row);
}

describe('reports pages', () => {
	let calls: string[];
	let bodies: Record<string, unknown[]>;
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		navigateTo('/reports');
		calls = [];
		bodies = {};
		answers = { 'GET /api/systems': { status: 200, body: systems } };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url === '/api/auth/antiforgery')
				return Promise.resolve(new Response(null, { status: 204 }));
			calls.push(key);
			if (typeof init?.body === 'string') (bodies[key] ??= []).push(JSON.parse(init.body));
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('/reports redirects to Newly Added, replacing the history entry (RP1, release editing B15)', () => {
		render(ReportsIndex);
		expect(goto).toHaveBeenCalledWith('/reports/newly-added', { replace: true });
	});

	describe('Library Size (RP3)', () => {
		const library = {
			totalBytes: 3000,
			gameCount: 3,
			updateCount: 1250,
			systems: [
				{ system: 'gbc', systemName: 'Game Boy Color', bytes: 2000, gameCount: 1, updateCount: 0 },
				{ system: 'gb', systemName: 'Game Boy', bytes: 1000, gameCount: 2, updateCount: 1250 }
			]
		};
		const tiles = () => [...document.querySelectorAll('svg rect')];
		const tileLinks = () =>
			[...document.querySelectorAll('svg a')].map((a) => a.getAttribute('href'));

		it('library level: one rectangle per system in its colour, and the table with shares', async () => {
			answers['GET /api/reports/library-size'] = { status: 200, body: library };
			render(LibrarySize);

			await screen.findByRole('link', { name: 'Game Boy Color' });
			expect(document.querySelector('p')!.textContent).toContain('in 3 games');
			expect(tiles().map((r) => r.getAttribute('fill'))).toEqual([
				'var(--bs-info)',
				'var(--bs-success)'
			]);
			expect(tiles()[0].getAttribute('stroke')).toBe('var(--bs-body-bg)');
			expect(document.querySelector('svg')!.getAttribute('role')).toBe('group');
			expect(document.querySelector('svg')!.getAttribute('aria-label')).toContain('Game Boy Color');
			expect(document.querySelector('svg title')!.textContent).toBe('Game Boy Color — 2.0 KiB');
			expect(document.querySelector('svg text')!.getAttribute('fill')).toBe('#000');
			const rows = [...document.querySelectorAll('tbody tr')].map((r) =>
				[...r.querySelectorAll('td')].map((td) => td.textContent?.trim())
			);
			expect(rows).toEqual([
				['Game Boy Color', '1', '0', '2.0 KiB', '66.7%'],
				['Game Boy', '2', '1,250', '1000 B', '33.3%']
			]);
			const updates = screen.getByRole('columnheader', { name: 'Updates' });
			expect(updates.classList.contains('text-end')).toBe(true);
		});

		it('clicking anywhere in a row follows its link; the link itself stays (B22)', async () => {
			answers['GET /api/reports/library-size'] = { status: 200, body: library };
			render(LibrarySize);
			const link = await screen.findByRole('link', { name: 'Game Boy' });
			const followed: string[] = [];
			for (const a of document.querySelectorAll('tbody a'))
				a.addEventListener('click', (e) => {
					e.preventDefault();
					followed.push(a.getAttribute('href')!);
				});
			const row = link.closest('tr')!;

			await fireEvent.click(row.querySelectorAll('td')[3]);
			expect(followed).toEqual(['/reports/library-size?system=gb']);
			await fireEvent.click(link);
			expect(followed).toHaveLength(2);
			expect(row.closest('table')!.classList.contains('clickable-rows')).toBe(true);
			expect(row.getAttribute('tabindex')).toBeNull();
		});

		it('each system row has an Open in Library link after the system link (B19)', async () => {
			answers['GET /api/reports/library-size'] = { status: 200, body: library };
			render(LibrarySize);

			const open = await screen.findByRole('link', { name: 'Open Game Boy in Library' });
			expect(open.getAttribute('href')).toBe('/?system=gb');
			expect(open.className).toContain('btn-sm');
			const cell = open.closest('td')!;
			expect(cell.querySelector('a')!.textContent).toBe('Game Boy');
			expect(open.closest('tr')!.querySelectorAll('td')[0]).toBe(cell);
		});

		it('a system rectangle or row drills in with ?system=', async () => {
			answers['GET /api/reports/library-size'] = { status: 200, body: library };
			render(LibrarySize);

			const row = await screen.findByRole('link', { name: 'Game Boy' });
			expect(row.getAttribute('href')).toBe('/reports/library-size?system=gb');
			expect(tileLinks()).toEqual([
				'/reports/library-size?system=gbc',
				'/reports/library-size?system=gb'
			]);
		});

		it('system level: game rectangles and rows open the game; a breadcrumb leads back', async () => {
			navigateTo('/reports/library-size?system=gb');
			answers['GET /api/reports/library-size/gb'] = {
				status: 200,
				body: {
					system: 'gb',
					systemName: 'Game Boy',
					bytes: 1000,
					updateCount: 1,
					games: [
						{ id: 'g1', displayTitle: 'Bravo', bytes: 700, romCount: 2, updateCount: 1 },
						{ id: 'g2', displayTitle: 'Alpha', bytes: 300, romCount: 1, updateCount: 0 }
					]
				}
			};
			render(LibrarySize);

			expect((await screen.findByRole('link', { name: 'Bravo' })).getAttribute('href')).toBe(
				'/games/g1'
			);
			expect(tileLinks()).toEqual(['/games/g1', '/games/g2']);
			expect(screen.getByRole('link', { name: 'Library' }).getAttribute('href')).toBe(
				'/reports/library-size'
			);
			expect(screen.getByText('Game Boy', { selector: '.breadcrumb-item' })).toBeTruthy();
			expect(screen.getByRole('columnheader', { name: '% of system' })).toBeTruthy();
			expect(
				[...document.querySelectorAll('tbody tr')].map(
					(r) => r.querySelectorAll('td')[2].textContent
				)
			).toEqual(['1', '0']);
		});

		it('draws the 300 largest games plus one unclickable "others" rectangle', async () => {
			navigateTo('/reports/library-size?system=gb');
			const games = Array.from({ length: 305 }, (_, i) => ({
				id: `g${i}`,
				displayTitle: `Game ${i}`,
				bytes: 10_000 - i,
				romCount: 1,
				updateCount: 0
			}));
			answers['GET /api/reports/library-size/gb'] = {
				status: 200,
				body: { system: 'gb', systemName: 'Game Boy', bytes: 1, updateCount: 0, games }
			};
			render(LibrarySize);

			await screen.findByRole('link', { name: 'Game 304' });
			expect(tiles()).toHaveLength(301);
			expect(tileLinks()).toHaveLength(300);
			const others = [...document.querySelectorAll('svg g title')].map((t) => t.textContent);
			expect(others).toHaveLength(1);
			expect(others[0]).toMatch(/^5 others — /);
			expect(document.querySelectorAll('tbody tr')).toHaveLength(305);
		}, 20_000); // 305 rows of five columns: slow on a loaded machine

		it('an empty library shows the empty state', async () => {
			answers['GET /api/reports/library-size'] = {
				status: 200,
				body: { totalBytes: 0, gameCount: 0, systems: [] }
			};
			render(LibrarySize);

			expect(await screen.findByText('The library is empty.')).toBeTruthy();
			expect(document.querySelector('svg')).toBeNull();
		});
	});

	describe('Duplicates (RP9)', () => {
		const list = 'GET /api/reports/duplicates?page=1&pageSize=25';
		const game = (id: string, displayTitle: string, romCount: number) => ({
			id,
			displayTitle,
			romCount,
			zipSizeBytes: 2048,
			createdAt: '2026-11-01T00:00:00Z',
			boxArtUrl: null
		});
		const group = {
			system: 'gb',
			systemName: 'Game Boy',
			baseTitle: 'Quest',
			gamesKey: 'k'.repeat(64),
			games: [game('g1', 'Quest', 2), game('g2', 'Quest DX', 1), game('g3', 'Quest II', 1)]
		};
		const page = { items: [group], page: 1, pageSize: 25, total: 1 };
		const signIn = (roles: 'admin'[]) =>
			auth.set({ id: 1, username: 'u', roles, mustChangePassword: false });
		const radio = (title: string) =>
			screen.getByRole('radio', { name: `Merge into ${title}` }) as HTMLInputElement;

		it('the System menu filters the request and goes back to page 1 (F2)', async () => {
			signIn(['admin']);
			answers[list] = { status: 200, body: page };
			answers[`${list}&system=gbc`] = { status: 200, body: { ...page, items: [] } };
			render(Duplicates);
			await screen.findByRole('heading', { name: 'Quest' });
			expect(document.getElementById('duplicates-system')).toBeNull();

			await pickSystem('gbc');

			await vi.waitFor(() => expect(calls).toContain(`${list}&system=gbc`));
			expect(systemToggle().getAttribute('aria-label')).toBe('System: Game Boy Color');
		});

		it('renders a card per group; the first game is pre-selected and labelled', async () => {
			signIn(['admin']);
			answers[list] = { status: 200, body: page };
			render(Duplicates);

			expect(await screen.findByRole('heading', { name: 'Quest' })).toBeTruthy();
			expect([radio('Quest').checked, radio('Quest DX').checked]).toEqual([true, false]);
			const first = screen.getByRole('link', { name: 'Quest' }).closest('tr')!;
			expect(first.textContent).toContain('Suggested target');
			expect(screen.getAllByText('Suggested target')).toHaveLength(1);
		});

		it('labels the size column ZIP size and shows each game zipSizeBytes (G6)', async () => {
			signIn(['admin']);
			answers[list] = { status: 200, body: page };
			render(Duplicates);

			await screen.findByRole('link', { name: 'Quest DX' });
			expect(screen.getByRole('columnheader', { name: 'ZIP size' })).toBeTruthy();
			expect(screen.queryByRole('columnheader', { name: 'Size' })).toBeNull();
			const row = screen.getByRole('link', { name: 'Quest DX' }).closest('tr')!;
			expect(row.textContent).toMatch(/2(\.0)? KiB|2 KB/);
		});

		it('a Site user sees the list without radios or buttons', async () => {
			signIn([]);
			answers[list] = { status: 200, body: page };
			render(Duplicates);

			await screen.findByRole('link', { name: 'Quest DX' });
			expect(screen.queryAllByRole('radio')).toEqual([]);
			expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();
			expect(screen.queryByRole('button', { name: 'Merge into selected' })).toBeNull();
		});

		it('merge confirms, posts the selected target and the other ids, then reloads', async () => {
			signIn(['admin']);
			answers[list] = { status: 200, body: page };
			answers['POST /api/reports/duplicates/merge'] = { status: 200, body: { targetGameId: 'g2' } };
			render(Duplicates);
			await screen.findByRole('link', { name: 'Quest DX' });

			await fireEvent.click(radio('Quest DX'));
			await fireEvent.click(screen.getByRole('button', { name: 'Merge into selected' }));
			const modal = document.querySelector('.modal')!;
			expect(modal.textContent?.replace(/\s+/g, ' ')).toContain('Merge 2 games into Quest DX?');
			await fireEvent.click(screen.getByRole('button', { name: 'Merge', hidden: true }));

			await vi.waitFor(() => expect(calls.filter((c) => c === list)).toHaveLength(2));
			expect(bodies['POST /api/reports/duplicates/merge']).toEqual([
				{ targetGameId: 'g2', sourceGameIds: ['g1', 'g3'] }
			]);
		});

		it('dismiss posts the group key and reloads', async () => {
			signIn(['admin']);
			answers[list] = { status: 200, body: page };
			render(Duplicates);
			await screen.findByRole('link', { name: 'Quest DX' });

			await fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

			await vi.waitFor(() => expect(calls.filter((c) => c === list)).toHaveLength(2));
			expect(bodies['POST /api/reports/duplicates/dismiss']).toEqual([
				{ system: 'gb', baseTitle: 'Quest', gamesKey: group.gamesKey }
			]);
		});

		it('a merge that stops part-way toasts the problem and the merged count', async () => {
			signIn(['admin']);
			toasts.items = [];
			answers[list] = { status: 200, body: page };
			answers['POST /api/reports/duplicates/merge'] = {
				status: 404,
				body: { status: 404, code: 'NotFound', title: 'Not found', merged: ['g2'] }
			};
			render(Duplicates);
			await screen.findByRole('link', { name: 'Quest DX' });

			await fireEvent.click(screen.getByRole('button', { name: 'Merge into selected' }));
			await fireEvent.click(screen.getByRole('button', { name: 'Merge', hidden: true }));

			await vi.waitFor(() =>
				expect(toasts.items.at(-1)).toMatchObject({
					kind: 'danger',
					text: 'Not found 1 merged before it stopped.'
				})
			);
		});

		it('shows the empty state', async () => {
			answers[list] = { status: 200, body: { items: [], page: 1, pageSize: 25, total: 0 } };
			render(Duplicates);

			expect(await screen.findByText('No duplicate games found.')).toBeTruthy();
		});
	});

	describe('Missing Images (RP5)', () => {
		const list = (query = '') => `GET /api/reports/missing-images?page=1&pageSize=50${query}`;

		it('lists games with a link to their Images tab, the system badge and the reason', async () => {
			answers[list()] = {
				status: 200,
				body: {
					items: [
						{
							id: 'g1',
							displayTitle: 'Alpha',
							system: 'gb',
							systemName: 'Game Boy',
							reason: 'NoCover'
						},
						{
							id: 'g2',
							displayTitle: 'Echo',
							system: 'gbc',
							systemName: 'Game Boy Color',
							reason: 'Placeholder'
						}
					],
					page: 1,
					pageSize: 50,
					total: 2
				}
			};
			render(MissingImages);

			const link = await screen.findByRole('link', { name: 'Alpha' });
			expect(link.getAttribute('href')).toBe('/games/g1?tab=images');
			const rows = [...document.querySelectorAll('tbody tr')].map((r) =>
				[...r.querySelectorAll('td')].map((td) => td.textContent?.trim())
			);
			expect(rows).toEqual([
				['Alpha', 'Game Boy', 'No cover'],
				['Echo', 'Game Boy Color', 'Placeholder art']
			]);
			expect(document.querySelector('thead')!.className).toBe('table-dark');
		});

		it('the System filter sends system and goes back to page 1', async () => {
			answers[list()] = { status: 200, body: { items: [], page: 1, pageSize: 50, total: 0 } };
			render(MissingImages);
			expect(systemToggle().getAttribute('aria-label')).toBe('System: All');

			await pickSystem('gbc');

			await vi.waitFor(() => expect(calls).toContain(list('&system=gbc')));
			expect(systemToggle().getAttribute('aria-label')).toBe('System: Game Boy Color');
			expect(systemToggle().classList).toContain('btn-secondary');
		});

		it('shows the empty state', async () => {
			answers[list()] = { status: 200, body: { items: [], page: 1, pageSize: 50, total: 0 } };
			render(MissingImages);

			expect(await screen.findByText('Every game has a cover image.')).toBeTruthy();
		});
	});

	describe('Multiple Releases (B18–B20)', () => {
		const list = (min = 2, page = 1) =>
			`GET /api/reports/multiple-releases?min=${min}&sort=releases&dir=desc&page=${page}&pageSize=50`;
		const item = (id: string, extra: Record<string, unknown> = {}) => ({
			id,
			displayTitle: `Game ${id}`,
			system: 'gb',
			systemName: 'Game Boy',
			releaseCount: 3,
			updateCount: 1,
			bytes: 2048,
			...extra
		});
		const body = (items: unknown[], total = items.length, maxReleases = 4, page = 1) => ({
			maxReleases,
			items,
			page,
			pageSize: 50,
			total
		});
		const select = () => screen.getByLabelText('Minimum releases') as HTMLSelectElement;
		const optionTexts = () => [...select().options].map((o) => o.textContent!.trim());
		const replaced = (path: string) => [path, { replace: true, reset: false }];

		beforeEach(() => navigateTo('/reports/multiple-releases'));

		it('lists game, system badge, releases, updates and size; numbers right-aligned (B19)', async () => {
			answers[list()] = {
				status: 200,
				body: body([item('g1', { systemName: 'Nintendo Game Boy' })])
			};
			render(MultipleReleases);

			const link = await screen.findByRole('link', { name: 'Game g1' });
			expect(link.getAttribute('href')).toBe('/games/g1');
			expect(
				[...document.querySelectorAll('thead th')].map((th) => [
					th.textContent!.trim(),
					th.className,
					th.getAttribute('scope')
				])
			).toEqual([
				['Game', '', 'col'],
				['System', '', 'col'],
				['Releases', 'text-end', 'col'],
				['Updates', 'text-end', 'col'],
				['Size', 'text-end', 'col']
			]);
			const cells = [...document.querySelectorAll('tbody td')];
			expect(cells.map((td) => td.textContent!.trim())).toEqual([
				'Game g1',
				'Nintendo Game Boy',
				'3',
				'1',
				'2.0 KiB'
			]);
			expect(cells.slice(2).every((td) => td.className === 'text-end text-nowrap')).toBe(true);
			expect(document.querySelector('thead')!.className).toBe('table-dark');
		});

		it('the threshold is disabled with only the current min until the first response, then offers 2…max', async () => {
			let release!: () => void;
			const pending = new Promise<void>((r) => (release = r));
			const answer = globalThis.fetch;
			vi.stubGlobal('fetch', async (url: string, init?: RequestInit) => {
				if (url.startsWith('/api/reports/multiple-releases')) await pending;
				return answer(url, init);
			});
			answers[list()] = { status: 200, body: body([item('g1')], 1, 4) };
			render(MultipleReleases);

			expect(select().disabled).toBe(true);
			expect(optionTexts()).toEqual(['2']);
			release();
			await screen.findByRole('link', { name: 'Game g1' });
			expect(select().disabled).toBe(false);
			expect(optionTexts()).toEqual(['2', '3', '4']);
			expect([...select().options].map((o) => o.value)).toEqual(['2', '3', '4']);
		});

		it('stays enabled with the last options while a later load runs', async () => {
			answers[list()] = { status: 200, body: body([item('g1')], 1, 4) };
			render(MultipleReleases);
			await screen.findByRole('link', { name: 'Game g1' });

			vi.stubGlobal('fetch', () => new Promise<Response>(() => {}));
			await fireEvent.change(select(), { target: { value: '3' } });

			await vi.waitFor(() => expect(screen.queryByRole('link', { name: 'Game g1' })).toBeNull());
			expect(select().disabled).toBe(false);
			expect(optionTexts()).toEqual(['2', '3', '4']);
		});

		it('adds a URL min above the max as an extra option and shows the empty state (B20)', async () => {
			navigateTo('/reports/multiple-releases?min=9');
			answers[list(9)] = { status: 200, body: body([], 0, 4) };
			render(MultipleReleases);

			expect(await screen.findByText('No games have 9 or more releases.')).toBeTruthy();
			expect(optionTexts()).toEqual(['2', '3', '4', '9']);
			expect(select().value).toBe('9');
		});

		it('an empty library still offers 2', async () => {
			answers[list()] = { status: 200, body: body([], 0, 0) };
			render(MultipleReleases);

			expect(await screen.findByText('No games have 2 or more releases.')).toBeTruthy();
			expect(optionTexts()).toEqual(['2']);
		});

		it('the System menu writes system=, resets the page and filters the request (release editing B12, F2)', async () => {
			navigateTo('/reports/multiple-releases?page=2');
			answers[list(2, 2)] = { status: 200, body: body([item('g1')], 60, 4, 2) };
			answers[`${list()}&system=gbc`] = { status: 200, body: body([item('g5')], 1, 2) };
			render(MultipleReleases);
			await screen.findByRole('link', { name: 'Game g1' });
			expect(document.getElementById('multiple-releases-system')).toBeNull();

			await pickSystem('gbc');
			const rows = [...systemToggle().closest('.dropdown')!.querySelectorAll('.dropdown-item')].map(
				(b) => b.textContent!.replace(/\s+/g, ' ').trim()
			);
			expect(rows).toEqual(['All systems', 'GBGame Boy', 'GBCGame Boy Color']);

			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/multiple-releases?system=gbc'));
			await screen.findByRole('link', { name: 'Game g5' });
			expect(systemToggle().getAttribute('aria-label')).toBe('System: Game Boy Color');
		});

		it('changing the threshold writes min, drops page and requests it', async () => {
			navigateTo('/reports/multiple-releases?page=2');
			answers[list(2, 2)] = { status: 200, body: body([item('g1')], 60, 4, 2) };
			answers[list(3)] = { status: 200, body: body([item('g3')], 1, 4) };
			render(MultipleReleases);
			await screen.findByRole('link', { name: 'Game g1' });

			await fireEvent.change(select(), { target: { value: '3' } });

			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/multiple-releases?min=3'));
			await screen.findByRole('link', { name: 'Game g3' });
			expect(calls).toContain(list(3));
		});

		it('choosing the default min leaves it out of the URL', async () => {
			navigateTo('/reports/multiple-releases?min=3');
			answers[list(3)] = { status: 200, body: body([item('g1')], 1, 4) };
			answers[list()] = { status: 200, body: body([item('g2')], 1, 4) };
			render(MultipleReleases);
			await screen.findByRole('link', { name: 'Game g1' });

			await fireEvent.change(select(), { target: { value: '2' } });
			await screen.findByRole('link', { name: 'Game g2' });

			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/multiple-releases'));
		});

		it.each(['?min=abc&page=x', '?min=1&page=0', '?min=10001&page=-1', '?min=2.5&page=1.5'])(
			'bad URL values %s fall back to the defaults',
			async (query) => {
				navigateTo(`/reports/multiple-releases${query}`);
				answers[list()] = { status: 200, body: body([item('g1')]) };
				render(MultipleReleases);

				await screen.findByRole('link', { name: 'Game g1' });
				expect(calls).toContain(list());
			}
		);

		it('reads min and page from the URL; paging writes page', async () => {
			navigateTo('/reports/multiple-releases?min=3&page=2');
			answers[list(3, 2)] = { status: 200, body: body([item('g1')], 120, 4, 2) };
			answers[list(3, 3)] = { status: 200, body: body([item('g9')], 120, 4, 3) };
			render(MultipleReleases);
			await screen.findByRole('link', { name: 'Game g1' });

			await fireEvent.click(screen.getByRole('button', { name: 'Next' }));

			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/multiple-releases?min=3&page=3'));
			await screen.findByRole('link', { name: 'Game g9' });
		});

		it('a page past the last goes to the last page (B20)', async () => {
			navigateTo('/reports/multiple-releases?page=9');
			answers[list(2, 9)] = { status: 200, body: body([], 60, 4, 9) };
			answers[list(2, 2)] = { status: 200, body: body([item('g1')], 60, 4, 2) };
			render(MultipleReleases);

			await screen.findByRole('link', { name: 'Game g1' });
			expect(goto).toHaveBeenCalledWith(...replaced('/reports/multiple-releases?page=2'));
			expect(screen.queryByText(/No games have/)).toBeNull();
		});

		describe('sortable columns (B18, B19)', () => {
			const sorted = (sort: string, dir: string, min = 2, page = 1) =>
				`GET /api/reports/multiple-releases?min=${min}&sort=${sort}&dir=${dir}&page=${page}&pageSize=50`;
			const header = (label: string) => screen.getByRole('button', { name: label }).closest('th')!;
			const iconOf = (label: string) =>
				screen.getByRole('button', { name: label }).querySelector('i')?.className.trim();

			it('every header is a sort button in its th; the active one has aria-sort and the icon', async () => {
				answers[list()] = { status: 200, body: body([item('g1')]) };
				render(MultipleReleases);
				await screen.findByRole('link', { name: 'Game g1' });

				for (const label of ['Game', 'System', 'Releases', 'Updates', 'Size']) {
					const button = screen.getByRole('button', { name: label });
					expect(button.className).toBe(
						'btn btn-link p-0 text-reset text-decoration-none fw-semibold'
					);
					expect(button.getAttribute('type')).toBe('button');
					expect(button.parentElement!.tagName).toBe('TH');
				}
				expect(header('Releases').getAttribute('aria-sort')).toBe('descending');
				expect(iconOf('Releases')).toBe('bi bi-sort-down');
				for (const label of ['Game', 'System', 'Updates', 'Size']) {
					expect(header(label).hasAttribute('aria-sort')).toBe(false);
					expect(iconOf(label)).toBeUndefined();
				}
			});

			it('clicking the active header flips the direction and resets the page', async () => {
				navigateTo('/reports/multiple-releases?page=2');
				answers[list(2, 2)] = { status: 200, body: body([item('g1')], 60, 4, 2) };
				answers[sorted('releases', 'asc')] = { status: 200, body: body([item('g2')], 60) };
				render(MultipleReleases);
				await screen.findByRole('link', { name: 'Game g1' });

				await fireEvent.click(screen.getByRole('button', { name: 'Releases' }));

				expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/multiple-releases?dir=asc'));
				await screen.findByRole('link', { name: 'Game g2' });
				expect(header('Releases').getAttribute('aria-sort')).toBe('ascending');
				expect(iconOf('Releases')).toBe('bi bi-sort-up-alt');
			});

			it('another header sorts in its default direction from page 1', async () => {
				navigateTo('/reports/multiple-releases?min=3&page=2');
				answers[list(3, 2)] = { status: 200, body: body([item('g1')], 60, 4, 2) };
				answers[sorted('name', 'asc', 3)] = { status: 200, body: body([item('g2')], 60) };
				answers[sorted('size', 'desc', 3)] = { status: 200, body: body([item('g3')], 60) };
				render(MultipleReleases);
				await screen.findByRole('link', { name: 'Game g1' });

				await fireEvent.click(screen.getByRole('button', { name: 'Game' }));
				expect(goto).toHaveBeenLastCalledWith(
					...replaced('/reports/multiple-releases?min=3&sort=name')
				);
				await screen.findByRole('link', { name: 'Game g2' });
				expect(header('Game').getAttribute('aria-sort')).toBe('ascending');
				expect(header('Releases').hasAttribute('aria-sort')).toBe(false);

				await fireEvent.click(screen.getByRole('button', { name: 'Size' }));
				expect(goto).toHaveBeenLastCalledWith(
					...replaced('/reports/multiple-releases?min=3&sort=size')
				);
				await screen.findByRole('link', { name: 'Game g3' });
			});

			it('a non-default direction for a text column is written', async () => {
				navigateTo('/reports/multiple-releases?sort=system');
				answers[sorted('system', 'asc')] = { status: 200, body: body([item('g1')]) };
				answers[sorted('system', 'desc')] = { status: 200, body: body([item('g2')]) };
				render(MultipleReleases);
				await screen.findByRole('link', { name: 'Game g1' });

				await fireEvent.click(screen.getByRole('button', { name: 'System' }));

				expect(goto).toHaveBeenLastCalledWith(
					...replaced('/reports/multiple-releases?sort=system&dir=desc')
				);
				await screen.findByRole('link', { name: 'Game g2' });
			});

			it.each([
				['?sort=updates&dir=asc', sorted('updates', 'asc')],
				['?sort=name', sorted('name', 'asc')],
				['?sort=Name&dir=ASC', sorted('releases', 'desc')],
				['?sort=title&dir=up', sorted('releases', 'desc')],
				['?sort=size&dir=sideways', sorted('size', 'desc')]
			])('URL %s requests %s', async (query, request) => {
				navigateTo(`/reports/multiple-releases${query}`);
				answers[request] = { status: 200, body: body([item('g1')]) };
				render(MultipleReleases);

				await screen.findByRole('link', { name: 'Game g1' });
				expect(calls.filter((c) => c.includes('multiple-releases'))).toEqual([request]);
			});
		});

		it('the Multiple Releases tab is there for a Site user (B14)', () => {
			auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
			render(ReportsLayout, {
				children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
			});
			const tab = screen.getByRole('link', { name: 'Multiple Releases' });
			expect(tab.getAttribute('href')).toBe('/reports/multiple-releases');
			expect(tab.querySelector('i')!.className.trim()).toBe('bi bi-layers');
		});
	});

	describe('Newly Added (release editing B14)', () => {
		beforeEach(() => navigateTo('/reports/newly-added'));

		it('shows one card per system with game, added time and releases', async () => {
			answers['GET /api/reports/newly-added'] = {
				status: 200,
				body: {
					systems: [
						{
							system: 'gb',
							systemName: 'Game Boy',
							games: [
								{
									id: 'g1',
									displayTitle: 'Tetris',
									addedAt: '2026-01-02T03:04:00Z',
									releaseCount: 2
								}
							]
						},
						{
							system: 'gbc',
							systemName: 'Game Boy Color',
							games: [
								{
									id: 'g2',
									displayTitle: 'Zelda',
									addedAt: '2026-01-01T00:00:00Z',
									releaseCount: 1
								}
							]
						}
					]
				}
			};
			render(NewlyAdded);

			const link = await screen.findByRole('link', { name: 'Tetris' });
			expect(link.getAttribute('href')).toBe('/games/g1');
			const cards = [...document.querySelectorAll('.card')];
			expect(
				cards.map((c) => c.querySelector('.card-header')!.textContent!.replace(/\s+/g, ' ').trim())
			).toEqual(['GB Game Boy', 'GBC Game Boy Color']);
			expect([...cards[0].querySelectorAll('th')].map((th) => th.textContent!.trim())).toEqual([
				'Game',
				'Added',
				'Releases'
			]);
			const cells = [...cards[0].querySelectorAll('tbody td')];
			expect(cells[2].textContent!.trim()).toBe('2');
			expect(cells[2].className).toBe('text-end');
			expect(cells[1].textContent!.trim()).not.toBe('');
		});

		it('the System menu writes system= and requests it (F2)', async () => {
			answers['GET /api/reports/newly-added'] = { status: 200, body: { systems: [] } };
			answers['GET /api/reports/newly-added?system=gbc'] = { status: 200, body: { systems: [] } };
			render(NewlyAdded);

			await pickSystem('gbc');

			expect(goto).toHaveBeenLastCalledWith('/reports/newly-added?system=gbc', {
				replace: true,
				reset: false
			});
			await vi.waitFor(() => expect(calls).toContain('GET /api/reports/newly-added?system=gbc'));
		});

		it('an empty library shows "No games yet."', async () => {
			answers['GET /api/reports/newly-added'] = { status: 200, body: { systems: [] } };
			render(NewlyAdded);

			expect(await screen.findByText('No games yet.')).toBeTruthy();
		});
	});

	describe('Games With Hacks (release editing B13)', () => {
		const list = (query = 'sort=hacks&dir=desc') =>
			`GET /api/reports/hacks?${query}&page=1&pageSize=50`;
		const item = (id: string) => ({
			id,
			displayTitle: `Game ${id}`,
			system: 'gb',
			systemName: 'Game Boy',
			hackCount: 2,
			releaseCount: 3,
			bytes: 2048
		});
		const body = (items: unknown[]) => ({ items, page: 1, pageSize: 50, total: items.length });
		const replaced = (path: string) => [path, { replace: true, reset: false }];

		beforeEach(() => navigateTo('/reports/hacks'));

		it('lists game (to its Releases tab), system, hacks, releases and size', async () => {
			answers[list()] = { status: 200, body: body([item('g1')]) };
			render(Hacks);

			const link = await screen.findByRole('link', { name: 'Game g1' });
			expect(link.getAttribute('href')).toBe('/games/g1?tab=releases');
			expect(
				[...document.querySelectorAll('thead th')].map((th) => th.textContent!.trim())
			).toEqual(['Game', 'System', 'Hacks', 'Releases', 'Size']);
			expect(
				[...document.querySelectorAll('tbody td')].map((td) => td.textContent!.trim())
			).toEqual(['Game g1', 'Game Boy', '2', '3', '2.0 KiB']);
			expect(document.querySelector('th[aria-sort]')!.textContent!.trim()).toBe('Hacks');
		});

		it('sorting by a header writes sort= and the system select writes system=', async () => {
			answers[list()] = { status: 200, body: body([item('g1')]) };
			// The page refetches after each change; answer those too.
			answers[list('sort=name&dir=asc')] = { status: 200, body: body([item('g1')]) };
			answers[`${list('sort=name&dir=asc')}&system=gb`] = { status: 200, body: body([item('g1')]) };
			render(Hacks);
			await screen.findByRole('link', { name: 'Game g1' });

			await fireEvent.click(screen.getByRole('button', { name: 'Game' }));
			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/hacks?sort=name'));

			await pickSystem('gb');
			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/hacks?system=gb&sort=name'));
		});

		it('shows the empty state', async () => {
			answers[list()] = { status: 200, body: body([]) };
			render(Hacks);

			expect(await screen.findByText('No games have hacked releases.')).toBeTruthy();
		});
	});

	describe('Missing Metadata (M4, M6)', () => {
		const admin = () =>
			auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		const list = (query = '') =>
			`GET /api/reports/missing-metadata?missing=any&sort=name&dir=asc&page=1&pageSize=50${query}`;
		const item = (id: string, extra: Record<string, unknown> = {}) => ({
			id,
			displayTitle: `Game ${id}`,
			system: 'gb',
			systemName: 'Game Boy',
			releaseYear: 1990,
			developers: ['Dev A', 'Dev B'],
			publishers: ['Pub'],
			...extra
		});
		const body = (items: unknown[], total = items.length) => ({
			items,
			page: 1,
			pageSize: 50,
			total
		});
		const replaced = (path: string) => [path, { replace: true, reset: false }];
		const layout = () =>
			render(ReportsLayout, {
				children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
			});

		beforeEach(() => navigateTo('/reports/missing-metadata'));

		it('the tab shows for Admins only, between Multiple Releases and Activity', () => {
			admin();
			layout();
			expect(
				screen
					.getAllByRole('link')
					.map((a) => [a.textContent!.trim(), a.querySelector('i')?.className.trim()])
			).toEqual([
				['Newly Added', 'bi bi-stars'],
				['Library Size', 'bi bi-pie-chart'],
				['Missing Images', 'bi bi-image-alt'],
				['Duplicates', 'bi bi-files'],
				['Multiple Releases', 'bi bi-layers'],
				['Games With Hacks', 'bi bi-bandaid'],
				['Missing Metadata', 'bi bi-card-checklist'],
				['Activity', 'bi bi-activity']
			]);
			expect(screen.getByRole('link', { name: 'Missing Metadata' }).getAttribute('href')).toBe(
				'/reports/missing-metadata'
			);
		});

		it('a Site user on the page gets NotAllowed, one h1 and no tabs', () => {
			auth.set({ id: 2, username: 'site', roles: [], mustChangePassword: false });
			layout();
			expect(screen.queryByRole('link', { name: 'Missing Metadata' })).toBeNull();
			expect(screen.queryByRole('list')).toBeNull();
			expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
			expect(screen.queryByText('Content')).toBeNull();
		});

		it('lists the columns with Missing badges, joined lists and links carrying from', async () => {
			admin();
			answers[list()] = {
				status: 200,
				body: body([
					item('g1', { releaseYear: null }),
					item('g2', { developers: [], publishers: [] })
				])
			};
			render(MissingMetadata);

			const link = await screen.findByRole('link', { name: 'Game g1' });
			expect(link.getAttribute('href')).toBe('/games/g1?from=missing-metadata');
			expect(
				[...document.querySelectorAll('thead th')].map((th) => th.textContent!.trim())
			).toEqual(['Game', 'System', 'Year', 'Developer', 'Publisher']);
			expect(document.querySelector('thead')!.className).toBe('table-dark');
			const cells = (id: string) =>
				[...screen.getByRole('link', { name: `Game ${id}` }).closest('tr')!.children].map((c) =>
					c.textContent!.trim()
				);
			expect(cells('g1')).toEqual(['Game g1', 'Game Boy', 'Missing', 'Dev A, Dev B', 'Pub']);
			expect(cells('g2')).toEqual(['Game g2', 'Game Boy', '1990', 'Missing', 'Missing']);
			expect(document.querySelector('tbody .badge.text-bg-warning')!.textContent).toBe('Missing');
			expect(screen.getByText('2 games')).toBeTruthy();
		});

		it('the total line reads "1,234 games" and "1 game"', async () => {
			admin();
			answers[list()] = { status: 200, body: body([item('g1')], 1234) };
			const { unmount } = render(MissingMetadata);
			expect(await screen.findByText('1,234 games')).toBeTruthy();
			expect(screen.getByText('1,234 games').className).toBe('small text-body-secondary mb-2');
			unmount();

			answers[list()] = { status: 200, body: body([item('g1')]) };
			render(MissingMetadata);
			expect(await screen.findByText('1 game')).toBeTruthy();
		});

		it('empty: the unfiltered text, no total line', async () => {
			admin();
			answers[list()] = { status: 200, body: body([]) };
			render(MissingMetadata);

			expect(
				await screen.findByText('Every game has its year, developer and publisher.')
			).toBeTruthy();
			expect(screen.queryByText(/\d games?$/)).toBeNull();
		});

		it('empty with a filter: the filtered text', async () => {
			admin();
			navigateTo('/reports/missing-metadata?missing=year');
			answers[
				'GET /api/reports/missing-metadata?missing=year&sort=name&dir=asc&page=1&pageSize=50'
			] = { status: 200, body: body([]) };
			render(MissingMetadata);

			expect(await screen.findByText('No games match these filters.')).toBeTruthy();
		});

		it('the System menu writes system= from page 1 (F2)', async () => {
			admin();
			navigateTo('/reports/missing-metadata?page=2');
			answers[list().replace('page=1', 'page=2')] = {
				status: 200,
				body: { ...body([item('g1')], 60), page: 2 }
			};
			render(MissingMetadata);
			await screen.findByRole('link', { name: 'Game g1' });
			expect(document.getElementById('missing-metadata-system')).toBeNull();

			await pickSystem('gbc');

			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/missing-metadata?system=gbc'));
		});

		it('URL state reaches the API; filters write the URL from page 1', async () => {
			admin();
			navigateTo(
				'/reports/missing-metadata?system=gb&missing=developer&q=zel&sort=year&dir=desc&page=2'
			);
			const request =
				'GET /api/reports/missing-metadata?missing=developer&sort=year&dir=desc&page=2&pageSize=50&system=gb&q=zel';
			answers[request] = { status: 200, body: { ...body([item('g1')], 60), page: 2 } };
			render(MissingMetadata);

			await screen.findByRole('link', { name: 'Game g1' });
			expect(calls).toContain(request);
			expect((screen.getByLabelText('Title') as HTMLInputElement).value).toBe('zel');

			await fireEvent.change(screen.getByLabelText('Missing'), { target: { value: 'publisher' } });
			expect(goto).toHaveBeenLastCalledWith(
				...replaced(
					'/reports/missing-metadata?system=gb&missing=publisher&q=zel&sort=year&dir=desc'
				)
			);
		});

		it('sortable headers: Game, System and Year; the active one flips', async () => {
			admin();
			answers[list()] = { status: 200, body: body([item('g1')]) };
			render(MissingMetadata);
			await screen.findByRole('link', { name: 'Game g1' });

			expect(screen.queryByRole('button', { name: 'Developer' })).toBeNull();
			expect(screen.getByRole('button', { name: 'System' })).toBeTruthy();
			expect(screen.getByRole('columnheader', { name: /Game/ }).getAttribute('aria-sort')).toBe(
				'ascending'
			);
			await fireEvent.click(screen.getByRole('button', { name: 'Year' }));
			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/missing-metadata?sort=year'));
		});

		it('clicking the active header flips its direction, from page 1', async () => {
			admin();
			navigateTo('/reports/missing-metadata?page=2');
			answers[
				'GET /api/reports/missing-metadata?missing=any&sort=name&dir=asc&page=2&pageSize=50'
			] = {
				status: 200,
				body: { ...body([item('g1')], 60), page: 2 }
			};
			render(MissingMetadata);
			await screen.findByRole('link', { name: 'Game g1' });

			await fireEvent.click(screen.getByRole('button', { name: /^Game/ }));
			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/missing-metadata?dir=desc'));
		});

		it('the title search is debounced and Clear resets every filter', async () => {
			admin();
			answers[list()] = { status: 200, body: body([item('g1')]) };
			render(MissingMetadata);
			await screen.findByRole('link', { name: 'Game g1' });
			const clear = screen.getByRole('button', { name: 'Clear' }) as HTMLButtonElement;
			expect(clear.disabled).toBe(true);

			await fireEvent.input(screen.getByLabelText('Title'), { target: { value: ' mario ' } });
			expect(goto).not.toHaveBeenCalled();
			await vi.waitFor(() =>
				expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/missing-metadata?q=mario'))
			);

			navigateTo('/reports/missing-metadata?system=gb&missing=year&q=mario&sort=year');
			await vi.waitFor(() => expect(clear.disabled).toBe(false));
			await fireEvent.click(clear);
			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/missing-metadata?sort=year'));
		});

		it('pagination writes the page', async () => {
			admin();
			answers[list()] = { status: 200, body: body([item('g1')], 120) };
			render(MissingMetadata);
			await screen.findByRole('link', { name: 'Game g1' });

			await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
			expect(goto).toHaveBeenLastCalledWith(...replaced('/reports/missing-metadata?page=2'));
		});
	});

	it('report tabs carry icons (B18)', () => {
		render(ReportsLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		expect(
			screen
				.getAllByRole('link')
				.map((a) => [a.textContent!.trim(), a.querySelector('i')?.className.trim()])
		).toEqual([
			['Newly Added', 'bi bi-stars'],
			['Library Size', 'bi bi-pie-chart'],
			['Missing Images', 'bi bi-image-alt'],
			['Duplicates', 'bi bi-files'],
			['Multiple Releases', 'bi bi-layers'],
			['Games With Hacks', 'bi bi-bandaid']
		]);
	});

	it('the Reports layout has one page-title h1 (B1)', () => {
		render(ReportsLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		expectOnePageTitle('Reports');
	});
});
