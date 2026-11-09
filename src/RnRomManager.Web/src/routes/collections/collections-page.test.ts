import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { CollectionDetail, CollectionListItem } from '#lib/api/types.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

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
		setItem: (key: string, value: string) => void store.set(key, value)
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
