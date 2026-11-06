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
const { default: QueuePage } = await import('./+page.svelte');

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
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin', 'rom-manager'],
			mustChangePassword: false
		});
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

		await fireEvent.click(await screen.findByRole('tab', { name: 'Manual' }));
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

		await fireEvent.click(await screen.findByRole('tab', { name: 'Manual' }));
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

	const tabNames = () => screen.getAllByRole('tab').map((t) => t.textContent?.trim());
	const selected = () =>
		screen
			.getAllByRole('tab')
			.find((t) => t.getAttribute('aria-selected') === 'true')
			?.textContent?.trim();

	it('opens on Suggestions when there are some, and the tabs switch', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		render(ReviewPage);

		await screen.findByRole('button', { name: 'Use' });
		expect(tabNames()).toEqual(['Suggestions', 'Attach', 'Manual']);
		expect(selected()).toBe('Suggestions');
		expect(document.querySelector('[role="tabpanel"] table')?.className).toBe(
			'table table-sm table-striped table-hover align-middle mb-0'
		);

		await fireEvent.click(screen.getByRole('tab', { name: 'Attach' }));
		expect(selected()).toBe('Attach');
		expect(screen.getByText('Attach to a game as a hack or translation.')).toBeTruthy();
		expect(screen.queryByRole('button', { name: 'Use' })).toBeNull();
	});

	it('opens on Manual when there are no suggestions', async () => {
		answers['GET /api/review/7'] = { status: 200, body: { ...noMatch, suggestions: [] } };
		render(ReviewPage);

		expect(await screen.findByRole('button', { name: 'Add manually' })).toBeTruthy();
		expect(selected()).toBe('Manual');
	});

	it('an ambiguous item has only the Matched DAT entry tab', async () => {
		answers['GET /api/review/7'] = { status: 200, body: ambiguous };
		render(ReviewPage);

		await screen.findAllByRole('button', { name: 'Attach' });
		expect(tabNames()).toEqual(['Matched DAT entry']);
	});

	it('the header row has the system and reason badges and the header uses the standard table', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		render(ReviewPage);

		const heading = await screen.findByRole('heading', { level: 1 });
		expect(heading.getAttribute('title')).toBe('tetris hack.gb');
		expect(screen.getByText('GB').classList.contains('badge')).toBe(true);
		expect(screen.getByText('NoDatMatch').classList.contains('badge')).toBe(true);
		expect(screen.getByText('TETRIS').closest('table')?.className).toBe(
			'table table-sm table-striped table-hover align-middle mb-0'
		);
	});

	it('dismiss posts as before and goes back to the queue', async () => {
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Dismiss' }));
		const confirms = screen.getAllByRole('button', { name: 'Dismiss', hidden: true });
		await fireEvent.click(confirms[confirms.length - 1]);

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
		expect(posts).toEqual([{ url: '/api/review/7/dismiss', body: undefined }]);
	});

	it('Delete opens the delete modal and goes back to the queue after deleting', async () => {
		let deleted = false;
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		const fetchStub = globalThis.fetch;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (init?.method === 'DELETE' && url === '/api/review/7') deleted = true;
			return fetchStub(url, init);
		});
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: /^Delete$/ }));
		const confirm = await screen.findByRole('button', { name: 'Delete file', hidden: true });
		await vi.waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
		await fireEvent.click(confirm);

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
		expect(deleted).toBe(true);
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

describe('review queue page', () => {
	let calls: string[];
	let answers: Record<string, Answer>;
	let store: Map<string, string>;

	const row = (id: number) => ({
		id,
		system: 'gb',
		reason: 'NoDatMatch',
		fileName: `file${id}.gb`,
		sizeBytes: 1024,
		headerTitle: null,
		createdAt: ''
	});
	const page = (ids: number[], total: number) => ({
		items: ids.map(row),
		page: 1,
		pageSize: 20,
		total
	});

	beforeEach(() => {
		resetClient();
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		calls = [];
		answers = {};
		store = new Map();
		// Node 25's localStorage global shadows jsdom's; stub it (as in pageSize.test.ts).
		vi.stubGlobal('localStorage', {
			getItem: (k: string) => store.get(k) ?? null,
			setItem: (k: string, v: string) => void store.set(k, v)
		});
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url !== '/api/auth/antiforgery') calls.push(key);
			const answer = answers[key] ?? { status: 204 };
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

	const list = (p: number, size: number) =>
		`GET /api/review?status=Pending&page=${p}&pageSize=${size}`;

	it('uses the stored page size and remembers a new one', async () => {
		store.set('rnrm.pageSize.review', '50');
		answers[list(1, 50)] = { status: 200, body: page([1], 1) };
		answers[list(1, 10)] = { status: 200, body: page([1], 1) };
		render(QueuePage);

		await screen.findByText('file1.gb');
		expect(calls).toContain(list(1, 50));
		await fireEvent.change(screen.getByLabelText('Rows per page'), { target: { value: '10' } });

		await vi.waitFor(() => expect(calls).toContain(list(1, 10)));
		expect(store.get('rnrm.pageSize.review')).toBe('10');
	});

	it('shows rows in the standard table with a system badge', async () => {
		answers[list(1, 20)] = { status: 200, body: page([1], 1) };
		render(QueuePage);

		await screen.findByText('file1.gb');
		expect(document.querySelector('table')!.className).toBe(
			'table table-sm table-striped table-hover align-middle mb-0'
		);
		expect(screen.getByText('GB').classList.contains('badge')).toBe(true);
		expect(document.querySelector('tbody .file-size')!.textContent).toBe('1.0 KiB');
	});

	it('the delete modal lists the fields and warns about a whole archive', async () => {
		answers[list(1, 20)] = { status: 200, body: page([1], 1) };
		answers['GET /api/review/1'] = {
			status: 200,
			body: {
				...noMatch,
				id: 1,
				archivePath: 'ingest/gb/pack.zip',
				archiveEntry: 'nested/file1.gb',
				crc32: 'abcd1234'
			}
		};
		render(QueuePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete file1.gb' }));

		expect(await screen.findByText('abcd1234')).toBeTruthy();
		expect(screen.getByText('nested/file1.gb')).toBeTruthy();
		expect(screen.getByText('TETRIS')).toBeTruthy();
		expect(screen.getByRole('alert', { hidden: true }).textContent?.replace(/\s+/g, ' ')).toContain(
			'This deletes the whole archive ingest/gb/pack.zip, including every other file in it.'
		);
	});

	it('confirming calls DELETE and steps back when the page is now empty', async () => {
		store.set('rnrm.pageSize.review', '10');
		answers[list(1, 10)] = { status: 200, body: page([1], 11) };
		answers[list(2, 10)] = { status: 200, body: page([11], 11) };
		answers['GET /api/review/11'] = { status: 200, body: { ...noMatch, id: 11 } };
		render(QueuePage);

		await screen.findByText('file1.gb');
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
		await fireEvent.click(await screen.findByRole('button', { name: 'Delete file11.gb' }));
		const confirm = await screen.findByRole('button', { name: 'Delete file', hidden: true });
		await vi.waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(false));
		await fireEvent.click(confirm);

		await vi.waitFor(() => expect(calls).toContain('DELETE /api/review/11'));
		await vi.waitFor(() => expect(calls.filter((c) => c === list(1, 10))).toHaveLength(2));
	});
});
