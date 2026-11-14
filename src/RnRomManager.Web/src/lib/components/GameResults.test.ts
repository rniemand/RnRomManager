import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { SvelteSet } from 'svelte/reactivity';
import type { GameListItem } from '../api/types';

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
		playRomId: null,
		playRomFileName: null,
		browserCore: null,
		...extra
	};
}

const playable = (id: string) =>
	game(id, { playRomId: 7, playRomFileName: 'Game (World).gb', browserCore: 'gambatte' });

describe('GameResults cards (C1, C2)', () => {
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
				expect(badges[0].className).toContain('position-absolute bottom-0 start-0 m-1');
			else expect(badges[0].closest('td')!.querySelector('a')!.textContent).toBe('Game a');
		}
	);

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
						browserCore: 'pcsx_rearmed'
					})
				],
				view
			});

			await fireEvent.click(screen.getByRole('button', { name: 'Play Game p' }));

			expect(document.querySelector('iframe')!.getAttribute('src')).toBe(
				'/emulator/play.html?game=p&rom=4&core=pcsx_rearmed&name=Disc%20(USA)%20(Track%201).bin'
			);
			expect(screen.getByRole('button', { name: 'Play Game n' })).not.toBeNull();
		}
	);

	const psp = () =>
		game('u', { playRomId: 5, playRomFileName: 'Umd (USA).iso', browserCore: 'ppsspp' });

	it.each([
		['grid', 'btn btn-sm btn-primary rounded-circle position-absolute bottom-0 end-0 m-1 z-2'],
		['list', 'btn btn-sm btn-outline-success']
	] as const)(
		'%s: PSP Play is a link that opens the isolated player in a new tab (PSP B3)',
		async (view, classes) => {
			render(GameResults, { games: [psp(), playable('a')], view });

			const link = screen.getByRole('link', { name: 'Play Game u (opens in a new tab)' });
			expect(link.getAttribute('href')).toBe(
				'/emulator/play-psp.html?game=u&rom=5&core=ppsspp&name=Umd%20(USA).iso'
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

			const iframe = document.querySelector('iframe')!;
			expect(iframe.getAttribute('src')).toBe(
				'/emulator/play.html?game=a&rom=7&core=gambatte&name=Game%20(World).gb'
			);
			expect(iframe.title).toBe('Emulator: Game (World).gb');
		}
	);
});
