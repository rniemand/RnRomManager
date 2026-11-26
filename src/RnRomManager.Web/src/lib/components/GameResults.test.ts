import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { SvelteSet } from 'svelte/reactivity';
import type { GameListItem } from '#lib/types/index.js';
import { resetClient } from '../api/client';
import { auth } from '../stores/auth.svelte';
import { toasts } from '../stores/toasts.svelte';

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));
const tooltips: string[] = [];
vi.mock('../tooltip', () => ({
	tooltip: (_node: Element, text: string) => {
		tooltips.push(text);
		return {};
	}
}));

const { default: GameResults } = await import('./GameResults.svelte');

function game(id: string, extra: Partial<GameListItem> = {}): GameListItem {
	return {
		id,
		system: 'gb',
		displayTitle: `Game ${id}`,
		releaseYear: 1990,
		regions: ['USA'],
		romCount: 1,
		primarySizeBytes: 1024,
		boxArtUrl: null,
		systemName: 'Game Boy',
		systemShortName: 'GB',
		rating: null,
		favourite: false,
		matchedOn: null,
		updateCount: 0,
		dlcCount: 0,
		saveCount: 0,
		ownSaveCount: 0,
		playRomId: null,
		playRomFileName: null,
		playDisc: false,
		browserCore: null,
		igdbId: null,
		...extra
	};
}

const playable = (id: string) =>
	game(id, { playRomId: 7, playRomFileName: 'Game (World).gb', browserCore: 'gambatte' });

