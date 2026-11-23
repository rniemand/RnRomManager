import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
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

// The release note button has a tooltip (R4); Bootstrap's Tooltip isn't mocked here.
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));
const { default: GamePage } = await import('./[id]/+page.svelte');

const rom = (id: number) => ({
	id,
	fileName: `Tetris ${id}.gb`,
	originalFileName: `Tetris ${id}.gb`,
	baseTitle: 'Tetris',
	regions: [],
	languages: [],
	revision: null,
	flags: [] as string[],
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
	version: null as string | null,
	files: null as
		| {
				id: number;
				fileName: string;
				sizeBytes: number;
				crc32: string;
				md5: string;
				sha1: string;
				downloadUrl: string;
		  }[]
		| null,
	totalSizeBytes: 1
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
	let bodies: Record<string, unknown>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		navigateTo('/games/g1');
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin'],
			mustChangePassword: false
		});
		calls = [];
		answers = {};
		bodies = {};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			// B16: the game's collections (the badges); answered here so the queued answers stay as they were.
			if (url.endsWith('/collections')) return Promise.resolve(new Response('[]', { status: 200 }));
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			if (typeof init?.body === 'string') bodies[key] = JSON.parse(init.body);
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
	/** B9: Delete release sits in the release body; expand the card first. */
	const releaseDelete = async (name: string) => {
		const toggle = await screen.findByRole('button', {
			name: new RegExp(`^(Show|Hide) details for ${name.replace('.', '\\.')}$`)
		});
		if (toggle.getAttribute('aria-expanded') !== 'true') await fireEvent.click(toggle);
		return screen.getByRole('button', { name: `Delete release ${name}` });
	};

	it('an admin sees the actions', async () => {
		auth.set({ id: 3, username: 'm', roles: ['admin'], mustChangePassword: false });
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		expect(await screen.findByRole('button', { name: /Edit Metadata/ })).toBeTruthy();
		expect(await releaseDelete('Tetris 1.gb')).toBeTruthy();
	});

	it('the admin actions are in the Manage menu, Delete game last after a divider (B3)', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		const manage = await screen.findByRole('button', { name: 'Manage' });
		const menu = manage.nextElementSibling!;
		expect(menu.classList).toContain('dropdown-menu');
		expect(
			[...menu.querySelectorAll('li')].map((li) =>
				li.querySelector('.dropdown-divider') ? '---' : li.textContent!.trim()
			)
		).toEqual([
			'View info.json',
			'---',
			'Edit Metadata',
			'Retry media',
			'Merge into…',
			'---',
			'Delete game'
		]);
		expect(
			within(menu as HTMLElement).getByRole('button', { name: 'Delete game' }).className
		).toContain('text-danger-emphasis');
	});

	it('Delete release sits right in the body actions row after Move release, Header details left (B9)', async () => {
		const detail = game([1]);
		detail.roms[0].header = { title: 'TETRIS', logoValid: true, romSizeBytes: null } as never;
		answers['GET /api/games/g1'] = [{ status: 200, body: detail }];
		render(GamePage);

		const del = await releaseDelete('Tetris 1.gb');
		expect(del.className).toBe('btn btn-sm btn-outline-danger');
		expect(del.textContent!.trim()).toBe('Delete release');
		const right = del.parentElement!;
		expect(right.className).toBe('ms-auto d-flex flex-wrap gap-2');
		expect([...right.children].map((b) => b.textContent!.trim())).toEqual([
			'Edit release',
			'Move release',
			'Delete release'
		]);
		const row = right.parentElement!;
		expect(row.firstElementChild!.textContent!.trim()).toBe('Header details');
		expect(
			screen
				.getByRole('article', { name: 'Tetris 1.gb' })
				.querySelector('.card-header .btn-outline-danger')
		).toBeNull();
	});

	it('Move release sits in the release body and opens the move dialog (R2)', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);
		await screen.findByRole('article', { name: 'Tetris 1.gb' });
		expect(
			screen
				.getByRole('article', { name: 'Tetris 1.gb' })
				.querySelector('.card-header [aria-label="Move release Tetris 1.gb"]')
		).toBeNull();

		await fireEvent.click(screen.getByRole('button', { name: 'Show details for Tetris 1.gb' }));
		const move = screen.getByRole('button', { name: 'Move release Tetris 1.gb' });
		expect(move.closest('.card-body')).toBeTruthy();
		expect(move.textContent!.trim()).toBe('Move release');
		await fireEvent.click(move);

		expect(modal().getByRole('heading', { name: 'Move release', hidden: true })).toBeTruthy();
	});

	it('a Site user sees no admin actions and a read-only rating', async () => {
		auth.set({ id: 2, username: 'u', roles: [], mustChangePassword: false });
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await screen.findByText('Releases');
		// Manage holds only View info.json for a Site user; no quick edit either.
		const manage = screen.getByRole('button', { name: 'Manage' });
		expect(
			[...manage.nextElementSibling!.querySelectorAll('li')].map((li) => li.textContent!.trim())
		).toEqual(['View info.json']);
		expect(screen.queryByRole('button', { name: /Edit Metadata/ })).toBeNull();
		expect(screen.queryByRole('button', { name: 'Quick edit' })).toBeNull();
		expect(
			screen.queryByRole('button', { name: 'Delete release Tetris 1.gb', hidden: true })
		).toBeNull();
		expect(
			screen.queryByRole('button', { name: 'Edit release Tetris 1.gb', hidden: true })
		).toBeNull();
		expect(screen.queryByRole('button', { name: /Rate 1 of 5/ })).toBeNull();
	});

	describe('Edit release (release editing B2, B3)', () => {
		const tagged = (overrides: Partial<ReturnType<typeof rom>> = {}) => ({
			...rom(1),
			regions: ['Mars', 'USA'],
			languages: ['En', 'Fr'],
			revision: 'Rev 1',
			flags: ['Hack'],
			...overrides
		});
		const editModal = () =>
			within(
				screen
					.getByRole('heading', { name: 'Edit release', hidden: true })
					.closest<HTMLElement>('.modal')!
			);
		const open = async (detail: object, name = 'Tetris 1.gb') => {
			answers['GET /api/games/g1'] = [
				{ status: 200, body: detail },
				{ status: 200, body: detail }
			];
			answers['GET /api/systems/regions'] = [{ status: 200, body: ['World', 'USA', 'Europe'] }];
			render(GamePage);
			await fireEvent.click(
				await screen.findByRole('button', { name: `Show details for ${name}` })
			);
			await fireEvent.click(screen.getByRole('button', { name: `Edit release ${name}` }));
			await editModal().findByLabelText('Languages');
		};
		const save = () =>
			fireEvent.click(editModal().getByRole('button', { name: 'Save', hidden: true }));

		it.each([
			['releases', [] as string[]],
			['updates', ['Update']],
			['dlc', ['DLC']]
		])('is the first Admin button on the %s tab', async (tab, flags) => {
			navigateTo(`/games/g1?tab=${tab}`);
			const detail = {
				...game([1]),
				fileSets: false,
				roms: [rom(9), { ...rom(1), flags, isPrimary: false }]
			};
			answers['GET /api/games/g1'] = [{ status: 200, body: detail }];
			render(GamePage);
			await fireEvent.click(
				await screen.findByRole('button', { name: 'Show details for Tetris 1.gb' })
			);
			const edit = screen.getByRole('button', { name: 'Edit release Tetris 1.gb' });
			expect(edit.className).toBe('btn btn-sm btn-outline-secondary');
			expect(edit.parentElement!.firstElementChild).toBe(edit);
		});

		it('prefills the form, keeps an unknown stored region, and has no Disc or Version field', async () => {
			await open({ ...game([1]), fileSets: false, roms: [tagged()] });
			const m = editModal();

			expect(m.getByText('Tetris 1.gb', { selector: 'p' }).className).toContain('font-monospace');
			expect((m.getByLabelText('Release') as HTMLInputElement).checked).toBe(true);
			expect(
				['World', 'USA', 'Europe', 'Mars'].map(
					(r) => (m.getByLabelText(r) as HTMLInputElement).checked
				)
			).toEqual([false, true, false, true]);
			expect((m.getByLabelText('Languages') as HTMLInputElement).value).toBe('En,Fr');
			expect((m.getByLabelText('Revision') as HTMLInputElement).value).toBe('Rev 1');
			expect((m.getByLabelText('Hack') as HTMLInputElement).checked).toBe(true);
			expect((m.getByLabelText('Translated') as HTMLInputElement).checked).toBe(false);
			expect(m.queryByLabelText('Disc')).toBeNull();
			expect(m.queryByLabelText('Version')).toBeNull();
			expect(m.getByText('File name unchanged')).toBeTruthy();
		});

		it('shows Version only for Update, and the new name preview', async () => {
			await open({ ...game([1]), fileSets: false });
			const m = editModal();

			await fireEvent.click(m.getByLabelText('Update'));
			expect(m.getByLabelText('Version')).toBeTruthy();
			await fireEvent.click(m.getByLabelText('Hack'));
			expect(m.getByText('Tetris 1 (Hack).gb', { selector: 'code' })).toBeTruthy();
		});

		it('on a disc system shows Disc and the keep-name hint instead of the preview', async () => {
			await open({ ...game([1]), fileSets: true, roms: [tagged({ flags: ['Disc 2'] })] });
			const m = editModal();

			expect((m.getByLabelText('Disc') as HTMLInputElement).value).toBe('2');
			expect(m.getByText('Disc releases keep their file name; only the tags change.')).toBeTruthy();
			expect(m.queryByText(/New file name/)).toBeNull();
		});

		it('sends only the changed fields; none closes without a request', async () => {
			await open({ ...game([1]), fileSets: false, roms: [tagged()] });
			await save();
			expect(calls.filter((c) => c.startsWith('PATCH'))).toEqual([]);

			await fireEvent.click(screen.getByRole('button', { name: 'Edit release Tetris 1.gb' }));
			await editModal().findByLabelText('Languages');
			answers['PATCH /api/games/g1/roms/1'] = [{ status: 200, body: { fileName: 'x.gb' } }];
			await fireEvent.click(editModal().getByLabelText('Mars'));
			await fireEvent.click(editModal().getByLabelText('World'));
			await fireEvent.input(editModal().getByLabelText('Languages'), {
				target: { value: 'En,,Fr ' }
			});
			await fireEvent.click(editModal().getByLabelText('Translated'));
			await save();

			await vi.waitFor(() =>
				expect(bodies['PATCH /api/games/g1/roms/1']).toEqual({
					regions: ['USA', 'World'],
					translated: true
				})
			);
		});

		it('shows field errors from a 400 under their fields', async () => {
			await open({ ...game([1]), fileSets: false });
			const message = "Must be 1–16 letters or '-' each.";
			answers['PATCH /api/games/g1/roms/1'] = [
				{
					status: 400,
					body: { status: 400, code: 'ValidationFailed', errors: { languages: [message] } }
				}
			];
			await fireEvent.input(editModal().getByLabelText('Languages'), { target: { value: 'E n' } });
			await save();

			expect((await editModal().findByText(message)).className).toBe('invalid-feedback');
		});

		it("on success toasts the server's name, reloads and switches to the release's new tab", async () => {
			toasts.items.length = 0;
			await open({ ...game([1]), fileSets: false });
			answers['PATCH /api/games/g1/roms/1'] = [
				{ status: 200, body: { fileName: 'Tetris 1 (2).gb' } }
			];
			await fireEvent.click(editModal().getByLabelText('DLC'));
			await save();

			await vi.waitFor(() =>
				expect(toasts.items.at(-1)?.text).toBe('Release updated: Tetris 1 (2).gb')
			);
			expect(bodies['PATCH /api/games/g1/roms/1']).toEqual({ kind: 'dlc' });
			await vi.waitFor(() =>
				expect(goto).toHaveBeenCalledWith('/games/g1?tab=dlc', expect.anything())
			);
			expect(calls.filter((c) => c === 'GET /api/games/g1')).toHaveLength(2);
		});
	});

	it('deleting a release calls the API and reloads', async () => {
		answers['GET /api/games/g1'] = [
			{ status: 200, body: game([1, 2]) },
			{ status: 200, body: game([1]) }
		];
		render(GamePage);

		await fireEvent.click(await releaseDelete('Tetris 2.gb'));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete', hidden: true }));

		await vi.waitFor(() => expect(calls).toContain('DELETE /api/games/g1/roms/2'));
		await vi.waitFor(() => expect(calls.filter((c) => c === 'GET /api/games/g1')).toHaveLength(2));
	});

	it('the release delete confirmation mentions the note only when the release has one (B12)', async () => {
		const detail = game([1, 2]);
		detail.roms[1].hasNote = true;
		answers['GET /api/games/g1'] = [{ status: 200, body: detail }];
		render(GamePage);

		await fireEvent.click(await releaseDelete('Tetris 2.gb'));
		expect(modal().getByText(/Delete Tetris 2\.gb and its note\?/)).toBeTruthy();

		await fireEvent.click(modal().getByRole('button', { name: 'Cancel', hidden: true }));
		await fireEvent.click(await releaseDelete('Tetris 1.gb'));
		await vi.waitFor(() => expect(modal().getByText(/Delete Tetris 1\.gb\?/)).toBeTruthy());
		expect(modal().queryByText(/and its note/)).toBeNull();
	});

	it("the release delete confirmation counts a disc set's other files; one without files is unchanged (B17)", async () => {
		const detail = game([1, 2]);
		detail.roms[1].files = [
			{
				id: 11,
				fileName: 'Disc.cue',
				sizeBytes: 1,
				crc32: '0',
				md5: '0',
				sha1: '0',
				downloadUrl: ''
			},
			{
				id: 12,
				fileName: 'Disc (Track 2).bin',
				sizeBytes: 1,
				crc32: '0',
				md5: '0',
				sha1: '0',
				downloadUrl: ''
			}
		];
		answers['GET /api/games/g1'] = [{ status: 200, body: detail }];
		render(GamePage);

		await fireEvent.click(await releaseDelete('Tetris 2.gb'));
		expect(modal().getByText(/The ROM and its 2 other files move to the system's/)).toBeTruthy();

		await fireEvent.click(modal().getByRole('button', { name: 'Cancel', hidden: true }));
		await fireEvent.click(await releaseDelete('Tetris 1.gb'));
		await vi.waitFor(() => expect(modal().getByText(/The ROM moves to the system's/)).toBeTruthy());
		expect(modal().queryByText(/other files/)).toBeNull();
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

		await fireEvent.click(await releaseDelete('Tetris 2.gb'));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete', hidden: true }));

		expect(await screen.findByText('This game changed, please retry.')).toBeTruthy();
		expect(await releaseDelete('Tetris 3.gb')).toBeTruthy();
	});

	it('the quick edit button in the information table opens Edit Metadata', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		const quick = await screen.findByRole('button', { name: 'Quick edit' });
		expect(
			within(screen.getByRole('table', { name: 'Game information' })).getByRole('button', {
				name: 'Quick edit'
			})
		).toBe(quick);
		await fireEvent.click(quick);
		expect(modal().getByRole('heading', { name: 'Edit Metadata', hidden: true })).toBeTruthy();
	});

	it('edit metadata validates before sending', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Edit Metadata/ }));
		await fireEvent.input(modal().getByLabelText('Release year'), { target: { value: '1800' } });
		await fireEvent.click(modal().getByRole('button', { name: 'Edit Metadata', hidden: true }));

		expect(modal().getByText('Leave empty or enter a year 1970–2100.')).toBeTruthy();
		expect(calls.some((c) => c.startsWith('PATCH'))).toBe(false);
	});

	it('edit metadata is a wide modal with a grid layout; fields still bound and sent', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		answers['PATCH /api/games/g1'] = [{ status: 200, body: { ...game([1]), title: 'Tetris DX' } }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Edit Metadata/ }));

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
		await fireEvent.click(modal().getByRole('button', { name: 'Edit Metadata', hidden: true }));

		await vi.waitFor(() => expect(calls).toContain('PATCH /api/games/g1'));
	});

	it('edit metadata: Enter in an input saves; a textarea needs Ctrl+Enter (M2, M3)', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		answers['PATCH /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);
		const patches = () => calls.filter((c) => c === 'PATCH /api/games/g1').length;

		await fireEvent.click(await screen.findByRole('button', { name: /Edit Metadata/ }));
		expect(
			modal().getByText('Press Enter to save (Ctrl+Enter in a multi-line field).')
		).toBeTruthy();
		await fireEvent.keyDown(modal().getByLabelText('Genres'), { key: 'Enter' });
		expect(patches()).toBe(0);
		await fireEvent.keyDown(modal().getByLabelText('Genres'), { key: 'Enter', ctrlKey: true });
		await vi.waitFor(() => expect(patches()).toBe(1));
	});

	it('edit metadata: Enter in Title saves (M2)', async () => {
		answers['GET /api/games/g1'] = [{ status: 200, body: game([1]) }];
		answers['PATCH /api/games/g1'] = [{ status: 200, body: game([1]) }];
		render(GamePage);

		await fireEvent.click(await screen.findByRole('button', { name: /Edit Metadata/ }));
		await fireEvent.keyDown(modal().getByLabelText('Title'), { key: 'Enter' });

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
