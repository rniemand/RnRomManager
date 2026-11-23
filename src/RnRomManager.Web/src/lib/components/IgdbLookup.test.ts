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
});
