import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { CollectionDetail, CollectionListItem } from '#lib/types/index.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';
import { expectOnePageTitle } from '#lib/testing/pageTitle.js';

const goto = vi.fn<(url: string, opts?: unknown) => void>((url) => navigateTo(url));
vi.mock('$app/navigation', () => ({ goto: (url: string, opts?: unknown) => goto(url, opts) }));
vi.mock('$app/state', () => ({
	page: {
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: CollectionsPage } = await import('./+page.svelte');
const { default: CollectionModal } = await import('#lib/components/CollectionModal.svelte');

type Answer = { status: number; body?: unknown };

const item = (id: string, name: string, extra: Partial<CollectionListItem> = {}) => ({
	id,
	name,
	gameCount: 2,
	coverUrl: null,
	updatedAt: '2026-11-09T00:00:00Z',
	...extra
});
const detail = (
	id: string,
	name: string,
	extra: Partial<CollectionDetail> = {}
): CollectionDetail => ({
	...item(id, name),
	notes: null,
	createdAt: '2026-11-09T00:00:00Z',
	...extra
});
const pageOf = (items: CollectionListItem[], total = items.length) => ({
	items,
	page: 1,
	pageSize: 20,
	total
});

let calls: { key: string; body: unknown }[];
let answers: Record<string, Answer[]>;

beforeEach(() => {
	resetClient();
	goto.mockClear();
	navigateTo('/collections');
	toasts.items.length = 0;
	calls = [];
	answers = {};
	const store = new Map<string, string>();
	vi.stubGlobal('localStorage', {
		getItem: (key: string) => store.get(key) ?? null,
		setItem: (key: string, value: string) => void store.set(key, value),
		removeItem: (key: string) => void store.delete(key)
	});
	vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
		const key = `${init?.method ?? 'GET'} ${url}`;
		calls.push({ key, body: init?.body });
		const answer = answers[key]?.shift() ?? { status: 204 };
		const payload = answer.body === undefined ? null : JSON.stringify(answer.body);
		return Promise.resolve(new Response(payload, { status: answer.status }));
	});
});
afterEach(() => vi.unstubAllGlobals());

const keys = () => calls.map((c) => c.key).filter((k) => k !== 'GET /api/auth/antiforgery');
const modal = () => within(document.querySelector<HTMLElement>('.modal')!);
const modalButton = (name: string | RegExp) =>
	modal().getByRole('button', { name, hidden: true }) as HTMLButtonElement;
const nameInput = () => modal().getByLabelText('Name') as HTMLInputElement;
const problem = (
	status: number,
	code: string,
	title: string,
	errors?: Record<string, string[]>
) => ({
	status,
	body: { status, code, title, errors }
});

describe('collections page (B22)', () => {
	it('shows a card per collection with its cover or a placeholder, linking to it', async () => {
		answers['GET /api/collections?pageSize=20'] = [
			{
				status: 200,
				body: pageOf([
					item('c1', 'Pokémon', { coverUrl: '/api/collections/c1/cover?v=1', gameCount: 1 }),
					item('c2', 'Zelda')
				])
			}
		];
		render(CollectionsPage);

		const link = await screen.findByRole('link', { name: 'Pokémon' });
		expect(link.getAttribute('href')).toBe('/collections/c1');
		expect(screen.getByRole('img', { name: 'Pokémon cover' }).getAttribute('src')).toBe(
			'/api/collections/c1/cover?v=1'
		);
		expect(screen.getByText('1 game')).toBeTruthy();
		expect(screen.getByText('2 games')).toBeTruthy();
		expect(screen.getAllByTestId('placeholder')).toHaveLength(1);
	});

	it('shows the empty state with a New collection action', async () => {
		answers['GET /api/collections?pageSize=20'] = [{ status: 200, body: pageOf([]) }];
		render(CollectionsPage);

		expect(await screen.findByText('You have no collections yet.')).toBeTruthy();
		expect(screen.getAllByRole('button', { name: /New collection/ })).toHaveLength(2);
	});

	it('reads q and page from the URL and pages through goto with replace', async () => {
		navigateTo('/collections?q=poke&page=2');
		answers['GET /api/collections?q=poke&page=2&pageSize=20'] = [
			{ status: 200, body: { ...pageOf([item('c1', 'Pokémon')], 45), page: 2 } }
		];
		render(CollectionsPage);

		expect(await screen.findByText('Page 2 of 3')).toBeTruthy();
		expect((screen.getByLabelText('Filter collections') as HTMLInputElement).value).toBe('poke');
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));

		expect(goto).toHaveBeenLastCalledWith('/collections?q=poke&page=3', {
			replace: true,
			reset: false
		});
	});

	it('typing in the filter updates q after a pause and goes back to page 1', async () => {
		vi.useFakeTimers();
		try {
			navigateTo('/collections?page=2');
			render(CollectionsPage);
			const input = screen.getByLabelText('Filter collections');

			await fireEvent.input(input, { target: { value: ' zel ' } });
			vi.advanceTimersByTime(300);

			expect(goto).toHaveBeenLastCalledWith('/collections?q=zel', { replace: true, reset: false });
		} finally {
			vi.useRealTimers();
		}
	});

	it('the rows-per-page choice is remembered under its own key', async () => {
		answers['GET /api/collections?pageSize=20'] = [
			{ status: 200, body: pageOf([item('c1', 'A')]) }
		];
		render(CollectionsPage);
		await screen.findByRole('link', { name: 'A' });

		await fireEvent.change(screen.getByLabelText('Rows per page'), { target: { value: '50' } });

		expect(localStorage.getItem('rnrm.pageSize.collections')).toBe('50');
		await vi.waitFor(() => expect(keys()).toContain('GET /api/collections?pageSize=50'));
	});

	it('has one page-title h1, with New collection kept at the right (B1, B2)', async () => {
		render(CollectionsPage);

		expectOnePageTitle('Collections');
		expect(
			screen.getByRole('button', { name: /New collection/ }).classList.contains('ms-auto')
		).toBe(true);
	});
});