describe('GameResults cards (C1, C2)', () => {
	afterEach(() => auth.set(null));

	it.each(['grid', 'list'] as const)(
		'%s: ratings are editable for an Admin and read-only for a Site user (U3)',
		(view) => {
			auth.set({ id: 1, username: 'a', roles: ['admin'], mustChangePassword: false });
			const admin = render(GameResults, { games: [game('a')], view });
			expect(screen.getAllByRole('button', { name: /Rate 1 of 5/ })).toHaveLength(1);
			admin.unmount();

			auth.set({ id: 2, username: 's', roles: [], mustChangePassword: false });
			render(GameResults, { games: [game('a')], view });
			expect(screen.queryByRole('button', { name: /Rate 1 of 5/ })).toBeNull();
		}
	);

	it.each(['grid', 'list'] as const)('%s: a game with saves links to its Saves tab', (view) => {
		render(GameResults, { games: [game('a', { saveCount: 2 }), game('b')], view });

		const links = screen.getAllByRole('link', { name: /^Saves of/ });
		expect(links.map((l) => [l.getAttribute('aria-label'), l.getAttribute('href')])).toEqual([
			['Saves of Game a', '/games/a?tab=saves']
		]);
	});

	it.each(['grid', 'list'] as const)(
		'%s: the updates badge shows only when updateCount > 0',
		(view) => {
			tooltips.length = 0;
			render(GameResults, {
				games: [game('a', { updateCount: 1 }), game('b', { updateCount: 1250 }), game('c')],
				view
			});

			const badges = [...document.querySelectorAll('.badge.text-bg-info')];
			expect(
				badges.map((b) => b.querySelector('[aria-hidden="true"]')!.textContent!.trim())
			).toEqual(['1', '1,250']);
			expect(badges.map((b) => b.querySelector('.visually-hidden')!.textContent)).toEqual([
				'1 update',
				'1,250 updates'
			]);
			expect(tooltips.filter((t) => t.includes('update'))).toEqual(['1 update', '1,250 updates']);
			expect(badges[0].querySelector('.bi-arrow-up-circle')).toBeTruthy();
			if (view === 'grid')
				expect(badges[0].parentElement!.className).toContain(
					'position-absolute bottom-0 start-0 m-1'
				);
			else expect(badges[0].closest('td')!.querySelector('a')!.textContent).toBe('Game a');
		}
	);

	it.each(['grid', 'list'] as const)(
		'%s: the DLC badge shows only when dlcCount > 0 (DLC B7)',
		(view) => {
			tooltips.length = 0;
			render(GameResults, {
				games: [game('a', { dlcCount: 1, updateCount: 2 }), game('b', { dlcCount: 3 }), game('c')],
				view
			});

			const badges = [...document.querySelectorAll('.badge.text-bg-info')].filter((b) =>
				b.querySelector('.bi-puzzle')
			);
			expect(badges.map((b) => b.querySelector('.visually-hidden')!.textContent)).toEqual([
				'1 DLC',
				'3 DLC'
			]);
			expect(tooltips.filter((t) => t.includes('DLC'))).toEqual(['1 DLC', '3 DLC']);
			// Right after the updates badge.
			expect(badges[0].previousElementSibling!.querySelector('.bi-arrow-up-circle')).toBeTruthy();
		}
	);

	it('grid: the releases badge shows only when romCount > 1', () => {
		tooltips.length = 0;
		render(GameResults, {
			games: [game('a', { romCount: 3 }), game('b'), game('c', { romCount: 0 })],
			view: 'grid'
		});

		const badges = [...document.querySelectorAll('.badge.text-bg-secondary')];
		expect(badges.map((b) => b.querySelector('.visually-hidden')!.textContent)).toEqual([
			'3 releases'
		]);
		expect(tooltips).toContain('3 releases');
		expect(badges[0].querySelector('.bi-stack')).toBeTruthy();
	});

	it('grid: Play shows only for a playable game, round, over the box art and above the link', () => {
		render(GameResults, { games: [playable('a'), game('b')], view: 'grid' });

		const play = screen.getByRole('button', { name: 'Play Game a' });
		expect(play.className).toBe(
			'btn btn-sm btn-primary rounded-circle position-absolute bottom-0 end-0 m-1 z-2'
		);
		expect(screen.queryByRole('button', { name: 'Play Game b' })).toBeNull();
	});

	it('list: Play is an outline icon button in a right-aligned Actions column, shown for Play alone', () => {
		render(GameResults, { games: [playable('a'), game('b')], view: 'list' });

		expect(screen.getByRole('columnheader', { name: 'Actions' })).toBeTruthy();
		const play = screen.getByRole('button', { name: 'Play Game a' });
		expect(play.className).toBe('btn btn-sm btn-outline-success');
		expect(play.closest('td')!.classList.contains('text-end')).toBe(true);
	});

	it('list: no Actions column without Play or an action', () => {
		render(GameResults, { games: [game('a')], view: 'list' });

		expect(screen.queryByRole('columnheader', { name: 'Actions' })).toBeNull();
	});

	it('list: Play comes before the action snippet', () => {
		const action = createRawSnippet((g: () => GameListItem) => ({
			render: () => `<button type="button">Remove ${g().displayTitle}</button>`
		}));
		render(GameResults, { games: [playable('a')], view: 'list', action });

		const buttons = [
			...screen
				.getByRole('button', { name: 'Play Game a' })
				.closest('td')!
				.querySelectorAll('button')
		];
		expect(buttons.map((b) => b.getAttribute('aria-label') ?? b.textContent)).toEqual([
			'Play Game a',
			'Remove Game a'
		]);
	});

	it.each(['grid', 'list'] as const)('%s: Play is hidden in select mode', (view) => {
		render(GameResults, { games: [playable('a')], view, selected: new SvelteSet<string>() });

		expect(screen.queryByRole('button', { name: 'Play Game a' })).toBeNull();
	});

	it.each(['grid', 'list'] as const)(
		'%s: Quick Play starts an NES or PlayStation primary with its core (Play B1)',
		async (view) => {
			render(GameResults, {
				games: [
					game('n', { playRomId: 3, playRomFileName: 'Hero (USA).nes', browserCore: 'fceumm' }),
					game('p', {
						playRomId: 4,
						playRomFileName: 'Disc (USA) (Track 1).bin',
						playDisc: true,
						browserCore: 'pcsx_rearmed'
					})
				],
				view
			});

			await fireEvent.click(screen.getByRole('button', { name: 'Play Game p' }));

			await vi.waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
			expect(document.querySelector('iframe')!.getAttribute('src')).toBe(
				'/emulator/play.html?game=p&rom=4&core=pcsx_rearmed&name=Disc%20(USA)%20(Track%201).bin&disc=1'
			);
			expect(screen.getByRole('button', { name: 'Play Game n' })).not.toBeNull();
		}
	);

	it.each(['grid', 'list'] as const)(
		'%s: Quick Play starts an external DS release on DeSmuME without disc=1 (S8)',
		async (view) => {
			render(GameResults, {
				games: [
					game('d', {
						playRomId: 8,
						playRomFileName: 'Quest (USA).nds',
						playDisc: false,
						browserCore: 'desmume'
					})
				],
				view
			});

			await fireEvent.click(screen.getByRole('button', { name: 'Play Game d' }));

			await vi.waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
			expect(document.querySelector('iframe')!.getAttribute('src')).toBe(
				'/emulator/play.html?game=d&rom=8&core=desmume&name=Quest%20(USA).nds'
			);
		}
	);

	it.each(['grid', 'list'] as const)(
		'%s: Quick Play starts a 3DO disc on Opera with disc=1 (3DO B7)',
		async (view) => {
			render(GameResults, {
				games: [
					game('t', {
						playRomId: 6,
						playRomFileName: 'Opal Rally (USA).cue',
						playDisc: true,
						browserCore: 'opera'
					})
				],
				view
			});

			await fireEvent.click(screen.getByRole('button', { name: 'Play Game t' }));

			await vi.waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
			expect(document.querySelector('iframe')!.getAttribute('src')).toBe(
				'/emulator/play.html?game=t&rom=6&core=opera&name=Opal%20Rally%20(USA).cue&disc=1'
			);
		}
	);

	const psp = () =>
		game('u', {
			playRomId: 5,
			playRomFileName: 'Umd (USA).iso',
			playDisc: true,
			browserCore: 'ppsspp'
		});

	it.each([
		['grid', 'btn btn-sm btn-primary rounded-circle position-absolute bottom-0 end-0 m-1 z-2'],
		['list', 'btn btn-sm btn-outline-success']
	] as const)(
		'%s: PSP Play is a link that opens the isolated player in a new tab (PSP B3)',
		async (view, classes) => {
			render(GameResults, { games: [psp(), playable('a')], view });

			const link = screen.getByRole('link', { name: 'Play Game u (opens in a new tab)' });
			expect(link.getAttribute('href')).toBe(
				'/emulator/play-psp.html?game=u&rom=5&core=ppsspp&name=Umd%20(USA).iso&disc=1'
			);
			expect(link.getAttribute('target')).toBe('_blank');
			expect(link.getAttribute('rel')).toBe('noopener');
			expect(link.className).toBe(classes);
			expect(screen.queryByRole('button', { name: /Play Game u/ })).toBeNull();
			await fireEvent.click(link);
			expect(document.querySelector('iframe')).toBeNull();
			// Every other system keeps its modal button.
			expect(screen.getByRole('button', { name: 'Play Game a' })).not.toBeNull();
		}
	);

	it.each(['grid', 'list'] as const)(
		'%s: the PSP Play link records the play on click and middle-click (B22)',
		async (view) => {
			const posts: string[] = [];
			vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
				if (init?.method === 'POST') posts.push(url);
				return Promise.resolve(new Response(null, { status: 204 }));
			});
			try {
				render(GameResults, { games: [psp()], view });
				const link = screen.getByRole('link', { name: 'Play Game u (opens in a new tab)' });
				link.addEventListener('click', (e) => e.preventDefault());

				await fireEvent.click(link);
				await fireEvent(link, new MouseEvent('auxclick', { bubbles: true, button: 1 }));

				await vi.waitFor(() =>
					expect(posts).toEqual(['/api/games/u/roms/5/played', '/api/games/u/roms/5/played'])
				);
			} finally {
				vi.unstubAllGlobals();
			}
		}
	);

	it.each(['grid', 'list'] as const)('%s: PSP Play is hidden in select mode', (view) => {
		render(GameResults, { games: [psp()], view, selected: new SvelteSet<string>() });

		expect(screen.queryByRole('link', { name: /Play Game u/ })).toBeNull();
	});

	it.each(['grid', 'list'] as const)(
		'%s: Play opens the emulator with the game, primary release and core',
		async (view) => {
			render(GameResults, { games: [game('b'), playable('a')], view });
			expect(document.querySelector('iframe')).toBeNull();

			await fireEvent.click(screen.getByRole('button', { name: 'Play Game a' }));

			await vi.waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
			const iframe = document.querySelector('iframe')!;
			expect(iframe.getAttribute('src')).toBe(
				'/emulator/play.html?game=a&rom=7&core=gambatte&name=Game%20(World).gb'
			);
			expect(iframe.title).toBe('Emulator: Game (World).gb');
		}
	);

	it('the grid is two columns below sm, three from 360px through game-grid-xs (U6)', () => {
		const { container } = render(GameResults, { games: [game('a')], view: 'grid' });
		expect(container.querySelector('.row')!.className).toBe(
			'row row-cols-2 game-grid-xs row-cols-sm-2 row-cols-md-3 row-cols-lg-4 row-cols-xl-6 g-3'
		);
	});

	it('below sm a card has no rating row; a card with an action keeps the action (U6)', () => {
		const { container, unmount } = render(GameResults, { games: [game('a')], view: 'grid' });
		const rating = container.querySelector('.card .z-2')!;
		expect(rating.classList.contains('d-none')).toBe(true);
		expect(rating.classList.contains('d-sm-block')).toBe(true);
		expect(rating.querySelector(':scope > span')!.className).toBe('d-none d-sm-inline');
		unmount();

		const action = createRawSnippet((g: () => GameListItem) => ({
			render: () => `<button type="button">Remove ${g().displayTitle}</button>`
		}));
		const withAction = render(GameResults, { games: [game('a')], view: 'grid', action });
		const row = withAction.container.querySelector('.card .z-2')!;
		expect(row.classList.contains('d-none')).toBe(false);
		expect(row.classList.contains('d-flex')).toBe(true);
		expect(row.querySelector(':scope > span')!.className).toBe('d-none d-sm-inline');
		expect(row.querySelector('button')!.textContent).toBe('Remove Game a');
	});

	it('a narrow grid (a two-thirds pane) stops at four columns, so the one-line rating fits (B17)', () => {
		const { container } = render(GameResults, { games: [game('a')], view: 'grid', narrow: true });
		expect(container.querySelector('.row')!.className).toBe(
			'row row-cols-2 game-grid-xs row-cols-sm-2 row-cols-md-3 row-cols-xl-4 g-3'
		);
	});
});

