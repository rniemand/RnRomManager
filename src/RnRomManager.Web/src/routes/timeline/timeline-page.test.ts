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
vi.mock('#lib/bootstrap.js', () => ({
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
		expect(calls).toEqual(['GET /api/systems', 'GET /api/timeline']);
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
		expect(calls).toEqual(['GET /api/systems', 'GET /api/timeline']);
	});

	it('one tick per year of the data range, zero-count years included, with counts and names', async () => {
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		expect(strip().className).toBe('d-flex gap-1 overflow-x-auto pb-2 mb-3');
		expect(tickNames()).toEqual([
			'1990: 2 games',
			'1991: 0 games',
			'1992: 1 game',
			'1993: 0 games',
			'1994: 0 games',
			'1995: 0 games',
			'1996: 0 games',
			'1997: 0 games',
			'1998: 0 games',
			'1999: 1,234 games'
		]);
		const zero = within(strip()).getByRole('button', { name: '1991: 0 games' });
		expect(zero.querySelectorAll('span')[1].className).toContain('text-body-secondary');
		const many = within(strip()).getByRole('button', { name: '1999: 1,234 games' });
		expect(many.textContent!.replace(/\s+/g, ' ').trim()).toBe('1999 1,234');
		expect(many.querySelectorAll('span')[1].className).not.toContain('text-body-secondary');
		for (const tick of within(strip()).getAllByRole('button')) {
			expect(tick.getAttribute('aria-pressed')).toBe('false');
			expect(tick.classList).toContain('btn-outline-secondary');
		}
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
		expect(pressed.classList).toContain('btn-primary');
		expect(tickNames()).toEqual(['1992: 1 game']);
		expect(document.querySelector('span[aria-live]')!.textContent).toBe('1992');
	});

	it('reads from and to from the URL, clamped, with the range label', async () => {
		navigateTo('/timeline?from=1985&to=1993&page=4');
		render(TimelinePage);
		await screen.findByRole('group', { name: 'Release years' });

		expect(tickNames()[0]).toBe('1990: 2 games');
		expect(tickNames().at(-1)).toBe('1993: 0 games');
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

	it('a failed timeline shows the error and retries', async () => {
		answers['GET /api/timeline'] = { status: 500, body: { status: 500, title: 'Boom' } };
		render(TimelinePage);

		expect(await screen.findByText('Boom')).toBeTruthy();
		answers['GET /api/timeline'] = { status: 200, body: timeline };
		await fireEvent.click(screen.getByRole('button', { name: /Retry/ }));
		expect(await screen.findByRole('group', { name: 'Release years' })).toBeTruthy();
	});
});