describe('collections tabs (discovery B1)', () => {
	const tabLink = (name: string) =>
		within(screen.getByRole('navigation', { name: 'Collections views' })).getByRole('link', {
			name
		});
	const current = () =>
		within(screen.getByRole('navigation', { name: 'Collections views' }))
			.getAllByRole('link')
			.filter((l) => l.getAttribute('aria-current') === 'page')
			.map((l) => l.textContent?.trim());

	it('a bare URL shows User collections as the current tab', async () => {
		answers['GET /api/collections?pageSize=20'] = [
			{ status: 200, body: pageOf([item('c1', 'A')]) }
		];
		render(CollectionsPage);

		await screen.findByRole('link', { name: 'A' });
		expect(current()).toEqual(['User collections']);
		expect(tabLink('User collections').getAttribute('href')).toBe('/collections');
		expect(tabLink('Publishers').getAttribute('href')).toBe('/collections?tab=publishers');
		expect(tabLink('Developers').getAttribute('href')).toBe('/collections?tab=developers');
		expect(tabLink('Genres').getAttribute('href')).toBe('/collections?tab=genres');
		expect(tabLink('Systems').getAttribute('href')).toBe('/collections?tab=systems');
		expect(screen.getAllByRole('button', { name: /New collection/ })).toHaveLength(1);
	});

	it('a discovery tab hides the collections list and fetches none', async () => {
		navigateTo('/collections?tab=genres&q=poke');
		render(CollectionsPage);

		expect(current()).toEqual(['Genres']);
		expect(screen.queryByLabelText('Filter collections')).toBeNull();
		expect(screen.queryByRole('button', { name: /New collection/ })).toBeNull();
		await Promise.resolve();
		expect(keys().filter((k) => k.startsWith('GET /api/collections'))).toEqual([]);
	});

	it('an unknown tab value is User collections with its legacy query kept', async () => {
		navigateTo('/collections?tab=studios&q=zel&page=2');
		answers['GET /api/collections?q=zel&page=2&pageSize=20'] = [
			{ status: 200, body: { ...pageOf([item('c1', 'Zelda')], 45), page: 2 } }
		];
		render(CollectionsPage);

		expect(await screen.findByRole('link', { name: 'Zelda' })).toBeTruthy();
		expect(current()).toEqual(['User collections']);
		expect((screen.getByLabelText('Filter collections') as HTMLInputElement).value).toBe('zel');
	});

	it('a shared selection link opens the group and its games directly', async () => {
		const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
		navigateTo(`/collections?tab=publishers&key=${key}&groupPage=2&gamePage=2`);
		answers['GET /api/discovery/publishers?page=2&pageSize=20'] = [
			{
				status: 200,
				body: {
					items: [{ key, name: 'Nintendo', gameCount: 30, logoUrl: null }],
					page: 2,
					pageSize: 20,
					total: 21
				}
			}
		];
		answers[`GET /api/discovery/publishers/games?key=${key}&page=2&pageSize=20`] = [
			{ status: 200, body: { items: [], page: 2, pageSize: 20, total: 30 } }
		];
		render(CollectionsPage);

		const selected = await screen.findByRole('link', { name: /Nintendo/ });
		expect(selected.getAttribute('aria-current')).toBe('true');
		await vi.waitFor(() =>
			expect(screen.getByRole('heading', { name: /Nintendo/ }).textContent).toContain('30 games')
		);
		expect(current()).toEqual(['Publishers']);
	});

	it('a discovery tab without a selection asks for one', async () => {
		navigateTo('/collections?tab=genres');
		render(CollectionsPage);

		expect(
			await screen.findByText('Choose a genre to see its games across every system.')
		).toBeTruthy();
		expect(keys().some((k) => k.includes('/games'))).toBe(false);
	});

	it('the Developers tab asks for a developer and lists developers', async () => {
		navigateTo('/collections?tab=developers');
		answers['GET /api/discovery/developers?pageSize=20'] = [
			{ status: 200, body: { items: [], page: 1, pageSize: 20, total: 0 } }
		];
		render(CollectionsPage);

		expect(
			await screen.findByText('Choose a developer to see its games across every system.')
		).toBeTruthy();
		expect(current()).toEqual(['Developers']);
		expect(await screen.findByText('No games have developers yet.')).toBeTruthy();
	});

	it('?name= resolves to the group key, replacing the URL (P3)', async () => {
		const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
		navigateTo('/collections?tab=developers&name=Nintendo%20EAD');
		answers['GET /api/discovery/developers/key?name=Nintendo+EAD'] = [
			{ status: 200, body: { key } }
		];
		render(CollectionsPage);

		await vi.waitFor(() =>
			expect(goto).toHaveBeenCalledWith(`/collections?tab=developers&key=${key}`, {
				replace: true,
				reset: false
			})
		);
	});

	it.each([
		[404, { status: 404, code: 'NotFound' }],
		[500, { status: 500, title: 'Boom' }]
	])('?name= that fails with %s falls back to find', async (status, body) => {
		navigateTo(`/collections?tab=publishers&name=${encodeURIComponent('  Hudson Soft ')}`);
		answers['GET /api/discovery/publishers/key?name=Hudson+Soft'] = [{ status, body }];
		render(CollectionsPage);

		await vi.waitFor(() =>
			expect(goto).toHaveBeenCalledWith('/collections?tab=publishers&find=Hudson+Soft', {
				replace: true,
				reset: false
			})
		);
	});

	it('a key in the URL wins over ?name=, which is dropped without a request', async () => {
		const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
		navigateTo(`/collections?tab=genres&key=${key}&name=Puzzle`);
		render(CollectionsPage);

		await vi.waitFor(() =>
			expect(goto).toHaveBeenCalledWith(`/collections?tab=genres&key=${key}`, {
				replace: true,
				reset: false
			})
		);
		expect(keys().some((k) => k.includes('/key?'))).toBe(false);
	});

	it('back and forward between tabs follow the URL', async () => {
		navigateTo('/collections?q=zel');
		answers['GET /api/collections?q=zel&pageSize=20'] = [
			{ status: 200, body: pageOf([item('c1', 'Zelda')]) },
			{ status: 200, body: pageOf([item('c1', 'Zelda')]) }
		];
		render(CollectionsPage);
		await screen.findByRole('link', { name: 'Zelda' });

		navigateTo('/collections?tab=publishers');
		await vi.waitFor(() => expect(current()).toEqual(['Publishers']));
		expect(screen.queryByRole('link', { name: 'Zelda' })).toBeNull();

		navigateTo('/collections?q=zel');
		expect(await screen.findByRole('link', { name: 'Zelda' })).toBeTruthy();
		expect(current()).toEqual(['User collections']);
		expect((screen.getByLabelText('Filter collections') as HTMLInputElement).value).toBe('zel');
		expect(keys().filter((k) => k.startsWith('GET /api/collections'))).toEqual([
			'GET /api/collections?q=zel&pageSize=20',
			'GET /api/collections?q=zel&pageSize=20'
		]);
	});
});

