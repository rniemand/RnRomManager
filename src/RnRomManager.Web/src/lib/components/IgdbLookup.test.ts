import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { GameDetail, IgdbMatch } from '../api/types';
import type { GameEditForm } from '../gameEdit';
import IgdbLookup from './IgdbLookup.svelte';

const game = (changes: Partial<GameDetail> = {}) =>
	({
		id: 'g1',
		systemName: 'Game Boy',
		displayTitle: 'Tetris',
		igdbId: null,
		igdbConfigured: true,
		...changes
	}) as unknown as GameDetail;

const form = (changes: Partial<GameEditForm> = {}): GameEditForm => ({
	title: 'Tetris',
	releaseYear: '',
	developers: '',
	publishers: '',
	genres: '',
	description: '',
	igdbId: null,
	...changes
});

const match = (igdbId: number, changes: Partial<IgdbMatch> = {}): IgdbMatch => ({
	igdbId,
	name: `Tetris ${igdbId}`,
	releaseYear: 1989,
	developers: ['Nintendo', 'Bullet-Proof'],
	publishers: [],
	genres: [],
	description: null,
	coverUrl: `https://images.igdb.com/igdb/image/upload/t_cover_small/co${igdbId}.jpg`,
	url: null,
	...changes
});

type Reply = { status: number; body?: unknown };
let calls: { url: string; signal?: AbortSignal | null }[];
let answers: Reply[];
/** When set, requests wait for it instead of answering at once. */
let gate: Promise<void> | null;

const problem = (status: number, code: string, title: string): Reply => ({
	status,
	body: { status, code, title }
});

beforeEach(() => {
	resetClient();
	calls = [];
	answers = [];
	gate = null;
	vi.stubGlobal('fetch', async (url: string, init?: RequestInit) => {
		calls.push({ url, signal: init?.signal });
		const answer = answers.shift() ?? { status: 200, body: { matches: [] } };
		if (gate) await gate;
		if (init?.signal?.aborted) throw new DOMException('Aborted', 'AbortError');
		return new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
			status: answer.status,
			headers: { 'Content-Type': 'application/json' }
		});
	});
});
afterEach(() => vi.unstubAllGlobals());

const setup = (
	props: Partial<{ game: GameDetail; form: GameEditForm; autostart: boolean }> = {}
) => {
	const onapply = vi.fn();
	render(IgdbLookup, { props: { game: game(), form: form(), onapply, ...props } });
	return onapply;
};
const input = () => screen.getByLabelText('Search IGDB for') as HTMLInputElement;
const searchButton = () => screen.getByRole('button', { name: 'Lookup Info' });
const notConfigured =
	'Metadata lookup needs an IGDB Client ID and secret. Set Sources:IgdbClientId and Sources:IgdbClientSecret and restart.';
const alertText = () =>
	document.querySelector('.alert-info')?.textContent?.replace(/\s+/g, ' ').trim();