describe('Pick up where you left off (S3, S4)', () => {
	const owned = (id: string, extra: Partial<GameListItem> = {}) =>
		game(id, {
			playRomId: 7,
			playRomFileName: 'Game (World).gb',
			browserCore: 'gambatte',
			saveCount: 3,
			ownSaveCount: 1,
			...extra
		});
	const rom = (id: number) => ({ id, fileName: `Game ${id}.gb`, externalFileName: null });
	const save = (id: string, romId: number | null, uploadedBy: string, kind = 'state') => ({
		id,
		name: id,
		description: '',
		kind,
		releaseFileName: '',
		romId,
		sizeBytes: 1,
		extension: '.state',
		uploadedBy,
		uploadedAt: '',
		updatedBy: uploadedBy,
		updatedAt: ''
	});
	let saves: unknown[];
	let fail: boolean;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		fail = false;
		saves = [];
		auth.set({ id: 2, username: 'Sam', roles: [], mustChangePassword: false });
		vi.stubGlobal('fetch', (url: string) => {
			if (fail)
				return Promise.resolve(new Response('{"status":500,"title":"Boom"}', { status: 500 }));
			const body = url.endsWith('/saves')
				? { items: saves }
				: { id: 'a', browserCore: 'gambatte', roms: [rom(7), rom(8)] };
			return Promise.resolve(new Response(JSON.stringify(body)));
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	const resumeButton = () =>
		screen.getByRole('button', { name: 'Pick up where you left off in Game a' });

	it('grid: the green button takes the Play corner; Play moves left of it', () => {
		render(GameResults, { games: [owned('a')], view: 'grid' });

		expect(resumeButton().className).toBe(
			'btn btn-sm btn-success rounded-circle position-absolute bottom-0 end-0 m-1 z-2'
		);
		expect(resumeButton().querySelector('i.bi-play-fill, i.bi')).not.toBeNull();
		expect(tooltips).toContain('Pick up where you left off');
		const play = screen.getByRole('button', { name: 'Play Game a' });
		expect(play.className).toBe(
			'btn btn-sm btn-primary rounded-circle position-absolute bottom-0 m-1 z-2'
		);
		expect(play.style.right).toBe('2.25rem');
	});

	it('list: right after Play with ms-1', () => {
		render(GameResults, { games: [owned('a')], view: 'list' });

		expect(resumeButton().className).toBe('btn btn-sm btn-success rounded-circle ms-1');
		expect(screen.getByRole('button', { name: 'Play Game a' }).nextElementSibling).toBe(
			resumeButton()
		);
	});

	it.each([
		['no own save', { ownSaveCount: 0 }],
		['not playable', { playRomId: null }],
		['a core that opens in a new tab', { browserCore: 'ppsspp' }]
	])('hidden with %s', (_, extra) => {
		render(GameResults, { games: [owned('a', extra)], view: 'grid' });
		expect(screen.queryByRole('button', { name: /Pick up where you left off/ })).toBeNull();
	});

	it('opens the most recently updated own save that plays here', async () => {
		saves = [
			save('theirs', 7, 'other'),
			save('unplayable', 99, 'sam'),
			save('mine', 8, 'SAM'),
			save('older', 7, 'sam')
		];
		render(GameResults, { games: [owned('a')], view: 'grid' });

		await fireEvent.click(resumeButton());

		await vi.waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
		expect(document.querySelector('iframe')!.getAttribute('src')).toBe(
			'/emulator/play.html?game=a&rom=8&core=gambatte&name=Game%208.gb&save=mine&saveKind=state'
		);
	});

	it('a battery save asks first', async () => {
		saves = [save('battery', 7, 'sam', 'battery')];
		render(GameResults, { games: [owned('a')], view: 'grid' });

		await fireEvent.click(resumeButton());

		await screen.findByText(
			'Loading this battery save replaces the in-browser save for this release.'
		);
		expect(document.querySelector('iframe')).toBeNull();
		await fireEvent.click(screen.getByRole('button', { name: 'Play', hidden: true }));
		await vi.waitFor(() =>
			expect(document.querySelector('iframe')!.getAttribute('src')).toContain(
				'&save=battery&saveKind=battery'
			)
		);
	});

	it('no playable own save → an info toast; a load error → a danger toast', async () => {
		saves = [save('theirs', 7, 'other')];
		render(GameResults, { games: [owned('a')], view: 'grid' });

		await fireEvent.click(resumeButton());
		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({ text: 'No save to pick up here.', kind: 'info' })
		);

		fail = true;
		await fireEvent.click(resumeButton());
		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({
				text: "Couldn't load your saves.",
				kind: 'danger'
			})
		);
		expect(document.querySelector('iframe')).toBeNull();
	});
});