describe('systems tab (Systems tab B1–B6)', () => {
	const system = (id: string, displayName: string, gameCount: number) => ({
		id,
		displayName,
		shortName: id.toUpperCase(),
		gameCount,
		romCount: gameCount,
		regions: []
	});
	const systems = [system('gb', 'Game Boy', 1), system('nes', 'NES', 0)];
	const games = (system: string) => `GET /api/games?system=${system}&page=1&pageSize=20`;
	const current = () =>
		within(screen.getByRole('navigation', { name: 'Collections views' }))
			.getAllByRole('link')
			.filter((l) => l.getAttribute('aria-current') === 'page')
			.map((l) => l.textContent?.trim());
	const gameRequests = () => keys().filter((k) => k.startsWith('GET /api/games'));

	it('is a tab that asks for a system at once, before the list loads', async () => {
		navigateTo('/collections?tab=systems');
		answers['GET /api/systems'] = [{ status: 200, body: systems }];
		render(CollectionsPage);

		expect(current()).toEqual(['Systems']);
		expect(screen.getByText('Choose a system to see its games and emulators.')).toBeTruthy();
		expect(screen.queryByRole('list', { name: 'Systems' })).toBeNull();
		expect(await screen.findByRole('table', { name: 'Systems' })).toBeTruthy();
		expect(screen.queryByLabelText('Filter collections')).toBeNull();
		expect(keys().filter((k) => k.startsWith('GET /api/collections'))).toEqual([]);
		expect(gameRequests()).toEqual([]);
	});

	it('a selected system shows its games, and selecting others never refetches the list', async () => {
		navigateTo('/collections?tab=systems&system=gb');
		answers['GET /api/systems'] = [{ status: 200, body: systems }];
		answers[games('gb')] = [{ status: 200, body: pageOf([], 0) }];
		answers[games('nes')] = [{ status: 200, body: pageOf([], 0) }];
		render(CollectionsPage);

		expect(await screen.findByText('No games for Game Boy yet.')).toBeTruthy();
		expect(screen.getByRole('link', { name: /Game Boy/ }).getAttribute('aria-current')).toBe(
			'true'
		);
		expect(screen.getByRole('button', { name: 'Emulators' })).toBeTruthy();

		navigateTo('/collections?tab=systems&system=nes');
		expect(await screen.findByText('No games for NES yet.')).toBeTruthy();
		navigateTo('/collections?tab=systems');
		expect(await screen.findByText('Choose a system to see its games and emulators.')).toBeTruthy();
		expect(keys().filter((k) => k === 'GET /api/systems')).toHaveLength(1);
		expect(gameRequests()).toEqual([games('gb'), games('nes')]);
	});

	it.each(['GB', 'zzz', '../gb'])(
		'an unknown system %j is not supported, with no games request',
		async (id) => {
			navigateTo(`/collections?tab=systems&system=${encodeURIComponent(id)}`);
			answers['GET /api/systems'] = [{ status: 200, body: systems }];
			render(CollectionsPage);

			expect(screen.getAllByText('Loading systems…', { exact: false })).toHaveLength(2);
			expect(await screen.findByText("This system isn't supported.")).toBeTruthy();
			expect(screen.getByRole('link', { name: 'Back to all systems' }).getAttribute('href')).toBe(
				'/collections?tab=systems'
			);
			expect(screen.queryByRole('button', { name: 'Emulators' })).toBeNull();
			expect(
				screen.getAllByRole('link').filter((l) => l.getAttribute('aria-current') === 'true')
			).toEqual([]);
			expect(gameRequests()).toEqual([]);
		}
	);

	it('a failed system list shows the error with nothing on the right, and retries', async () => {
		navigateTo('/collections?tab=systems&system=gb');
		answers['GET /api/systems'] = [
			{ status: 500, body: { status: 500, title: 'Boom' } },
			{ status: 200, body: systems }
		];
		answers[games('gb')] = [{ status: 200, body: pageOf([], 0) }];
		render(CollectionsPage);

		const retry = await screen.findByRole('button', { name: /Retry/ });
		expect(screen.queryByText("This system isn't supported.")).toBeNull();
		expect(screen.queryByText('Loading systems…', { exact: false })).toBeNull();
		expect(gameRequests()).toEqual([]);

		await fireEvent.click(retry);
		expect(await screen.findByText('No games for Game Boy yet.')).toBeTruthy();
	});

	it('a failed list without a selection shows no prompt', async () => {
		navigateTo('/collections?tab=systems');
		answers['GET /api/systems'] = [{ status: 500, body: { status: 500, title: 'Boom' } }];
		render(CollectionsPage);

		await screen.findByRole('button', { name: /Retry/ });
		expect(screen.queryByText('Choose a system to see its games and emulators.')).toBeNull();
	});
});

