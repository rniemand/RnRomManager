import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import GameTypeahead from './GameTypeahead.svelte';
import { iconClass } from './icons';

const item = (id: string, displayTitle: string, boxArtUrl: string | null = null) => ({
	id,
	system: 'gb',
	displayTitle,
	releaseYear: null,
	regions: [],
	romCount: 1,
	systemName: 'Game Boy',
	systemShortName: 'GB',
	rating: null,
	favourite: false,
	boxArtUrl
});

describe('GameTypeahead', () => {
	let requested: string[];

	beforeEach(() => {
		vi.useFakeTimers();
		resetClient();
		requested = [];
		vi.stubGlobal('fetch', (input: string) => {
			requested.push(input);
			const body = {
				items: [item('src', 'Tetris'), item('t2', 'Tetris 2', '/api/games/t2/images/1')],
				page: 1,
				pageSize: 10,
				total: 2
			};
			return Promise.resolve(new Response(JSON.stringify(body), { status: 200 }));
		});
	});
	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
	});

	const props = { id: 't', label: 'Target game', system: 'gb', excludeId: 'src', selected: null };
	const type = (value: string) =>
		fireEvent.input(screen.getByLabelText('Target game'), { target: { value } });

	it('waits 300 ms after the last keystroke, then searches the same system', async () => {
		render(GameTypeahead, props);

		await type('te');
		await vi.advanceTimersByTimeAsync(200);
		await type('tet');
		await vi.advanceTimersByTimeAsync(299);
		expect(requested).toEqual([]);

		await vi.advanceTimersByTimeAsync(1);
		expect(requested).toEqual(['/api/games?q=tet&system=gb&pageSize=10']);
	});

	it('needs 2 characters', async () => {
		render(GameTypeahead, props);

		await type(' t ');
		await vi.advanceTimersByTimeAsync(500);

		expect(requested).toEqual([]);
	});

	it('leaves out the source game and selects a result', async () => {
		render(GameTypeahead, props);

		await type('tetris');
		await vi.advanceTimersByTimeAsync(300);

		expect(screen.queryByRole('button', { name: 'Tetris' })).toBeNull();
		await fireEvent.click(await screen.findByRole('button', { name: 'Tetris 2' }));
		expect(screen.getByText('Selected: Tetris 2')).toBeTruthy();
	});

	it('without a system searches every system and shows each short system name (B24, P4)', async () => {
		render(GameTypeahead, { id: 't', label: 'Target game', selected: null });

		await type('tetris');
		await vi.advanceTimersByTimeAsync(300);

		expect(requested).toEqual(['/api/games?q=tetris&pageSize=10']);
		const badge = (await screen.findByRole('button', { name: /Tetris 2/ })).querySelector('.badge');
		expect(badge?.textContent?.trim()).toBe('GB');
		expect(badge?.getAttribute('title')).toBe('Game Boy');
	});

	it('with excludeCollection asks for non-members only (P2)', async () => {
		render(GameTypeahead, { ...props, excludeCollection: 'c1' });

		await type('tetris');
		await vi.advanceTimersByTimeAsync(300);

		expect(requested).toEqual(['/api/games?q=tetris&system=gb&notInCollection=c1&pageSize=10']);
	});

	it('shows the cover, or the system icon without one (P3)', async () => {
		render(GameTypeahead, { id: 't', label: 'Target game', selected: null });

		await type('tetris');
		await vi.advanceTimersByTimeAsync(300);

		const withCover = await screen.findByRole('button', { name: /Tetris 2/ });
		const img = withCover.querySelector('img');
		expect(img?.getAttribute('src')).toBe('/api/games/t2/images/1');
		expect(img?.getAttribute('alt')).toBe('');
		const without = screen.getByRole('button', { name: /^GB Tetris$/ });
		expect(without.querySelector('img')).toBeNull();
		expect(without.firstElementChild?.querySelector('i')?.className.trim()).toBe(
			iconClass('system-gb')
		);
	});

	it('without overlay the results and status stay inline (C2)', async () => {
		render(GameTypeahead, props);

		await type('tetris');
		await vi.advanceTimersByTimeAsync(300);

		const list = (await screen.findByRole('button', { name: 'Tetris 2' })).parentElement!;
		expect(list.className).toBe('list-group mt-1');
		expect(document.getElementById('t-help')!.className).toBe('form-text');
		expect(document.querySelector('.position-relative')).toBeNull();
	});

	describe('overlay (C2)', () => {
		const panel = () => document.querySelector<HTMLElement>('.position-absolute');
		const box = () => screen.getByLabelText('Target game') as HTMLInputElement;
		const show = async (value = 'tetris') => {
			render(GameTypeahead, { ...props, overlay: true });
			await type(value);
			await vi.advanceTimersByTimeAsync(300);
			await screen.findByRole('button', { name: 'Tetris 2' });
		};

		it('floats the results in a panel below the input, the help visually hidden', async () => {
			await show();

			expect(panel()!.className).toBe(
				'list-group position-absolute start-0 end-0 mt-1 shadow z-3 overflow-y-auto'
			);
			expect(panel()!.getAttribute('style')).toBe('max-height: 50vh');
			expect(panel()!.parentElement!.className).toBe('position-relative');
			expect(document.getElementById('t-help')!.className).toBe('visually-hidden');
			expect(box().getAttribute('aria-describedby')).toBe('t-help');
		});

		it('the status is the panel first row', async () => {
			vi.stubGlobal('fetch', () => new Promise(() => {}));
			render(GameTypeahead, { ...props, overlay: true });
			await type('zz');

			const row = panel()!.firstElementChild!;
			expect(row.tagName).toBe('DIV');
			expect(row.className).toBe('list-group-item small text-body-secondary');
			expect(row.textContent).toBe('Searching…');
			expect(document.getElementById('t-help')!.textContent!.trim()).toBe('Searching…');
		});

		it('Escape closes it, keeping the query, and typing reopens it', async () => {
			await show();

			const event = new KeyboardEvent('keydown', {
				key: 'Escape',
				cancelable: true,
				bubbles: true
			});
			box().dispatchEvent(event);
			await vi.advanceTimersByTimeAsync(0);
			expect(event.defaultPrevented).toBe(true);
			expect(panel()).toBeNull();
			expect(box().value).toBe('tetris');
			expect(document.getElementById('t-help')!.textContent!.trim()).toBe('');

			await type('tetri');
			await vi.advanceTimersByTimeAsync(300);
			expect(await screen.findByRole('button', { name: 'Tetris 2' })).toBeTruthy();
		});

		it('Escape while searching stays closed when the response arrives', async () => {
			let release!: (r: Response) => void;
			vi.stubGlobal('fetch', () => new Promise<Response>((done) => (release = done)));
			render(GameTypeahead, { ...props, overlay: true });
			await type('tetris');
			await vi.advanceTimersByTimeAsync(300);
			expect(panel()).not.toBeNull();

			await fireEvent.keyDown(box(), { key: 'Escape' });
			release(
				new Response(
					JSON.stringify({ items: [item('t2', 'Tetris 2')], page: 1, pageSize: 10, total: 1 })
				)
			);
			await vi.advanceTimersByTimeAsync(10);
			expect(panel()).toBeNull();
		});

		it('a pointerdown outside closes it; inside keeps it', async () => {
			await show();
			const outside = document.body.appendChild(document.createElement('div'));

			await fireEvent.pointerDown(screen.getByRole('button', { name: 'Tetris 2' }));
			expect(panel()).not.toBeNull();
			await fireEvent.pointerDown(outside);
			expect(panel()).toBeNull();
			outside.remove();
		});

		it('focus moving outside closes it; a null relatedTarget keeps it open', async () => {
			await show();
			const outside = document.body.appendChild(document.createElement('button'));

			await fireEvent.focusOut(box(), { relatedTarget: null });
			expect(panel()).not.toBeNull();
			await fireEvent.focusOut(box(), {
				relatedTarget: screen.getByRole('button', { name: 'Tetris 2' })
			});
			expect(panel()).not.toBeNull();
			await fireEvent.focusOut(box(), { relatedTarget: outside });
			expect(panel()).toBeNull();
			outside.remove();
		});

		it('selecting a result closes it', async () => {
			await show();
			await fireEvent.click(screen.getByRole('button', { name: 'Tetris 2' }));
			expect(panel()).toBeNull();
			expect(document.getElementById('t-help')!.textContent!.trim()).toBe('Selected: Tetris 2');
		});
	});

	it('with a system shows no badge', async () => {
		render(GameTypeahead, props);

		await type('tetris');
		await vi.advanceTimersByTimeAsync(300);

		expect(
			(await screen.findByRole('button', { name: 'Tetris 2' })).querySelector('.badge')
		).toBeNull();
	});
});
