import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import GameTypeahead from './GameTypeahead.svelte';

const item = (id: string, displayTitle: string) => ({
	id,
	system: 'gb',
	displayTitle,
	releaseYear: null,
	regions: [],
	romCount: 1,
	systemName: 'Game Boy',
	rating: null,
	favourite: false,
	boxArtUrl: null
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
				items: [item('src', 'Tetris'), item('t2', 'Tetris 2')],
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
});