describe('CollectionModal (B23)', () => {
	async function openCreate(): Promise<void> {
		answers['GET /api/collections?pageSize=20'] = [{ status: 200, body: pageOf([]) }];
		render(CollectionsPage);
		await screen.findByText('You have no collections yet.');
		await fireEvent.click(screen.getAllByRole('button', { name: /New collection/ })[0]);
	}

	it('creates without a cover and goes to the new collection', async () => {
		answers['POST /api/collections'] = [
			{ status: 201, body: detail('new', 'Mine', { notes: 'n' }) }
		];
		await openCreate();

		await fireEvent.input(nameInput(), { target: { value: '  Mine ' } });
		await fireEvent.input(modal().getByLabelText('Notes (Markdown)'), {
			target: { value: 'n\r\n' }
		});
		await fireEvent.click(modalButton('Create'));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/collections/new', undefined));
		const post = calls.find((c) => c.key === 'POST /api/collections')!;
		expect(JSON.parse(post.body as string)).toEqual({ name: 'Mine', notes: 'n\n' });
		expect(keys().filter((k) => k.includes('/cover'))).toEqual([]);
	});

	it('creates, then uploads the chosen cover', async () => {
		answers['POST /api/collections'] = [{ status: 201, body: detail('new', 'Mine') }];
		answers['POST /api/collections/new/cover'] = [{ status: 200, body: detail('new', 'Mine') }];
		await openCreate();

		await fireEvent.input(nameInput(), { target: { value: 'Mine' } });
		const file = new File([new Uint8Array([1])], 'art.png', { type: 'image/png' });
		await fireEvent.change(modal().getByLabelText('Cover image file'), {
			target: { files: [file] }
		});
		await fireEvent.click(modalButton('Create'));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/collections/new', undefined));
		const upload = calls.find((c) => c.key === 'POST /api/collections/new/cover')!;
		expect((upload.body as FormData).get('file')).toBe(file);
	});

	it('a failed cover step toasts but still goes to the created collection', async () => {
		answers['POST /api/collections'] = [{ status: 201, body: detail('new', 'Mine') }];
		answers['POST /api/collections/new/cover/from-url'] = [
			problem(502, 'ImageDownloadFailed', 'The image could not be downloaded.')
		];
		await openCreate();

		await fireEvent.input(nameInput(), { target: { value: 'Mine' } });
		await fireEvent.click(modal().getByRole('radio', { name: 'URL', hidden: true }));
		await fireEvent.input(modal().getByLabelText('Cover image URL'), {
			target: { value: 'https://example.com/a.png' }
		});
		await fireEvent.click(modalButton('Create'));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/collections/new', undefined));
		expect(toasts.items.map((t) => [t.kind, t.text])).toContainEqual([
			'danger',
			"Mine was created, but its cover wasn't saved: The image could not be downloaded."
		]);
	});

	it('an empty name is caught before any request', async () => {
		await openCreate();

		await fireEvent.click(modalButton('Create'));

		expect(modal().getByRole('alert', { hidden: true }).textContent).toContain(
			'A name is required.'
		);
		expect(keys()).not.toContain('POST /api/collections');
	});

	it('409 CollectionNameTaken shows on the name field', async () => {
		answers['POST /api/collections'] = [
			problem(409, 'CollectionNameTaken', 'You already have a collection with this name.')
		];
		await openCreate();

		await fireEvent.input(nameInput(), { target: { value: 'Pokemon' } });
		await fireEvent.click(modalButton('Create'));

		await vi.waitFor(() => expect(nameInput().classList.contains('is-invalid')).toBe(true));
		expect(modal().getByRole('alert', { hidden: true }).textContent).toContain(
			'You already have a collection with this name.'
		);
		expect(goto).not.toHaveBeenCalled();
	});

	describe('editing', () => {
		const existing = detail('c1', 'Pokémon', {
			notes: '# Gotta\n\n**catch** them',
			coverUrl: '/api/collections/c1/cover?v=1'
		});

		it('opens in View with the notes rendered through markdown.ts', async () => {
			render(CollectionModal, { open: true, collection: existing });

			const rendered = await modal().findByTestId('notes-rendered');
			expect(within(rendered).getByRole('heading', { name: 'Gotta', hidden: true })).toBeTruthy();
			expect(rendered.querySelector('strong')?.textContent).toBe('catch');
		});

		it('Save PATCHes name and notes; clearing notes sends null', async () => {
			const onchanged = vi.fn();
			answers['PATCH /api/collections/c1'] = [{ status: 200, body: { ...existing, notes: null } }];
			render(CollectionModal, { open: true, collection: existing, onchanged });

			await fireEvent.click(modal().getByRole('radio', { name: 'Edit', hidden: true }));
			await fireEvent.input(modal().getByLabelText('Notes (Markdown)'), {
				target: { value: '  ' }
			});
			await fireEvent.click(modalButton('Save'));

			await vi.waitFor(() => expect(onchanged).toHaveBeenCalled());
			const patch = calls.find((c) => c.key === 'PATCH /api/collections/c1')!;
			expect(JSON.parse(patch.body as string)).toEqual({ name: 'Pokémon', notes: null });
		});

		it('cover actions apply immediately and refresh the preview', async () => {
			const onchanged = vi.fn();
			answers['POST /api/collections/c1/cover'] = [
				{ status: 200, body: { ...existing, coverUrl: '/api/collections/c1/cover?v=2' } }
			];
			answers['DELETE /api/collections/c1/cover'] = [
				{ status: 200, body: { ...existing, coverUrl: null } }
			];
			render(CollectionModal, { open: true, collection: existing, onchanged });

			const file = new File([new Uint8Array([1])], 'art.jpg', { type: 'image/jpeg' });
			await fireEvent.change(modal().getByLabelText('Cover image file'), {
				target: { files: [file] }
			});
			await fireEvent.click(modalButton(/Set cover/));
			await vi.waitFor(() =>
				expect(
					modal().getByRole('img', { name: 'Current cover', hidden: true }).getAttribute('src')
				).toBe('/api/collections/c1/cover?v=2')
			);

			await fireEvent.click(modalButton(/Remove cover/));
			await vi.waitFor(() =>
				expect(modal().queryByRole('img', { name: 'Current cover', hidden: true })).toBeNull()
			);
			expect(onchanged).toHaveBeenCalledTimes(2);
			expect(keys()).not.toContain('PATCH /api/collections/c1');
		});

		it('a rejected cover shows the server message under the cover input', async () => {
			answers['POST /api/collections/c1/cover'] = [
				problem(400, 'ValidationFailed', 'Validation failed', {
					file: ["The image's dimensions can't be read."]
				})
			];
			render(CollectionModal, { open: true, collection: existing });

			const file = new File([new Uint8Array([1])], 'art.png', { type: 'image/png' });
			await fireEvent.change(modal().getByLabelText('Cover image file'), {
				target: { files: [file] }
			});
			await fireEvent.click(modalButton(/Set cover/));

			await vi.waitFor(() =>
				expect(modal().getByRole('alert', { hidden: true }).textContent).toContain(
					"The image's dimensions can't be read."
				)
			);
		});
	});
});