describe('IgdbLookup (IGDB lookup M2, M3)', () => {
	it('not configured: only the alert, and no request, even with autostart', async () => {
		setup({ game: game({ igdbConfigured: false }), autostart: true });

		await Promise.resolve();
		expect(alertText()).toBe(notConfigured);
		expect(screen.queryByLabelText('Search IGDB for')).toBeNull();
		expect(screen.queryByRole('button', { name: 'Not on IGDB' })).toBeNull();
		expect(calls).toEqual([]);
	});

	it('prefills the display title cut to 100 characters, with the help text; no request until asked', () => {
		setup({ game: game({ displayTitle: 'x'.repeat(150) }) });

		expect(input().value).toBe('x'.repeat(100));
		expect(input().getAttribute('maxlength')).toBe('100');
		expect(
			screen.getByText('Searches IGDB on Game Boy, then on every platform when that finds nothing.')
		).toBeTruthy();
		expect(searchButton().getAttribute('type')).toBe('submit');
		expect(calls).toEqual([]);
	});

	it('the button is disabled while the input is blank', async () => {
		setup();

		await fireEvent.input(input(), { target: { value: '   ' } });
		expect((searchButton() as HTMLButtonElement).disabled).toBe(true);
		await fireEvent.input(input(), { target: { value: 'Tetris' } });
		expect((searchButton() as HTMLButtonElement).disabled).toBe(false);
	});

	it('Enter searches the trimmed text; autostart searches at once', async () => {
		setup();
		await fireEvent.input(input(), { target: { value: '  Tetris DX ' } });
		await fireEvent.submit(input().form!);

		await vi.waitFor(() =>
			expect(calls.map((c) => c.url)).toEqual(['/api/games/g1/igdb-matches?q=Tetris+DX'])
		);
	});

	it('autostart searches the prefilled title', async () => {
		setup({ autostart: true });

		await vi.waitFor(() =>
			expect(calls.map((c) => c.url)).toEqual(['/api/games/g1/igdb-matches?q=Tetris'])
		);
	});

	it('shows the loading text and disables the button; a new search aborts the old one', async () => {
		let open!: () => void;
		gate = new Promise((resolve) => (open = resolve));
		answers = [
			{ status: 200, body: { matches: [match(1)] } },
			{ status: 200, body: { matches: [match(2)] } }
		];
		setup({ autostart: true });

		expect(await screen.findByText('Searching IGDB…')).toBeTruthy();
		expect(screen.getByText('Searching IGDB…').closest('[aria-live="polite"]')).toBeTruthy();
		expect((searchButton() as HTMLButtonElement).disabled).toBe(true);
		await fireEvent.submit(input().form!);
		await vi.waitFor(() => expect(calls).toHaveLength(2));
		expect(calls[0].signal!.aborted).toBe(true);
		open();

		expect(await screen.findByText('Tetris 2 (1989)')).toBeTruthy();
		expect(screen.queryByText(/Tetris 1/)).toBeNull();
	});

	it('a 409 shows the not-configured alert', async () => {
		answers = [problem(409, 'IgdbNotConfigured', 'Metadata lookup needs …')];
		setup({ autostart: true });

		await vi.waitFor(() => expect(alertText()).toBe(notConfigured));
		expect(screen.queryByLabelText('Search IGDB for')).toBeNull();
	});

	it('another error shows its title with Retry, which searches again', async () => {
		answers = [problem(502, 'IgdbFailed', 'IGDB is busy; try again in a moment.')];
		setup({ autostart: true });

		expect(await screen.findByText('IGDB is busy; try again in a moment.')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
		await vi.waitFor(() => expect(calls).toHaveLength(2));
		expect(await screen.findByText('No IGDB matches for “Tetris”.')).toBeTruthy();
	});

	it('no matches says so with the searched text', async () => {
		setup({ autostart: true });

		expect(await screen.findByText('No IGDB matches for “Tetris”.')).toBeTruthy();
	});

	it('lists matches with cover, name, year, first developer and the Linked badge; selecting presses one', async () => {
		answers = [
			{
				status: 200,
				body: {
					matches: [match(1), match(2, { releaseYear: null, developers: [], coverUrl: null })]
				}
			}
		];
		setup({ autostart: true, form: form({ igdbId: 2 }) });

		const first = await screen.findByRole('button', { name: /Tetris 1/ });
		const second = screen.getByRole('button', { name: /Tetris 2/ });
		expect(first.getAttribute('type')).toBe('button');
		expect(first.querySelector('img')!.getAttribute('src')).toBe(
			'https://images.igdb.com/igdb/image/upload/t_cover_small/co1.jpg'
		);
		expect(first.querySelector('img')!.getAttribute('alt')).toBe('');
		expect(first.textContent).toContain('Tetris 1 (1989)');
		expect(first.textContent).toContain('Nintendo');
		expect(first.textContent).not.toContain('Bullet-Proof');
		expect(first.querySelector('.badge')).toBeNull();
		expect(second.querySelector('img')).toBeNull();
		expect(second.textContent).not.toContain('(');
		expect(second.querySelector('.badge.text-bg-success')!.textContent).toBe('Linked');
		expect(first.getAttribute('aria-pressed')).toBe('false');

		await fireEvent.click(first);
		expect(first.getAttribute('aria-pressed')).toBe('true');
		expect(second.getAttribute('aria-pressed')).toBe('false');
	});

	it('Not on IGDB applies igdbId 0 to the form', async () => {
		const onapply = setup({ form: form({ igdbId: 5, title: 'Mine' }) });

		const button = screen.getByRole('button', { name: 'Not on IGDB' });
		expect(button.getAttribute('type')).toBe('button');
		await fireEvent.click(button);

		expect(onapply).toHaveBeenCalledWith(form({ igdbId: 0, title: 'Mine' }));
		expect(calls).toEqual([]);
	});
	describe('compare table and Use this match (M3)', () => {
		const full = match(9, {
			name: 'Tetris DX',
			releaseYear: 1998,
			developers: ['Nintendo'],
			publishers: ['Nintendo', 'Capcom'],
			genres: [],
			description: 'd'.repeat(250),
			url: 'https://www.igdb.com/games/tetris-dx'
		});
		const pick = async (props: Parameters<typeof setup>[0] = {}, m: IgdbMatch = full) => {
			answers = [{ status: 200, body: { matches: [m] } }];
			const onapply = setup({ autostart: true, ...props });
			await fireEvent.click(await screen.findByRole('button', { name: new RegExp(m.name) }));
			return onapply;
		};
		const box = (field: string) =>
			screen.getByRole('checkbox', { name: `Copy ${field}` }) as HTMLInputElement;
		const rows = () =>
			[...document.querySelectorAll('tbody tr')].map((tr) =>
				[...tr.querySelectorAll('th, td')].slice(1).map((c) => c.textContent!.trim())
			);

		it('no table until a match is selected', async () => {
			answers = [{ status: 200, body: { matches: [full] } }];
			setup({ autostart: true });
			await screen.findByRole('button', { name: /Tetris DX/ });

			expect(document.querySelector('table')).toBeNull();
			expect(screen.queryByRole('button', { name: 'Use this match' })).toBeNull();
		});

		it('shows Field, Current and IGDB per row, inside table-responsive, with a hidden caption', async () => {
			await pick({ form: form({ developers: 'Nintendo\n Nintendo ', description: '' }) });

			const table = document.querySelector('table')!;
			expect(table.parentElement!.className).toContain('table-responsive');
			expect(table.querySelector('caption')!.className).toContain('visually-hidden');
			expect(table.querySelector('caption')!.textContent).toBe('Fields to copy from IGDB');
			expect([...table.querySelectorAll('thead th')].map((th) => th.textContent!.trim())).toEqual([
				'Copy',
				'Field',
				'Current',
				'IGDB'
			]);
			expect(rows()).toEqual([
				['Title', 'Tetris', 'Tetris DX'],
				['Release year', '—', '1998'],
				['Developers', 'Nintendo', 'Nintendo'],
				['Publishers', '—', 'Nintendo, Capcom'],
				['Genres', '—', '—'],
				['Description', '—', `${'d'.repeat(200)}…`]
			]);
		});

		it('starting ticks and disabled rows follow defaultCopyFields', async () => {
			await pick({ form: form({ developers: 'Nintendo', description: 'Mine.' }) });

			expect([box('Title').checked, box('Title').disabled]).toEqual([false, false]);
			expect([box('Release year').checked, box('Release year').disabled]).toEqual([true, false]);
			expect(box('Developers').disabled).toBe(true);
			expect(box('Publishers').checked).toBe(true);
			expect(box('Genres').disabled).toBe(true);
			expect([box('Description').checked, box('Description').disabled]).toEqual([false, false]);
		});

		it('View on IGDB shows only when the match has a url', async () => {
			await pick();
			const link = screen.getByRole('link', { name: 'View on IGDB' });
			expect(link.getAttribute('href')).toBe('https://www.igdb.com/games/tetris-dx');
			expect(link.getAttribute('target')).toBe('_blank');
			expect(link.getAttribute('rel')).toBe('noopener noreferrer');
		});

		it('no View on IGDB without a url', async () => {
			await pick({}, { ...full, url: null });
			expect(screen.queryByRole('link', { name: 'View on IGDB' })).toBeNull();
		});

		it('Use this match copies the ticked fields and links the id', async () => {
			const onapply = await pick();
			await fireEvent.click(box('Title'));
			await fireEvent.click(box('Description'));

			const use = screen.getByRole('button', { name: 'Use this match' });
			expect(use.getAttribute('type')).toBe('button');
			await fireEvent.click(use);

			expect(onapply).toHaveBeenCalledWith(
				form({
					title: 'Tetris DX',
					releaseYear: '1998',
					developers: 'Nintendo',
					publishers: 'Nintendo\nCapcom',
					igdbId: 9
				}),
				false
			);
		});

		it('Use this match with nothing ticked only links', async () => {
			const onapply = await pick();
			for (const field of ['Release year', 'Developers', 'Publishers', 'Description'])
				if (box(field).checked) await fireEvent.click(box(field));

			await fireEvent.click(screen.getByRole('button', { name: 'Use this match' }));

			expect(onapply).toHaveBeenCalledWith(form({ igdbId: 9 }), false);
		});

		it('Use this match and save copies the ticked fields and asks to save', async () => {
			const onapply = await pick();
			await fireEvent.click(box('Title'));

			const save = screen.getByRole('button', { name: 'Use this match and save' });
			expect(save.getAttribute('type')).toBe('button');
			await fireEvent.click(save);

			expect(onapply).toHaveBeenCalledTimes(1);
			expect(onapply.mock.calls[0][1]).toBe(true);
			expect(onapply.mock.calls[0][0]).toMatchObject({ title: 'Tetris DX', igdbId: 9 });
		});
	});
});
