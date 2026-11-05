import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

const goto = vi.fn();
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({ page: { params: { id: '7' } } }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: ReviewPage } = await import('./[id]/+page.svelte');

const base = {
	id: 7,
	system: 'gb',
	sourcePath: 'ingest/gb/tetris hack.gb',
	archivePath: null,
	archiveEntry: null,
	originalFileName: 'tetris hack.gb',
	sizeBytes: 32768,
	crc32: '1',
	md5: '2',
	sha1: '3',
	header: { title: 'TETRIS' },
	status: 'Pending',
	gameId: null,
	createdAt: ''
};
const noMatch = {
	...base,
	reason: 'NoDatMatch',
	suggestions: [
		{ datGameName: 'Tetris (World)', romName: 'Tetris (World) (Rev 1).gb', score: 0.92 }
	],
	datIdentification: null,
	candidateGames: []
};
const ambiguous = {
	...base,
	reason: 'AmbiguousGroup',
	suggestions: [],
	datIdentification: {
		datName: 'Nintendo - Game Boy',
		datVersion: '1',
		datGameName: 'Tetris (World)',
		romName: 'Tetris (World).gb'
	},
	candidateGames: [
		{ id: 'g1', displayTitle: 'Tetris' },
		{ id: 'g2', displayTitle: 'Tetris DX' }
	]
};

type Answer = { status: number; body?: unknown };

describe('review detail page', () => {
	let posts: { url: string; body: unknown }[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		auth.set({ id: 1, username: 'admin', role: 'Admin', mustChangePassword: false });
		posts = [];
		answers = {};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (init?.method === 'POST' && url !== '/api/auth/antiforgery')
				posts.push({ url, body: init.body ? JSON.parse(init.body as string) : undefined });
			const answer = answers[`${init?.method ?? 'GET'} ${url}`] ?? { status: 204 };
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

	it('picking a suggestion resolves as Dat and opens the game', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		answers['POST /api/review/7/resolve'] = { status: 200, body: { gameId: 'g9' } };
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Use' }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/games/g9'));
		expect(posts).toEqual([
			{
				url: '/api/review/7/resolve',
				body: { action: 'Dat', datSystem: 'gb', datGameName: 'Tetris (World)' }
			}
		]);
	});

	it('the manual form validates before sending', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Add manually' }));

		expect(screen.getByText('Use 1–255 characters.')).toBeTruthy();
		expect(posts).toEqual([]);
	});

	it('a manual entry with several candidate games explains AmbiguousGroup', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		answers['POST /api/review/7/resolve'] = {
			status: 409,
			body: { code: 'AmbiguousGroup', title: 'AmbiguousGroup' }
		};
		render(ReviewPage);

		await fireEvent.input(await screen.findByLabelText('Title'), { target: { value: 'Tetris' } });
		await fireEvent.input(screen.getByLabelText('Regions'), { target: { value: 'USA, Europe' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Add manually' }));

		expect(await screen.findByText(/Attach it to one of them instead/)).toBeTruthy();
		expect(posts[0].body).toMatchObject({
			action: 'Manual',
			title: 'Tetris',
			regions: ['USA', 'Europe']
		});
	});

	it('AmbiguousGroup attaches to a candidate with flag None', async () => {
		answers['GET /api/review/7'] = { status: 200, body: ambiguous };
		answers['POST /api/review/7/resolve'] = { status: 200, body: { gameId: 'g2' } };
		render(ReviewPage);

		const buttons = await screen.findAllByRole('button', { name: 'Attach' });
		await fireEvent.click(buttons[1]);

		await vi.waitFor(() =>
			expect(posts[0].body).toEqual({ action: 'Attach', targetGameId: 'g2', flag: 'None' })
		);
	});

	it('SourceMissing goes back to the queue', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		answers['POST /api/review/7/resolve'] = {
			status: 409,
			body: { code: 'SourceMissing', title: 'SourceMissing' }
		};
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Use' }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
	});
});