describe('collections search (B16)', () => {
	const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
	const groups = (items: { key: string; name: string }[]) => ({
		status: 200,
		body: {
			items: items.map((g) => ({ ...g, gameCount: 1, logoUrl: null })),
			page: 1,
			pageSize: 20,
			total: items.length
		}
	});
	const wait = () => new Promise((done) => setTimeout(done, 350));

	it('typing writes find with replace, keeps the selection and goes back to group page 1', async () => {
		navigateTo(`/collections?tab=publishers&key=${key}&groupPage=2`);
		answers['GET /api/discovery/publishers?page=2&pageSize=20'] = [
			groups([{ key, name: 'Nintendo' }])
		];
		answers['GET /api/discovery/publishers?pageSize=20&q=nin'] = [
			groups([{ key, name: 'Nintendo' }])
		];
		render(CollectionsPage);
		await screen.findByRole('link', { name: /Nintendo/ });

		await fireEvent.input(screen.getByLabelText('Search publishers'), {
			target: { value: 'nin' }
		});
		await wait();

		expect(goto).toHaveBeenLastCalledWith(`/collections?tab=publishers&key=${key}&find=nin`, {
			replace: true,
			reset: false
		});
		await vi.waitFor(() =>
			expect(keys()).toContain('GET /api/discovery/publishers?pageSize=20&q=nin')
		);
	});

	it('the box follows the URL on Back/Forward, and a tab change drops find', async () => {
		navigateTo('/collections?tab=genres&find=rpg');
		answers['GET /api/discovery/genres?pageSize=20&q=rpg'] = [groups([])];
		answers['GET /api/discovery/genres?pageSize=20'] = [groups([])];
		render(CollectionsPage);
		const box = screen.getByLabelText('Search genres') as HTMLInputElement;
		expect(box.value).toBe('rpg');

		navigateTo('/collections?tab=genres');
		await vi.waitFor(() => expect(box.value).toBe(''));
		expect(
			within(screen.getByRole('navigation', { name: 'Collections views' }))
				.getByRole('link', { name: 'Publishers' })
				.getAttribute('href')
		).toBe('/collections?tab=publishers');
	});

	it('a sort click writes sort with replace at group page 1, keeping find and the selection (G4, G5)', async () => {
		navigateTo(`/collections?tab=publishers&key=${key}&groupPage=2&gamePage=3&find=nin`);
		answers['GET /api/discovery/publishers?page=2&pageSize=20&q=nin'] = [
			groups([{ key, name: 'Nintendo' }])
		];
		answers['GET /api/discovery/publishers?pageSize=20&q=nin&sort=games'] = [
			groups([{ key, name: 'Nintendo' }])
		];
		render(CollectionsPage);
		await screen.findByRole('link', { name: 'Nintendo' });

		await fireEvent.click(screen.getByRole('button', { name: /^Games/ }));

		expect(goto).toHaveBeenLastCalledWith(
			`/collections?tab=publishers&key=${key}&gamePage=3&find=nin&sort=games`,
			{ replace: true, reset: false }
		);
		await vi.waitFor(() =>
			expect(keys()).toContain('GET /api/discovery/publishers?pageSize=20&q=nin&sort=games')
		);
		await fireEvent.click(await screen.findByRole('button', { name: /^Games/ }));
		expect(goto).toHaveBeenLastCalledWith(
			`/collections?tab=publishers&key=${key}&gamePage=3&find=nin&sort=games&dir=asc`,
			{ replace: true, reset: false }
		);
	});

	it('Clear drops find, sort and dir with replace, keeping the selection and game page (G7)', async () => {
		navigateTo(
			`/collections?tab=genres&key=${key}&groupPage=2&gamePage=3&find=rpg&sort=games&dir=asc`
		);
		answers['GET /api/discovery/genres?page=2&pageSize=20&q=rpg&sort=games&dir=asc'] = [groups([])];
		render(CollectionsPage);

		await fireEvent.click(await screen.findByRole('button', { name: /Clear/ }));

		expect(goto).toHaveBeenLastCalledWith(`/collections?tab=genres&key=${key}&gamePage=3`, {
			replace: true,
			reset: false
		});
		expect((screen.getByLabelText('Search genres') as HTMLInputElement).value).toBe('');
		await vi.waitFor(() => expect(screen.queryByRole('button', { name: /Clear/ })).toBeNull());
	});

	it('Systems: typing filters through the URL', async () => {
		navigateTo('/collections?tab=systems');
		answers['GET /api/systems'] = [
			{
				status: 200,
				body: [
					{
						id: 'gb',
						displayName: 'Game Boy',
						shortName: 'GB',
						gameCount: 1,
						romCount: 1,
						regions: []
					},
					{
						id: 'nes',
						displayName: 'NES',
						shortName: 'NES',
						gameCount: 0,
						romCount: 0,
						regions: []
					}
				]
			}
		];
		render(CollectionsPage);
		await screen.findByRole('table', { name: 'Systems' });

		await fireEvent.input(screen.getByLabelText('Search systems'), { target: { value: 'game' } });
		await wait();

		expect(goto).toHaveBeenLastCalledWith('/collections?tab=systems&find=game', {
			replace: true,
			reset: false
		});
		await vi.waitFor(() =>
			expect(
				within(screen.getByRole('table', { name: 'Systems' }))
					.getAllByRole('link')
					.map((l) => l.getAttribute('href'))
			).toEqual(['/collections?tab=systems&system=gb&find=game'])
		);
	});

	it('Systems: sort by Games then Clear, through the URL, keeping the selection (G4, G7)', async () => {
		navigateTo('/collections?tab=systems&system=gb&gamePage=2&find=n');
		const system = (id: string, displayName: string, gameCount: number) => ({
			id,
			displayName,
			shortName: id,
			gameCount,
			romCount: gameCount,
			regions: []
		});
		answers['GET /api/systems'] = [
			{ status: 200, body: [system('gb', 'Game Boy', 1), system('nes', 'NES', 5)] }
		];
		render(CollectionsPage);
		const table = await screen.findByRole('table', { name: 'Systems' });
		const order = () =>
			within(table)
				.getAllByRole('link')
				.map((l) => l.querySelector('span.text-truncate')!.textContent);
		expect(order()).toEqual(['NES']);

		await fireEvent.click(screen.getByRole('button', { name: /^Games/ }));
		expect(goto).toHaveBeenLastCalledWith(
			'/collections?tab=systems&system=gb&gamePage=2&find=n&sort=games',
			{ replace: true, reset: false }
		);

		await fireEvent.click(await screen.findByRole('button', { name: /Clear/ }));
		expect(goto).toHaveBeenLastCalledWith('/collections?tab=systems&system=gb&gamePage=2', {
			replace: true,
			reset: false
		});
		await vi.waitFor(() => expect(order()).toEqual(['Game Boy', 'NES']));
	});
});

