import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

const goto = vi.fn((...args: [string, unknown?]) => navigateTo(args[0]));
vi.mock('$app/navigation', () => ({ goto: (...args: [string, unknown?]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: 'g1' },
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

// Bootstrap's Modal needs layout/transitions jsdom lacks; the modal markup is always in the DOM anyway.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: GamePage } = await import('./[id]/+page.svelte');

const rom = (id: number) => ({
	id,
	fileName: `Tetris ${id}.gb`,
	originalFileName: `Tetris ${id}.gb`,
	baseTitle: 'Tetris',
	regions: [],
	languages: [],
	revision: null,
	flags: [],
	isPrimary: id === 1,
	identificationMethod: 'Dat',
	datName: null,
	datVersion: null,
	datGameName: null,
	header: null,
	sizeBytes: 1,
	crc32: '0',
	md5: '0',
	sha1: '0',
	dateAdded: '',
	dateModified: '',
	downloadUrl: '',
	hasNote: false,
	externalFileName: null as string | null,
	version: null as string | null
});

const game = (romIds: number[]) => ({
	id: 'g1',
	system: 'gb',
	systemName: 'Game Boy',
	systemShortName: 'GB',
	title: 'Tetris',
	displayTitle: 'Tetris',
	releaseYear: 1989,
	developers: [],
	publishers: [],
	genres: [],
	description: null,
	hasDefaultBoxArt: false,
	rating: null,
	favourite: false,
	metadata: { createdAt: '', updatedAt: '' },
	roms: romIds.map(rom),
	images: []
});

type Answer = { status: number; body?: unknown };

describe('game detail admin actions', () => {
	let calls: string[];
	let answers: Record<string, Answer[]>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		navigateTo('/games/g1');
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin', 'rom-manager'],
			mustChangePassword: false
		});
		calls = [];
		answers = {};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			// B16: the game's collections (the badges); answered here so the queued answers stay as they were.
			if (url.endsWith('/collections')) return Promise.resolve(new Response('[]', { status: 200 }));
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			const answer = answers[key]?.shift() ?? { status: 204 };
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

	const modal = () => within(document.querySelector<HTMLElement>('.modal')!);

	it('a rom-manager sees the actions', async () => {
		auth.set({ id: 3, username: 'm', roles: ['rom-manager'], mustChangePassword: false });
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		expect(await screen.findByRole('button', { name: /Edit metadata/ })).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Delete Tetris 1.gb' })).toBeTruthy();
	});

	it('an admin without rom-manager sees no actions', async () => {
		auth.set({ id: 4, username: 'a', roles: ['admin'], mustChangePassword: false });
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await screen.findByText('Releases');
		expect(screen.queryByRole('button', { name: /Edit metadata/ })).toBeNull();
		expect(screen.queryByRole('button', { name: 'Delete Tetris 1.gb' })).toBeNull();
	});

	it('a User sees no admin actions', async () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await screen.findByText('Releases');
		expect(screen.queryByRole('button', { name: /Edit metadata/ })).toBeNull();
		expect(screen.queryByRole('button', { name: 'Delete Tetris 1.gb' })).toBeNull();
	});

	it('deleting a release calls the API and reloads', async () => {
		answers['GET /api/games/g1'] = [
			{ status: 200, body: game([1, 2]) },
			{ status: 200, body: game([1]) }
		];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Tetris 2.gb' }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete', hidden: true }));

		await vi.waitFor(() => expect(calls).toContain('DELETE /api/games/g1/roms/2'));
		await vi.waitFor(() => expect(calls.filter((c) => c === 'GET /api/games/g1')).toHaveLength(2));
	});

	it('the release delete confirmation mentions the note only when the release has one (B12)', async () => {
		const detail = game([1, 2]);
		detail.roms[1].hasNote = true;
		answers['GET /api/games/g1'] = [{ status: 200, body: detail }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Tetris 2.gb' }));
		expect(modal().getByText(/Delete Tetris 2\.gb and its note\?/)).toBeTruthy();

		await fireEvent.click(modal().getByRole('button', { name: 'Cancel', hidden: true }));
		await fireEvent.click(screen.getByRole('button', { name: 'Delete Tetris 1.gb' }));
		await vi.waitFor(() => expect(modal().getByText(/Delete Tetris 1\.gb\?/)).toBeTruthy());
		expect(modal().queryByText(/and its note/)).toBeNull();
	});

	it('the game delete confirmation mentions notes when any release has one (B12)', async () => {
		const withNote = game([1, 2]);
		withNote.roms[1].hasNote = true;
		answers['GET /api/games/g1'] = [{ status: 200, body: withNote }];
		const { unmount } = render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Delete game/ }));
		expect(modal().getByText(/Delete Tetris and its notes\?/)).toBeTruthy();
		unmount();

		answers['GET /api/games/g1'] = [{ status: 200, body: game([1, 2]) }];
		render(GamePage);
		await fireEvent.click(await screen.findByRole('button', { name: /Delete game/ }));
		expect(modal().getByText(/Delete Tetris\?/)).toBeTruthy();
		expect(modal().queryByText(/and its notes/)).toBeNull();
	});

	it('a stale id (404) reloads and asks for a retry', async () => {
		answers['GET /api/games/g1'] = [
			{ status: 200, body: game([1, 2]) },
			{ status: 200, body: game([1, 3]) }
		];
		answers['DELETE /api/games/g1/roms/2'] = [{ status: 404, body: { code: 'NotFound' } }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Tetris 2.gb' }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete', hidden: true }));

		expect(await screen.findByText('This game changed, please retry.')).toBeTruthy();
		expect(await screen.findByRole('button', { name: 'Delete Tetris 3.gb' })).toBeTruthy();
	});

	it('edit metadata validates before sending', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Edit metadata/ }));
		await fireEvent.input(modal().getByLabelText('Release year'), { target: { value: '1800' } });
		await fireEvent.click(modal().getByRole('button', { name: 'Edit metadata', hidden: true }));

		expect(modal().getByText('Leave empty or enter a year 1970–2100.')).toBeTruthy();
		expect(calls.some((c) => c.startsWith('PATCH'))).toBe(false);
	});

	it('edit metadata is a wide modal with a grid layout; fields still bound and sent', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		answers['PATCH /api/games/g1'] = [{ status: 200, body: { ...game([1]), title: 'Tetris DX' } }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Edit metadata/ }));

		expect(document.querySelector('.modal-dialog')!.classList.contains('modal-lg')).toBe(true);
		const title = modal().getByLabelText('Title');
		expect(title.closest('.col-md-9')).toBeTruthy();
		expect(modal().getByLabelText('Release year').closest('.col-md-3')).toBeTruthy();
		for (const label of ['Developers', 'Publishers', 'Genres']) {
			const field = modal().getByLabelText(label);
			expect(field.closest('.col-md-4')).toBeTruthy();
			expect(field.getAttribute('rows')).toBe('3');
			expect(field.getAttribute('aria-describedby')).toBe('edit-lists-help');
		}
		await fireEvent.input(title, { target: { value: 'Tetris DX' } });
		await fireEvent.click(modal().getByRole('button', { name: 'Edit metadata', hidden: true }));

		await vi.waitFor(() => expect(calls).toContain('PATCH /api/games/g1'));
	});

	it('the admin modal opens from the Images tab, outside the hidden Releases panel (GP1)', async () => {
		navigateTo('/games/g1?tab=images');
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Delete game/ }));

		const first = document.querySelector<HTMLElement>('.modal')!;
		expect(first.querySelector('.modal-title')!.textContent).toMatch(/Delete/);
		expect(first.closest('[hidden]')).toBeNull();
		expect(document.getElementById('releases-panel')!.hidden).toBe(true);
	});

	it('other admin modals keep the default width', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Delete game/ }));

		expect(document.querySelector('.modal-dialog')!.classList.contains('modal-lg')).toBe(false);
	});

	it('deleting the game goes back to the Library', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Delete game/ }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete', hidden: true }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/'));
		expect(calls).toContain('DELETE /api/games/g1');
	});

	describe('merge suggestions (GP5)', () => {
		const listItem = (id: string, displayTitle: string) => ({
			id,
			system: 'gb',
			systemName: 'Game Boy',
			systemShortName: 'GB',
			displayTitle,
			releaseYear: null,
			regions: [],
			romCount: 1,
			primarySizeBytes: 1,
			boxArtUrl: null,
			rating: null,
			favourite: false,
			matchedOn: null
		});
		const pageOf = (...items: ReturnType<typeof listItem>[]) => ({
			status: 200,
			body: { items, page: 1, pageSize: 6, total: items.length }
		});
		const suggest = (q: string) =>
			`GET /api/games?${new URLSearchParams({ system: 'gb', q, sort: 'relevance', pageSize: '6' })}`;

		async function openMerge(displayTitle: string): Promise<void> {
			answers['GET /api/games/g1'] = [{ status: 200, body: { ...game([1]), displayTitle } }];
			render(GamePage);
			await fireEvent.click(await screen.findByRole('button', { name: /Merge into/ }));
		}

		const suggestionButtons = () =>
			[...document.querySelectorAll('[aria-labelledby="merge-suggested"] button')].map((b) =>
				b.textContent?.trim()
			);
		const input = () => document.getElementById('merge-target') as HTMLInputElement;

		it('requests the title without tags by relevance, excludes the current game and shows at most 5', async () => {
			answers[suggest('Tetris DX')] = [
				pageOf(
					listItem('g1', 'Tetris DX'),
					...['a', 'b', 'c', 'd', 'e'].map((id) => listItem(id, `Tetris ${id}`))
				)
			];
			await openMerge('Tetris DX (USA) [!]');

			await vi.waitFor(() =>
				expect(suggestionButtons()).toEqual([
					'Tetris a',
					'Tetris b',
					'Tetris c',
					'Tetris d',
					'Tetris e'
				])
			);
			expect(calls).toContain(suggest('Tetris DX'));
		});

		it('cuts a long title to its first 8 words', async () => {
			await openMerge('one two three four five six seven eight nine ten');

			await vi.waitFor(() =>
				expect(calls).toContain(suggest('one two three four five six seven eight'))
			);
		});

		it('clicking one fills the typeahead and the merge posts it; typing again clears it', async () => {
			answers[suggest('Tetris')] = [pageOf(listItem('g2', 'Tetris DX'))];
			answers['POST /api/games/g1/merge'] = [{ status: 200, body: { targetGameId: 'g2' } }];
			await openMerge('Tetris');

			const button = await modal().findByRole('button', { name: 'Tetris DX', hidden: true });
			await fireEvent.click(button);
			expect(input().value).toBe('Tetris DX');
			expect(button.getAttribute('aria-pressed')).toBe('true');
			expect(document.getElementById('merge-target-help')!.textContent).toContain(
				'Selected: Tetris DX'
			);

			await fireEvent.input(input(), { target: { value: 'Tetris D' } });
			expect(document.getElementById('merge-target-help')!.textContent).not.toContain('Selected');

			await fireEvent.click(button);
			await fireEvent.click(modal().getByRole('button', { name: 'Merge into', hidden: true }));
			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/games/g2'));
			expect(calls).toContain('POST /api/games/g1/merge');
		});

		it('retries once with the first word of 3+ characters', async () => {
			answers[suggest('Orbit Blocks')] = [pageOf(listItem('g1', 'Orbit Blocks'))];
			answers[suggest('Orbit')] = [pageOf(listItem('g3', 'Orbit Kart'))];
			await openMerge('Orbit Blocks');

			await vi.waitFor(() => expect(suggestionButtons()).toEqual(['Orbit Kart']));
		});

		it('a title that is all tags requests nothing; no matches shows no list', async () => {
			await openMerge('(Beta) [b]');
			await new Promise((r) => setTimeout(r, 20));

			expect(calls.filter((c) => c.startsWith('GET /api/games?'))).toEqual([]);
			expect(document.getElementById('merge-suggested')).toBeNull();
		});

		it('a failed suggestion fetch shows no list and the typeahead still works', async () => {
			answers[suggest('Tetris')] = [{ status: 500, body: { code: 'IoError', title: 'x' } }];
			await openMerge('Tetris');
			await vi.waitFor(() => expect(calls).toContain(suggest('Tetris')));

			expect(document.getElementById('merge-suggested')).toBeNull();
			await fireEvent.input(input(), { target: { value: 'Tet' } });
			await vi.waitFor(
				() => expect(calls.some((c) => c.startsWith('GET /api/games?q=Tet'))).toBe(true),
				{ timeout: 1000 }
			);
		});
	});
});
