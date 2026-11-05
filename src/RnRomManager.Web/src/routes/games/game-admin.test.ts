import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

const goto = vi.fn();
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({ page: { params: { id: 'g1' } } }));
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
	originalFileName: '',
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
	downloadUrl: ''
});

const game = (romIds: number[]) => ({
	id: 'g1',
	system: 'gb',
	title: 'Tetris',
	displayTitle: 'Tetris',
	releaseYear: 1989,
	developers: [],
	publishers: [],
	genres: [],
	description: null,
	hasDefaultBoxArt: false,
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
		auth.set({ id: 1, username: 'admin', role: 'Admin', mustChangePassword: false });
		calls = [];
		answers = {};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
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

	it('a User sees no admin actions', async () => {
		auth.set({ id: 2, username: 'u', role: 'User', mustChangePassword: false });
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

	it('deleting the game goes back to the Library', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Delete game/ }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete', hidden: true }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/'));
		expect(calls).toContain('DELETE /api/games/g1');
	});
});