describe('remembered tab search and sort (G8)', () => {
	const key = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
	const empty = { status: 200, body: { items: [], page: 1, pageSize: 20, total: 0 } };
	const one = {
		status: 200,
		body: {
			items: [{ key, name: 'Sega', gameCount: 1, logoUrl: null }],
			page: 1,
			pageSize: 20,
			total: 1
		}
	};
	const gb = {
		status: 200,
		body: [
			{ id: 'gb', displayName: 'Game Boy', shortName: 'GB', gameCount: 1, romCount: 1, regions: [] }
		]
	};
	const wait = () => new Promise((done) => setTimeout(done, 350));
	const replaced = (url: string) =>
		expect(goto).toHaveBeenCalledWith(url, { replace: true, reset: false });

	it('a bare tab URL restores the saved search and sort with replace', async () => {
		localStorage.setItem('rnrm.collections.genres', 'find=rpg&sort=games');
		navigateTo('/collections?tab=genres');
		render(CollectionsPage);

		await vi.waitFor(() => replaced('/collections?tab=genres&find=rpg&sort=games'));
		await vi.waitFor(() =>
			expect((screen.getByLabelText('Search genres') as HTMLInputElement).value).toBe('rpg')
		);
	});

	it('a bare URL with a group page restores at group page 1', async () => {
		localStorage.setItem('rnrm.collections.publishers', 'sort=games&dir=asc');
		navigateTo('/collections?tab=publishers&groupPage=3');
		render(CollectionsPage);

		await vi.waitFor(() => replaced('/collections?tab=publishers&sort=games&dir=asc'));
	});

	it('Systems restores too, keeping other params', async () => {
		localStorage.setItem('rnrm.collections.systems', 'find=gb');
		navigateTo('/collections?tab=systems');
		answers['GET /api/systems'] = [{ status: 200, body: [] }];
		render(CollectionsPage);

		await vi.waitFor(() => replaced('/collections?tab=systems&find=gb'));
	});

	it.each([
		['/collections?tab=genres&find=puz', 'genres'],
		['/collections?tab=genres&dir=desc', 'genres'],
		[`/collections?tab=genres&key=${key}`, 'genres'],
		['/collections?tab=systems&system=gb', 'systems'],
		['/collections?tab=systems&sort=games', 'systems']
	] as const)('%s is left as it is and not overwritten', async (url, tab) => {
		localStorage.setItem(`rnrm.collections.${tab}`, 'find=rpg');
		answers['GET /api/systems'] = [{ status: 200, body: [] }];
		navigateTo(url);
		render(CollectionsPage);

		await wait();
		expect(goto).not.toHaveBeenCalled();
		expect(localStorage.getItem(`rnrm.collections.${tab}`)).toBe('find=rpg');
	});

	it('a game page name link that resolves to a key is not restored', async () => {
		localStorage.setItem('rnrm.collections.developers', 'find=zzz');
		answers['GET /api/discovery/developers/key?name=Nintendo+EAD'] = [
			{ status: 200, body: { key } }
		];
		navigateTo('/collections?tab=developers&name=Nintendo%20EAD');
		render(CollectionsPage);

		await vi.waitFor(() => replaced(`/collections?tab=developers&key=${key}`));
		await wait();
		expect(goto).toHaveBeenCalledOnce();
		expect(localStorage.getItem('rnrm.collections.developers')).toBe('find=zzz');
	});

	it('a malformed saved value is forgotten with no navigation', async () => {
		localStorage.setItem('rnrm.collections.publishers', 'sort=bogus&dir=asc&tab=genres');
		navigateTo('/collections?tab=publishers');
		render(CollectionsPage);

		await vi.waitFor(() => expect(localStorage.getItem('rnrm.collections.publishers')).toBeNull());
		expect(goto).not.toHaveBeenCalled();
	});

	it('a committed search and a sort click save; Back/Forward does not', async () => {
		navigateTo('/collections?tab=publishers');
		answers['GET /api/discovery/publishers?pageSize=20'] = [empty];
		answers['GET /api/discovery/publishers?pageSize=20&q=sega'] = [one];
		render(CollectionsPage);

		await fireEvent.input(screen.getByLabelText('Search publishers'), {
			target: { value: 'sega' }
		});
		await wait();
		expect(localStorage.getItem('rnrm.collections.publishers')).toBe('find=sega');

		await fireEvent.click(await screen.findByRole('button', { name: /^Games/ }));
		expect(localStorage.getItem('rnrm.collections.publishers')).toBe('find=sega&sort=games');

		navigateTo('/collections?tab=publishers&find=nin');
		await wait();
		expect(localStorage.getItem('rnrm.collections.publishers')).toBe('find=sega&sort=games');
		expect(localStorage.getItem('rnrm.collections.genres')).toBeNull();
	});

	it('Clear forgets the tab, leaving the others', async () => {
		localStorage.setItem('rnrm.collections.genres', 'find=rpg');
		localStorage.setItem('rnrm.collections.systems', 'sort=games');
		navigateTo('/collections?tab=genres&find=rpg');
		answers['GET /api/discovery/genres?pageSize=20&q=rpg'] = [empty];
		render(CollectionsPage);

		await fireEvent.click(await screen.findByRole('button', { name: /Clear/ }));

		replaced('/collections?tab=genres');
		expect(localStorage.getItem('rnrm.collections.genres')).toBeNull();
		expect(localStorage.getItem('rnrm.collections.systems')).toBe('sort=games');
		await wait();
		expect(goto).toHaveBeenCalledOnce();
	});

	it('Systems: a search saves under its own key', async () => {
		navigateTo('/collections?tab=systems');
		answers['GET /api/systems'] = [gb];
		render(CollectionsPage);

		await fireEvent.input(await screen.findByLabelText('Search systems'), {
			target: { value: 'gb' }
		});
		await wait();
		expect(localStorage.getItem('rnrm.collections.systems')).toBe('find=gb');
		expect(localStorage.getItem('rnrm.collections.publishers')).toBeNull();
	});
});
