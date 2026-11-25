import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';
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
// Bootstrap's Modal (GameResults' play dialog) needs layout/transitions jsdom lacks.
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

const { default: TimelinePage } = await import('./+page.svelte');

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
/** 1990 (2 games), 1992 (1 game), 1999 (1,234 games): a data range of 1990–1999. */
const timeline = {
	igdbConfigured: true,
	total: 1237,
	years: [
		{ year: 1990, count: 2 },
		{ year: 1992, count: 1 },
		{ year: 1999, count: 1234 }
	]
};

describe('timeline page (T1, T3–T5)', () => {
	let calls: string[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		navigateTo('/timeline');
		calls = [];
		answers = {
			'GET /api/systems': { status: 200, body: systems },
			'GET /api/timeline': { status: 200, body: timeline }
		};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	const strip = () => screen.getByRole('group', { name: 'Release years' });
	const tickNames = () =>
		within(strip())
			.getAllByRole('button')
			.map((b) => b.getAttribute('aria-label'));
	const pushed = () => goto.mock.calls.at(-1);

	it('has one Timeline title and the page title', async () => {
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });
		expectOnePageTitle('Timeline');
		expect(document.title).toBe('Timeline · RnRomManager');
	});

	it('asks for the systems first, then the timeline', async () => {
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });
		expect(calls).toEqual([
			'GET /api/systems',
			'GET /api/timeline',
			'GET /api/timeline/games?from=1990&to=1999&pageSize=50'
		]);
	});

	it('a known system filters the counts; an unknown one is ignored', async () => {
		answers['GET /api/timeline?system=gbc'] = { status: 200, body: timeline };
		navigateTo('/timeline?system=gbc');
		const { unmount } = render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });
		expect(calls).toContain('GET /api/timeline?system=gbc');
		unmount();

		calls = [];
		navigateTo('/timeline?system=nope');
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });
		expect(calls).toEqual([
			'GET /api/systems',
			'GET /api/timeline',
			'GET /api/timeline/games?from=1990&to=1999&pageSize=50'
		]);
	});

	it('a line with a point per year with games and a tick per empty year, cards alternating', async () => {
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		expect(strip().className).toBe('timeline-line overflow-x-auto pb-2 mb-3');
		const row = strip().firstElementChild!;
		expect(row.className).toContain('timeline-row d-flex position-relative');
		expect(row.children).toHaveLength(10);
		expect(tickNames()).toEqual(['1990: 2 games', '1992: 1 game', '1999: 1,234 games']);
		const points = within(strip()).getAllByRole('button');
		expect(points.map((b) => b.className.replace(/\s*svelte-\S+/g, ''))).toEqual([
			'timeline-point above',
			'timeline-point below',
			'timeline-point above'
		]);
		const ticks = [...strip().querySelectorAll('.timeline-tick')];
		expect(ticks.map((t) => t.getAttribute('title'))).toEqual(
			[1991, 1993, 1994, 1995, 1996, 1997, 1998].map((y) => `${y}: no games`)
		);
		expect(ticks.every((t) => t.getAttribute('aria-hidden') === 'true')).toBe(true);
		expect(strip().querySelector('.timeline-tick-label')).toBeNull();
		const many = within(strip()).getByRole('button', { name: '1999: 1,234 games' });
		expect(many.textContent!.replace(/\s+/g, ' ').trim()).toBe('1999 1,234');
		const card = many.querySelector('.timeline-card')!;
		for (const name of ['timeline-card', 'border', 'rounded', 'bg-body', 'px-2', 'py-1', 'small'])
			expect(card.classList).toContain(name);
		expect(card.firstElementChild!.className).toContain('fw-semibold');
		expect(many.querySelector('.timeline-dot')!.classList).toContain('bg-primary');
		for (const point of points) expect(point.getAttribute('aria-pressed')).toBe('false');
	});

	it('an empty first shown year is a labelled tick', async () => {
		navigateTo('/timeline?from=1991&to=1992');
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		expect(tickNames()).toEqual(['1992: 1 game']);
		const label = strip().querySelector('.timeline-tick-label')!;
		expect(label.textContent).toBe('1991');
		expect(label.className).toContain('small text-body-secondary');
		expect(label.closest('.timeline-tick')!.getAttribute('title')).toBe('1991: no games');
	});

	it('a tick selects its year; the single-year tick is pressed', async () => {
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		await fireEvent.click(within(strip()).getByRole('button', { name: '1992: 1 game' }));
		expect(pushed()).toEqual(['/timeline?from=1992&to=1992', { reset: false }]);

		const pressed = await vi.waitFor(() => {
			const tick = within(strip()).getByRole('button', { name: '1992: 1 game' });
			if (tick.getAttribute('aria-pressed') !== 'true') throw new Error('not pressed');
			return tick;
		});
		const card = pressed.querySelector('.timeline-card')!;
		expect(card.classList).toContain('text-bg-primary');
		expect(card.classList).toContain('border-primary');
		expect(card.classList).not.toContain('bg-body');
		expect(tickNames()).toEqual(['1992: 1 game']);
		expect(document.querySelector('span[aria-live]')!.textContent).toBe('1992');
	});

	it('reads from and to from the URL, clamped, with the range label', async () => {
		navigateTo('/timeline?from=1985&to=1993&page=4');
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		expect(tickNames()[0]).toBe('1990: 2 games');
		expect(tickNames().at(-1)).toBe('1992: 1 game');
		expect([...strip().querySelectorAll('.timeline-tick')].at(-1)!.getAttribute('title')).toBe(
			'1993: no games'
		);
		const label = screen.getByText('1990–1993');
		expect(label.className).toBe('small text-body-secondary');
		expect(label.getAttribute('aria-live')).toBe('polite');
	});

	it('the controls navigate from page 1 with a new history entry, and disable at the edges', async () => {
		navigateTo('/timeline?from=1992&to=1996&page=3');
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });
		const button = (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement;

		expect(button('Zoom in').querySelector('.bi-zoom-in')).not.toBeNull();
		expect(button('Earlier').querySelector('.bi-chevron-left')).not.toBeNull();
		expect(
			[
				button('Zoom in'),
				button('Zoom out'),
				button('Earlier'),
				button('Later'),
				button('Show all')
			].map((b) => b.disabled)
		).toEqual([false, false, false, false, false]);

		await fireEvent.click(button('Zoom in'));
		expect(pushed()).toEqual(['/timeline?from=1993&to=1995', { reset: false }]);
		navigateTo('/timeline?from=1992&to=1996');
		await fireEvent.click(button('Zoom out'));
		expect(pushed()).toEqual(['/timeline', { reset: false }]);
		navigateTo('/timeline?from=1992&to=1996');
		await fireEvent.click(button('Earlier'));
		expect(pushed()).toEqual(['/timeline?from=1990&to=1994', { reset: false }]);
		navigateTo('/timeline?from=1992&to=1996');
		await fireEvent.click(button('Later'));
		expect(pushed()).toEqual(['/timeline?from=1994&to=1998', { reset: false }]);
		navigateTo('/timeline?from=1992&to=1996');
		await fireEvent.click(button('Show all'));
		expect(pushed()).toEqual(['/timeline', { reset: false }]);

		navigateTo('/timeline');
		await vi.waitFor(() => expect(button('Show all').disabled).toBe(true));
		expect(
			[button('Zoom in'), button('Zoom out'), button('Earlier'), button('Later')].map(
				(b) => b.disabled
			)
		).toEqual([false, true, true, true]);
		navigateTo('/timeline?from=1995&to=1995');
		await vi.waitFor(() => expect(button('Zoom in').disabled).toBe(true));
	});

	it('changing system shows its whole range', async () => {
		answers['GET /api/timeline?system=gb'] = { status: 200, body: timeline };
		navigateTo('/timeline?from=1992&to=1996&page=2');
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		const toggle = screen.getByRole('button', { name: /^System: / });
		const row = await vi.waitFor(() => {
			const found = toggle
				.closest('.dropdown')!
				.querySelector<HTMLButtonElement>('.dropdown-item[data-value="gb"]');
			if (!found) throw new Error('no gb row');
			return found;
		});
		await fireEvent.click(row);

		expect(pushed()).toEqual(['/timeline?system=gb', { reset: false }]);
		await vi.waitFor(() => expect(calls).toContain('GET /api/timeline?system=gb'));
	});

	it('no linked game with a year: the empty state, no strip and no controls', async () => {
		answers['GET /api/timeline'] = {
			status: 200,
			body: { igdbConfigured: true, total: 0, years: [] }
		};
		render(TimelinePage);

		expect(
			await screen.findByText('No games linked to IGDB have a release year yet.')
		).toBeTruthy();
		expect(screen.queryByRole('group', { name: 'Release years' })).toBeNull();
		expect(screen.queryByRole('button', { name: 'Zoom in' })).toBeNull();
	});

	describe('games table (T6)', () => {
		const item = (id: string, title: string, year: number) => ({
			id,
			system: 'gb',
			systemName: 'Game Boy',
			systemShortName: 'GB',
			displayTitle: title,
			releaseYear: year,
			regions: ['USA'],
			romCount: 1,
			primarySizeBytes: 1024,
			boxArtUrl: null,
			rating: null,
			favourite: false,
			matchedOn: null,
			updateCount: 0,
			dlcCount: 0,
			saveCount: 0,
			playRomId: null,
			playRomFileName: null,
			playDisc: false,
			browserCore: null,
			igdbId: 77
		});
		const gamesPage = (items: unknown[], total = items.length, page = 1) => ({
			items,
			page,
			pageSize: 50,
			total
		});
		const all = 'GET /api/timeline/games?from=1990&to=1999&pageSize=50';

		it('requests the shown years and lists the games with a count line', async () => {
			answers[all] = { status: 200, body: gamesPage([item('g1', 'Orbit Kart', 1990)], 1234) };
			render(TimelinePage);

			const link = await screen.findByRole('link', { name: 'Orbit Kart' });
			expect(link.closest('table')).toBeTruthy();
			const count = screen.getByText('1,234 games');
			expect(count.className).toBe('small text-body-secondary mb-2');
			expect(calls).toContain(all);
		});

		it('sends system and page, and pages through the URL from the current range', async () => {
			const url = 'GET /api/timeline/games?system=gb&from=1992&to=1996&page=2&pageSize=50';
			answers['GET /api/timeline?system=gb'] = { status: 200, body: timeline };
			answers[url] = { status: 200, body: gamesPage([item('g1', 'Orbit Kart', 1992)], 120, 2) };
			navigateTo('/timeline?system=gb&from=1992&to=1996&page=2');
			render(TimelinePage);

			await screen.findByRole('link', { name: 'Orbit Kart' });
			expect(calls).toContain(url);
			await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
			expect(pushed()).toEqual(['/timeline?system=gb&from=1992&to=1996&page=3', { reset: false }]);
		});

		it('one game reads "1 game"', async () => {
			answers[all] = { status: 200, body: gamesPage([item('g1', 'Orbit Kart', 1990)]) };
			render(TimelinePage);

			expect(await screen.findByText('1 game')).toBeTruthy();
		});

		it('View on IGDB follows Play in each row when IGDB is configured', async () => {
			answers[all] = { status: 200, body: gamesPage([item('g1', 'Orbit Kart', 1990)]) };
			render(TimelinePage);

			const igdb = await screen.findByRole('link', { name: 'View Orbit Kart on IGDB' });
			expect(igdb.getAttribute('href')).toBe('/api/igdb/games/77/page');
			expect(igdb.className).toBe('btn btn-sm btn-outline-secondary');
			expect(igdb.getAttribute('title')).toBe('View on IGDB');
			expect([igdb.getAttribute('target'), igdb.getAttribute('rel')]).toEqual([
				'_blank',
				'noopener noreferrer'
			]);
			expect(igdb.querySelector('.bi-box-arrow-up-right')).not.toBeNull();
			expect(igdb.closest('td')!.className).toBe('text-end text-nowrap');
		});

		it('no View on IGDB when IGDB is not configured', async () => {
			answers['GET /api/timeline'] = { status: 200, body: { ...timeline, igdbConfigured: false } };
			answers[all] = { status: 200, body: gamesPage([item('g1', 'Orbit Kart', 1990)]) };
			render(TimelinePage);

			await screen.findByRole('link', { name: 'Orbit Kart' });
			expect(screen.queryByRole('link', { name: /on IGDB/ })).toBeNull();
		});

		it('a range with no games keeps the strip and says so', async () => {
			answers['GET /api/timeline/games?from=1991&to=1991&pageSize=50'] = {
				status: 200,
				body: gamesPage([])
			};
			navigateTo('/timeline?from=1991&to=1991');
			render(TimelinePage);

			expect(await screen.findByText('No games in this period.')).toBeTruthy();
			expect(screen.getByRole('group', { name: 'Release years' })).toBeTruthy();
			expect(screen.queryByText(/\d games?$/)).toBeNull();
		});
	});

	it('a failed timeline shows the error and retries', async () => {
		answers['GET /api/timeline'] = { status: 500, body: { status: 500, title: 'Boom' } };
		render(TimelinePage);

		expect(await screen.findByText('Boom')).toBeTruthy();
		answers['GET /api/timeline'] = { status: 200, body: timeline };
		await fireEvent.click(screen.getByRole('button', { name: /Retry/ }));
		expect(await screen.findByRole('group', { name: 'Release years' })).toBeTruthy();
	});
});
