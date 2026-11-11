import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

const goto = vi.fn();
vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: '7' },
		get url() {
			return testUrl.current;
		}
	}
}));
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
// use:tooltip builds a Bootstrap Tooltip the Modal-only mock above lacks; record the text instead.
const tooltips: { node: Element; text: string }[] = [];
vi.mock('#lib/tooltip.js', () => ({
	tooltip: (node: Element, text: string) => {
		tooltips.push({ node, text });
	}
}));
const shiftTip = 'Delete (Shift+click to skip confirmation)';

const { default: ReviewPage } = await import('./[id]/+page.svelte');
const { default: QueuePage } = await import('./+page.svelte');

const base = {
	id: 7,
	system: 'gb',
	systemName: 'Game Boy',
	systemShortName: 'GB',
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

	// G8: Switch is identified through titledb by title id.
	const switchItem = {
		...noMatch,
		system: 'switch',
		systemName: 'Nintendo Switch',
		systemShortName: 'Switch',
		originalFileName: 'orbit.nsp',
		suggestions: [
			{
				datGameName: 'Orbit Expedition [0100000000002000]',
				romName: 'Orbit Expedition [0100000000002000][v0].nsp',
				score: 0.9
			}
		]
	};

	async function searchFor(item: unknown, results: unknown[]): Promise<void> {
		answers['GET /api/review/7'] = { status: 200, body: item };
		const system = (item as { system: string }).system;
		answers[`GET /api/dat/search?system=${system}&q=orbit&limit=20`] = {
			status: 200,
			body: results
		};
		render(ReviewPage);
		await fireEvent.input(await screen.findByLabelText(/Search the .* DAT/), {
			target: { value: 'orbit' }
		});
		await fireEvent.click(screen.getByRole('button', { name: 'Search' }));
	}

	it('a Switch DAT search shows no size for a zero-size titledb result, and a size when there is one (G8)', async () => {
		await searchFor(switchItem, [
			{
				gameName: 'Orbit Expedition [0100000000002000]',
				romName: 'Orbit A.nsp',
				sizeBytes: 0,
				crc32: ''
			},
			{
				gameName: 'Orbit Sized [0100000000004000]',
				romName: 'Orbit B.nsp',
				sizeBytes: 2048,
				crc32: ''
			}
		]);

		const zero = (await screen.findByText('Orbit A.nsp')).closest('li')!;
		const sized = screen.getByText('Orbit B.nsp').closest('li')!;
		expect(zero.textContent).not.toMatch(/\bB\b/);
		expect(sized.textContent).toMatch(/2(\.0)? KiB|2 KB|2,048/);
	});

	it('a libretro zero-size result keeps its size display (G8)', async () => {
		await searchFor(noMatch, [
			{ gameName: 'Orbit (World)', romName: 'Orbit (World).gb', sizeBytes: 0, crc32: 'abcd' }
		]);

		expect((await screen.findByText('Orbit (World).gb')).closest('li')!.textContent).toMatch(/0 B/);
	});

	it('a Switch item explains title-ID selection and toasts a plain added release (G8)', async () => {
		toasts.items = [];
		answers['GET /api/review/7'] = { status: 200, body: switchItem };
		answers['POST /api/review/7/resolve'] = { status: 200, body: { gameId: 'g9' } };
		render(ReviewPage);

		expect(
			await screen.findByText('Choose a titledb result to identify this release by title ID.')
		).toBeTruthy();
		expect(screen.queryByText(/unverified release, because its hashes differ/)).toBeNull();
		await fireEvent.click(screen.getByRole('button', { name: 'Use' }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/games/g9'));
		expect(toasts.items.at(-1)?.text).toBe('Added release of Orbit Expedition [0100000000002000].');
	});

	it('a libretro item keeps the unverified explanation and toast (G8)', async () => {
		toasts.items = [];
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		answers['POST /api/review/7/resolve'] = { status: 200, body: { gameId: 'g9' } };
		render(ReviewPage);

		expect(await screen.findByText(/unverified release, because its hashes differ/)).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Use' }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/games/g9'));
		expect(toasts.items.at(-1)?.text).toBe('Added as an unverified release of Tetris (World).');
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
		const details = screen.getByRole('table', { name: /^Details of/, hidden: true });
		expect(details.textContent).not.toContain('Archive');
		await fireEvent.click(confirm);

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
		expect(deleted).toBe(true);
	});

	it('Shift+click on Delete deletes a loose file without the modal and goes back (RV1)', async () => {
		toasts.items = [];
		let deletes = 0;
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		const fetchStub = globalThis.fetch;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (init?.method === 'DELETE' && url === '/api/review/7') deletes++;
			return fetchStub(url, init);
		});
		render(ReviewPage);

		const button = await screen.findByRole('button', { name: /^Delete$/ });
		expect(tooltips.some((t) => t.node === button && t.text === shiftTip)).toBe(true);
		await fireEvent.click(button, { shiftKey: true });

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
		expect(deletes).toBe(1);
		expect(toasts.items.at(-1)?.text).toBe('Deleted tetris hack.gb.');
		expect(screen.queryByRole('heading', { name: /^Delete File:/, hidden: true })).toBeNull();
	});

	it('Shift+click on an archive item still opens the modal (RV1)', async () => {
		const archived = { ...noMatch, archivePath: 'ingest/gb/pack.zip', archiveEntry: 'a.gb' };
		answers['GET /api/review/7'] = { status: 200, body: archived };
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: /^Delete$/ }), {
			shiftKey: true
		});

		expect(
			await screen.findByRole('heading', { name: 'Delete File: tetris hack.gb', hidden: true })
		).toBeTruthy();
		expect(goto).not.toHaveBeenCalled();
	});

	it('a failed Shift+click delete toasts and stays (RV1)', async () => {
		toasts.items = [];
		answers['GET /api/review/7'] = { status: 200, body: noMatch };
		answers['DELETE /api/review/7'] = { status: 500, body: { code: 'IoError', title: 'IoError' } };
		render(ReviewPage);

		await fireEvent.click(await screen.findByRole('button', { name: /^Delete$/ }), {
			shiftKey: true
		});

		await vi.waitFor(() => expect(toasts.items.at(-1)).toMatchObject({ kind: 'danger' }));
		expect(goto).not.toHaveBeenCalled();
	});

	it.each([
		['NoDatMatch', noMatch],
		['AmbiguousGroup', ambiguous]
	])('the header has a Google it link for a %s item (RV2)', async (_reason, body) => {
		answers['GET /api/review/7'] = { status: 200, body };
		render(ReviewPage);

		const link = await screen.findByRole('link', { name: /Google it/ });
		expect(link.getAttribute('href')).toBe(
			'https://www.google.com/search?q=%22TETRIS%22%20Game%20Boy%20rom'
		);
		expect(link.getAttribute('target')).toBe('_blank');
		expect(link.getAttribute('rel')).toBe('noopener noreferrer');
		expect(link.querySelector('.bi-google')).not.toBeNull();
		// B3: same size as Dismiss and Delete.
		expect(link.className).toBe('btn btn-outline-secondary');
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
		createdAt: '',
		isArchive: id >= 100
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
		answers = {
			'GET /api/systems': {
				status: 200,
				body: [
					{ id: 'gb', displayName: 'Game Boy', gameCount: 0, romCount: 0, regions: [] },
					{ id: 'gbc', displayName: 'Game Boy Color', gameCount: 0, romCount: 0, regions: [] }
				]
			}
		};
		store = new Map();
		navigateTo('/review');
		goto.mockReset();
		goto.mockImplementation((path: string) => navigateTo(path));
		// Node 25's localStorage global shadows jsdom's; stub it (as in pageSize.test.ts).
		vi.stubGlobal('localStorage', {
			getItem: (k: string) => store.get(k) ?? null,
			setItem: (k: string, v: string) => void store.set(k, v),
			removeItem: (k: string) => void store.delete(k)
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
		goto.mockReset();
	});

	const list = (p: number, size: number) =>
		`GET /api/review?status=Pending&page=${p}&pageSize=${size}`;
	const urlOptions = { replace: true, reset: false };
	const filterKey = 'rnrm.review.filter';

	it('the search, System and Reason controls write the URL and send the query (RV4)', async () => {
		answers[list(1, 20)] = { status: 200, body: page([1], 1) };
		render(QueuePage);
		await screen.findByText('file1.gb');
		await screen.findByRole('option', { name: 'Game Boy Color' });

		await fireEvent.input(screen.getByPlaceholderText('Search file name or title'), {
			target: { value: ' tetris ' }
		});
		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review?q=tetris', urlOptions));
		await fireEvent.change(screen.getByLabelText('System'), { target: { value: 'gbc' } });
		await fireEvent.change(screen.getByLabelText('Reason'), {
			target: { value: 'AmbiguousGroup' }
		});

		expect(goto).toHaveBeenLastCalledWith(
			'/review?q=tetris&system=gbc&reason=AmbiguousGroup',
			urlOptions
		);
		await vi.waitFor(() =>
			expect(calls).toContain(`${list(1, 20)}&q=tetris&system=gbc&reason=AmbiguousGroup`)
		);
		expect(store.get(filterKey)).toBe('?q=tetris&system=gbc&reason=AmbiguousGroup');
	});

	it('paging writes page to the URL and storage; a filter change resets to page 1 (RV4)', async () => {
		answers[list(1, 20)] = { status: 200, body: page([1], 40) };
		answers[list(2, 20)] = { status: 200, body: page([21], 40) };
		render(QueuePage);
		await screen.findByText('file1.gb');

		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));

		expect(goto).toHaveBeenLastCalledWith('/review?page=2', urlOptions);
		await screen.findByText('file21.gb');
		expect(store.get(filterKey)).toBe('?page=2');

		await fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'NoDatMatch' } });
		expect(goto).toHaveBeenLastCalledWith('/review?reason=NoDatMatch', urlOptions);
	});

	it('URL params win over storage and are saved (RV4)', async () => {
		store.set(filterKey, '?system=gbc');
		navigateTo('/review?reason=AmbiguousGroup');
		answers[`${list(1, 20)}&reason=AmbiguousGroup`] = { status: 200, body: page([1], 1) };
		render(QueuePage);

		await screen.findByText('file1.gb');
		expect(goto).not.toHaveBeenCalled();
		expect(calls.some((c) => c.includes('system=gbc'))).toBe(false);
		expect(store.get(filterKey)).toBe('?reason=AmbiguousGroup');
	});

	it('an empty URL restores the saved filter with replaceState (RV4)', async () => {
		store.set(filterKey, '?q=abc&page=2');
		answers[`${list(2, 20)}&q=abc`] = { status: 200, body: page([21], 21) };
		render(QueuePage);

		await screen.findByText('file21.gb');
		expect(goto).toHaveBeenCalledWith('/review?q=abc&page=2', urlOptions);
		expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('abc');
	});

	it('Clear empties the URL and storage (RV4)', async () => {
		navigateTo('/review?q=abc&system=gb&page=2');
		render(QueuePage);
		const clear = await screen.findByRole('button', { name: 'Clear' });

		await fireEvent.click(clear);

		expect(goto).toHaveBeenLastCalledWith('/review', urlOptions);
		await vi.waitFor(() => expect(store.has(filterKey)).toBe(false));
		expect((clear as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('');
	});

	it('shows the filtered empty state only when a filter is set (RV4)', async () => {
		answers[list(1, 20)] = { status: 200, body: page([], 0) };
		answers[`${list(1, 20)}&system=gb`] = { status: 200, body: page([], 0) };
		render(QueuePage);
		expect(await screen.findByText('Nothing to review.')).toBeTruthy();

		await fireEvent.change(screen.getByLabelText('System'), { target: { value: 'gb' } });

		expect(await screen.findByText('No review items match this filter.')).toBeTruthy();
	});

	it('a throwing localStorage does not break the page (RV4)', async () => {
		const boom = () => {
			throw new Error('denied');
		};
		vi.stubGlobal('localStorage', { getItem: boom, setItem: boom, removeItem: boom });
		answers[list(1, 20)] = { status: 200, body: page([1], 1) };
		render(QueuePage);

		expect(await screen.findByText('file1.gb')).toBeTruthy();
		await fireEvent.change(screen.getByLabelText('System'), { target: { value: 'gb' } });
		expect(goto).toHaveBeenLastCalledWith('/review?system=gb', urlOptions);
	});

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

		const button = await screen.findByRole('button', { name: 'Delete file1.gb' });
		expect(screen.getByRole('heading', { name: 'Delete File', hidden: true })).toBeTruthy();
		await fireEvent.click(button);

		expect(await screen.findByText('abcd1234')).toBeTruthy();
		expect(
			screen.getByRole('heading', { name: 'Delete File: tetris hack.gb', hidden: true })
		).toBeTruthy();
		const table = screen.getByRole('table', { name: 'Details of tetris hack.gb', hidden: true });
		const rows = [...table.querySelectorAll('tbody tr')].map(
			(r) => r.querySelector('th')!.textContent
		);
		expect(rows.slice(0, 9)).toEqual([
			'File name',
			'System',
			'Reason',
			'Size',
			'CRC32',
			'MD5',
			'SHA1',
			'Source path',
			'Archive'
		]);
		expect(rows.at(-1)).toBe('Added');
		expect(screen.getByText('nested/file1.gb')).toBeTruthy();
		expect(screen.getByText('TETRIS')).toBeTruthy();
		expect(screen.getByRole('alert', { hidden: true }).textContent?.replace(/\s+/g, ' ')).toContain(
			'This deletes the whole archive ingest/gb/pack.zip, including every other file in it.'
		);
	});

	it('Shift+click deletes a loose item without the modal and reloads (RV1)', async () => {
		toasts.items = [];
		answers[list(1, 20)] = { status: 200, body: page([1, 2], 2) };
		render(QueuePage);

		const button = await screen.findByRole('button', { name: 'Delete file1.gb' });
		expect(tooltips.some((t) => t.node === button && t.text === shiftTip)).toBe(true);
		await fireEvent.click(button, { shiftKey: true });

		await vi.waitFor(() => expect(calls.filter((c) => c === list(1, 20))).toHaveLength(2));
		expect(calls).toContain('DELETE /api/review/1');
		expect(calls).not.toContain('GET /api/review/1');
		expect(toasts.items.at(-1)?.text).toBe('Deleted file1.gb.');
	});

	it('Shift+click on an archive item opens the modal instead (RV1)', async () => {
		answers[list(1, 20)] = { status: 200, body: page([100], 1) };
		answers['GET /api/review/100'] = { status: 200, body: { ...noMatch, id: 100 } };
		render(QueuePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete file100.gb' }), {
			shiftKey: true
		});

		await vi.waitFor(() => expect(calls).toContain('GET /api/review/100'));
		expect(calls).not.toContain('DELETE /api/review/100');
	});

	it('a failed Shift+click delete toasts and keeps the list (RV1)', async () => {
		toasts.items = [];
		answers[list(1, 20)] = { status: 200, body: page([1], 1) };
		answers['DELETE /api/review/1'] = { status: 409, body: { code: 'JobRunning', title: 'x' } };
		render(QueuePage);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete file1.gb' }), {
			shiftKey: true
		});

		await vi.waitFor(() => expect(toasts.items.at(-1)?.kind).toBe('warning'));
		expect(calls.filter((c) => c === list(1, 20))).toHaveLength(1);
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
		const details = screen.getByRole('table', { name: /^Details of/, hidden: true });
		expect(details.textContent).not.toContain('Archive');
		await fireEvent.click(confirm);

		await vi.waitFor(() => expect(calls).toContain('DELETE /api/review/11'));
		await vi.waitFor(() => expect(calls.filter((c) => c === list(1, 10))).toHaveLength(2));
	});
});
