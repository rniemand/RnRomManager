import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { GameDetail, SaveItem } from '#lib/types/index.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));

const { default: GameSaves } = await import('./GameSaves.svelte');

const game = {
	id: 'g1',
	displayTitle: 'Tetris',
	roms: [
		{ id: 1, fileName: 'Tetris (World).gb' },
		{ id: 2, fileName: 'Tetris (Japan).gb' }
	]
} as unknown as GameDetail;

const save = (extra: Partial<SaveItem> = {}): SaveItem => ({
	id: 'a'.repeat(32),
	name: 'Before boss',
	description: '',
	kind: 'state',
	releaseFileName: 'Tetris (World).gb',
	romId: 1,
	sizeBytes: 2048,
	extension: '.state',
	uploadedBy: 'alice',
	uploadedAt: '2026-11-16T10:00:00Z',
	updatedBy: 'bob',
	updatedAt: '2026-11-16T11:00:00Z',
	...extra
});

type Reply = { status: number; body?: unknown };
let calls: { key: string; body: unknown }[];
let answers: Record<string, Reply[]>;
const list = 'GET /api/games/g1/saves';

beforeEach(() => {
	resetClient();
	toasts.items.length = 0;
	auth.set({ id: 2, username: 'alice', roles: [], mustChangePassword: false });
	calls = [];
	answers = {};
	vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
		const key = `${init?.method ?? 'GET'} ${url}`;
		calls.push({ key, body: init?.body });
		const queue = answers[key];
		const answer = (queue && queue.length > 1 ? queue.shift() : queue?.[0]) ?? { status: 204 };
		const payload = answer.body === undefined ? null : JSON.stringify(answer.body);
		return Promise.resolve(new Response(payload, { status: answer.status }));
	});
});
afterEach(() => vi.unstubAllGlobals());

const modal = () => within(document.querySelector<HTMLElement>('.modal')!);
const writes = () => calls.filter((c) => !c.key.startsWith('GET'));
const listed = (items: SaveItem[]) => (answers[list] = [{ status: 200, body: { items } }]);

async function choose(file: File): Promise<void> {
	const input = modal().getByLabelText('File', { selector: 'input' }) as HTMLInputElement;
	Object.defineProperty(input, 'files', { value: [file], configurable: true });
	await fireEvent.change(input);
}

describe('GameSaves', () => {
	it('groups saves into a panel per release, then Release removed; no Release column (S2, S3)', async () => {
		listed([
			save({ description: 'line 1\nline 2' }),
			save({
				id: 'b'.repeat(32),
				name: 'Old',
				kind: 'battery',
				romId: null,
				releaseFileName: 'Gone (World).gb'
			})
		]);
		render(GameSaves, { game });

		const world = await screen.findByRole('heading', { level: 3, name: /^Tetris \(World\)\.gb/ });
		expect(world.id).toBe('saves-rom-1-heading');
		expect(world.textContent!.replace(/\s+/g, ' ').trim()).toBe('Tetris (World).gb 1');
		expect(world.querySelector('.badge.text-bg-secondary.rounded-pill')).toBeTruthy();
		const removed = screen.getByRole('heading', { level: 3, name: /^Release removed/ });
		expect(removed.id).toBe('saves-removed-heading');
		// Panels start open; Japan has no saves, so no panel.
		expect(document.getElementById('saves-rom-1')!.classList).toContain('show');
		expect(document.getElementById('saves-removed')!.classList).toContain('show');
		expect(screen.queryByRole('heading', { level: 3, name: /Japan/ })).toBeNull();
		expect(screen.queryByRole('columnheader', { name: 'Release' })).toBeNull();

		const panel = within(document.getElementById('saves-rom-1')!);
		expect(panel.getByText('Before boss')).toBeTruthy();
		expect(panel.getByText(/line 1/)).toBeTruthy();
		expect(panel.getByText('State')).toBeTruthy();
		expect(panel.getByText('bob')).toBeTruthy();
		expect(panel.getByText('2.0 KiB').closest('.badge')!.className).toBe(
			'badge text-bg-light border'
		);
		const orphan = within(document.getElementById('saves-removed')!);
		expect(orphan.getByText('Battery')).toBeTruthy();
		const download = panel.getByRole('link', { name: 'Download Before boss' });
		expect(download.getAttribute('href')).toBe(`/api/games/g1/saves/${'a'.repeat(32)}/download`);
		expect(download.className).toBe('btn btn-sm btn-link text-body-secondary p-0');
		for (const action of ['Edit', 'Replace', 'Delete'])
			expect(panel.getByRole('button', { name: `${action} Before boss` })).toBeTruthy();
		expect(panel.getByRole('button', { name: 'Delete Before boss' }).className).toContain(
			'text-danger-emphasis'
		);
	});

	it('orders panels as game.roms and each opens and closes on its own (S2)', async () => {
		listed([save({ romId: 2, name: 'J' }), save({ id: 'b'.repeat(32), name: 'W', romId: 1 })]);
		render(GameSaves, { game });

		await screen.findByText('J');
		const headings = screen.getAllByRole('heading', { level: 3 }).map((h) => h.id);
		expect(headings).toEqual(['saves-rom-1-heading', 'saves-rom-2-heading']);
		await fireEvent.click(screen.getByRole('button', { name: /^Tetris \(World\)\.gb/ }));
		expect(document.getElementById('saves-rom-1')!.classList).not.toContain('show');
		expect(document.getElementById('saves-rom-2')!.classList).toContain('show');
	});
	it('shows no Owner column to a Site user (S8)', async () => {
		listed([save(), save({ id: 'b'.repeat(32), name: 'Old', uploadedBy: null })]);
		render(GameSaves, { game });
		await screen.findAllByRole('row');
		expect(screen.queryByRole('columnheader', { name: 'Owner' })).toBeNull();
	});

	it('shows the Owner column to an Admin', async () => {
		auth.set({ id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false });
		listed([save(), save({ id: 'b'.repeat(32), name: 'Old', uploadedBy: null })]);
		render(GameSaves, { game });

		const rows = await screen.findAllByRole('row');
		expect(screen.getByRole('columnheader', { name: 'Owner' })).toBeTruthy();
		expect(within(rows[1]).getByText('alice')).toBeTruthy();
		expect(within(rows[2]).getByText('Unknown')).toBeTruthy();
	});

	it('shows an empty state without saves', async () => {
		listed([]);
		render(GameSaves, { game });

		const empty = await screen.findByText('No saves yet');
		expect(empty.closest('section')!.getAttribute('aria-labelledby')).toBe('saves-heading');
		expect(document.querySelector('.accordion')).toBeNull();
	});

	it('has a heading row with Saves and Add save, then the panels; no card (S2a)', async () => {
		listed([save()]);
		render(GameSaves, { game });

		const table = (await screen.findByRole('table')) as HTMLElement;
		const heading = screen.getByRole('heading', { level: 2, name: 'Saves' });
		expect(heading.className).toBe('h5 mb-0 me-auto');
		const row = heading.parentElement!;
		expect(row.className).toBe('d-flex align-items-center gap-2 mb-3');
		const section = row.parentElement!;
		expect(section.tagName).toBe('SECTION');
		expect(section.className).toBe('');
		expect(section.getAttribute('aria-labelledby')).toBe('saves-heading');
		const add = screen.getByRole('button', { name: 'Add save' });
		expect(add.parentElement).toBe(row);
		expect(add.className).toBe('btn btn-sm btn-primary');
		expect(table.parentElement!.className).toBe('table-responsive');
		expect(table.parentElement!.parentElement!.className).toBe('accordion-body p-0');
		expect(document.querySelector('.card')).toBeNull();
	});

	describe('drop on a release panel (S4)', () => {
		class FakeDataTransfer {
			private list: File[] = [];
			items = { add: (f: File) => void this.list.push(f) };
			get files() {
				return this.list;
			}
		}
		const drop = (target: HTMLElement, files: File[]) =>
			fireEvent.drop(target, {
				dataTransfer: { types: ['Files'], files } as unknown as DataTransfer
			});
		/** jsdom only takes a real FileList on `files`; capture what the modal puts there. */
		let picked: unknown;
		const filesDescriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'files')!;
		beforeEach(() => {
			picked = undefined;
			vi.stubGlobal('DataTransfer', FakeDataTransfer);
			Object.defineProperty(HTMLInputElement.prototype, 'files', {
				configurable: true,
				get: () => picked,
				set: (v) => (picked = v)
			});
		});
		afterEach(() => Object.defineProperty(HTMLInputElement.prototype, 'files', filesDescriptor));
		const zoneOf = (headingName: RegExp) =>
			screen.getByRole('heading', { level: 3, name: headingName }).closest('.accordion')!
				.parentElement!;

		it('one file opens Add save fixed to that release, with the file and its name', async () => {
			listed([save({ romId: 2, name: 'J' })]);
			render(GameSaves, { game });
			await screen.findByText('J');
			expect(screen.getByText('Drop a save file here to add it to this release')).toBeTruthy();

			const file = new File(['x'], 'Tetris (Japan).state.sav', { type: '' });
			// Dropped on the closed panel's header still counts.
			await fireEvent.click(screen.getByRole('button', { name: /^Tetris \(Japan\)\.gb/ }));
			await drop(zoneOf(/^Tetris \(Japan\)\.gb/), [file]);

			await vi.waitFor(() =>
				expect(document.querySelector('.modal-title')!.textContent).toBe('Add save')
			);
			const release = modal().getByLabelText('Release') as HTMLSelectElement;
			expect(release.value).toBe('2');
			expect(release.disabled).toBe(true);
			expect((modal().getByLabelText('Name') as HTMLInputElement).value).toBe(
				'Tetris (Japan).state'
			);
			expect(picked).toEqual([file]);

			answers['POST /api/games/g1/saves'] = [{ status: 201, body: save() }];
			await fireEvent.click(modal().getByRole('button', { name: 'Add save', hidden: true }));
			await vi.waitFor(() => expect(writes()).toHaveLength(1));
			const form = writes()[0].body as FormData;
			expect(form.get('romId')).toBe('2');
			expect((form.get('file') as File).name).toBe('Tetris (Japan).state.sav');
		});

		it('two files toast and open nothing; Release removed has no drop zone', async () => {
			listed([save(), save({ id: 'b'.repeat(32), name: 'Old', romId: null })]);
			render(GameSaves, { game });
			await screen.findByText('Old');

			await drop(zoneOf(/^Tetris \(World\)\.gb/), [
				new File(['a'], 'a.sav'),
				new File(['b'], 'b.sav')
			]);
			expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([
				['Drop one save file at a time.', 'warning']
			]);
			expect(document.querySelector('.modal-title')!.textContent).toBe('');
			expect(screen.getAllByText('Drop a save file here to add it to this release')).toHaveLength(
				1
			);
		});

		it('the Add save button opens an unpreset modal after a drop', async () => {
			listed([save({ romId: 2, name: 'J' })]);
			render(GameSaves, { game });
			await screen.findByText('J');
			await drop(zoneOf(/^Tetris \(Japan\)\.gb/), [new File(['x'], 'x.sav')]);
			await vi.waitFor(() =>
				expect((modal().getByLabelText('Release') as HTMLSelectElement).disabled).toBe(true)
			);

			await fireEvent.click(modal().getByRole('button', { name: 'Cancel', hidden: true }));
			await fireEvent.click(screen.getByRole('button', { name: 'Add save' }));
			await vi.waitFor(() =>
				expect((modal().getByLabelText('Release') as HTMLSelectElement).disabled).toBe(false)
			);
			expect((modal().getByLabelText('Release') as HTMLSelectElement).value).toBe('1');
			expect((modal().getByLabelText('Name') as HTMLInputElement).value).toBe('');
		});
	});
	it('adds a save with the text fields before the file, then reloads the list', async () => {
		listed([]);
		answers['POST /api/games/g1/saves'] = [{ status: 201, body: save() }];
		render(GameSaves, { game });
		await screen.findByText('No saves yet');

		await fireEvent.click(screen.getByRole('button', { name: 'Add save' }));
		await fireEvent.change(modal().getByLabelText('Release'), { target: { value: '2' } });
		await fireEvent.click(modal().getByLabelText('Battery'));
		await fireEvent.input(modal().getByLabelText('Name'), { target: { value: '  Gym 8 ' } });
		await fireEvent.input(modal().getByLabelText('Description'), { target: { value: 'notes' } });
		await choose(new File([new Uint8Array([1, 2])], 'GOLD.SRM'));
		await fireEvent.click(modal().getByRole('button', { name: 'Add save', hidden: true }));

		await vi.waitFor(() => expect(calls.filter((c) => c.key === list)).toHaveLength(2));
		const body = writes().find((c) => c.key === 'POST /api/games/g1/saves')!.body as FormData;
		expect([...body.keys()]).toEqual(['romId', 'kind', 'name', 'description', 'file']);
		expect([body.get('romId'), body.get('kind'), body.get('name')]).toEqual([
			'2',
			'battery',
			'Gym 8'
		]);
		expect((body.get('file') as File).name).toBe('GOLD.SRM');
	});

	it('explains under the Kind radios that .sav/.srm files upload as Battery (release editing B21)', async () => {
		listed([]);
		render(GameSaves, { game });
		await screen.findByText('No saves yet');

		await fireEvent.click(screen.getByRole('button', { name: 'Add save' }));

		const help = modal().getByText(
			'Battery saves from other emulators (.sav, .srm) can be uploaded as Battery.'
		);
		const fieldset = help.closest('fieldset')!;
		expect(help.className).toBe('form-text');
		expect(fieldset.lastElementChild).toBe(help);
		expect(fieldset.getAttribute('aria-describedby')).toBe(help.id);
		expect(help.id).toMatch(/-kind-help$/);
	});

	it('checks the form before sending: a name and a file are required', async () => {
		listed([]);
		render(GameSaves, { game });
		await screen.findByText('No saves yet');

		await fireEvent.click(screen.getByRole('button', { name: 'Add save' }));
		await fireEvent.click(modal().getByRole('button', { name: 'Add save', hidden: true }));

		expect(modal().getByText('Must be 1–100 characters.')).toBeTruthy();
		expect(modal().getByText('Choose a save file.')).toBeTruthy();
		expect(writes().filter((c) => c.key.includes('/saves'))).toEqual([]);
	});

	it('edits the name and description', async () => {
		listed([save()]);
		answers[`PATCH /api/games/g1/saves/${'a'.repeat(32)}`] = [{ status: 200, body: save() }];
		render(GameSaves, { game });

		await fireEvent.click(await screen.findByRole('button', { name: 'Edit Before boss' }));
		expect(modal().queryByLabelText('File', { selector: 'input' })).toBeNull();
		await fireEvent.input(modal().getByLabelText('Name'), { target: { value: 'After boss' } });
		await fireEvent.click(modal().getByRole('button', { name: 'Save', hidden: true }));

		await vi.waitFor(() =>
			expect(JSON.parse(writes().find((c) => c.key.startsWith('PATCH'))!.body as string)).toEqual({
				name: 'After boss',
				description: ''
			})
		);
	});

	it('replaces the file, and deletes after a confirmation naming the save', async () => {
		listed([save()]);
		render(GameSaves, { game });

		await fireEvent.click(await screen.findByRole('button', { name: 'Replace Before boss' }));
		expect(modal().queryByLabelText('Name')).toBeNull();
		await choose(new File([new Uint8Array([9])], 'new.state'));
		await fireEvent.click(modal().getByRole('button', { name: 'Replace', hidden: true }));
		await vi.waitFor(() =>
			expect(writes().map((c) => c.key)).toContain(`PUT /api/games/g1/saves/${'a'.repeat(32)}/file`)
		);

		await fireEvent.click(screen.getByRole('button', { name: 'Delete Before boss' }));
		expect(modal().getByText('Before boss', { selector: 'strong' })).toBeTruthy();
		await fireEvent.click(modal().getByRole('button', { name: 'Delete save', hidden: true }));
		await vi.waitFor(() =>
			expect(writes().map((c) => c.key)).toContain(`DELETE /api/games/g1/saves/${'a'.repeat(32)}`)
		);
	});

	it('a JobRunning answer is a toast; another error stays in the modal with its title', async () => {
		listed([save()]);
		const url = `DELETE /api/games/g1/saves/${'a'.repeat(32)}`;
		answers[url] = [
			{ status: 409, body: { code: 'JobRunning', title: 'Another job is running' } },
			{
				status: 409,
				body: {
					code: 'StorageMigrationPending',
					title:
						"This game's ROMs are being moved by the storage job. Try again when it has finished."
				}
			}
		];
		render(GameSaves, { game });

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Before boss' }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete save', hidden: true }));
		await vi.waitFor(() => expect(toasts.items.map((t) => t.kind)).toEqual(['warning']));
		expect(modal().queryByRole('alert', { hidden: true })).toBeNull();

		await fireEvent.click(screen.getByRole('button', { name: 'Delete Before boss' }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete save', hidden: true }));
		expect((await modal().findByRole('alert', { hidden: true })).textContent).toContain(
			'being moved by the storage job'
		);
	});

	it('tells the page after each change, so it reloads the renumbered releases', async () => {
		listed([save()]);
		const onchanged = vi.fn();
		render(GameSaves, { game, onchanged });

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Before boss' }));
		await fireEvent.click(modal().getByRole('button', { name: 'Delete save', hidden: true }));

		await vi.waitFor(() => expect(onchanged).toHaveBeenCalledTimes(1));
	});

	it('reloads the list when version changes', async () => {
		listed([]);
		const view = render(GameSaves, { game, version: 0 });
		await screen.findByText('No saves yet');

		await view.rerender({ game, version: 1 });

		await vi.waitFor(() => expect(calls.filter((c) => c.key === list)).toHaveLength(2));
	});
});

describe('GameSaves Play (B16, B19)', () => {
	const id = 'a'.repeat(32);
	const playable = (core: string | null, externalFileName: string | null = null) =>
		({
			...game,
			browserCore: core,
			roms: game.roms.map((r) => ({ ...r, externalFileName }))
		}) as unknown as GameDetail;
	const iframe = () => document.querySelector('iframe');

	it('shows Play only for a save of a release the modal can play', async () => {
		listed([save(), save({ id: 'b'.repeat(32), name: 'Orphan', romId: null })]);
		render(GameSaves, { game: playable('gambatte') });

		expect(await screen.findByRole('button', { name: 'Play Before boss' })).toBeTruthy();
		expect(screen.queryByRole('button', { name: 'Play Orphan' })).toBeNull();
	});

	it.each([
		['no core', null],
		['the PSP tab player', 'ppsspp']
	])('hides Play with %s', async (_, core) => {
		listed([save()]);
		render(GameSaves, { game: playable(core, core ? 'x.iso' : null) });

		await screen.findByRole('button', { name: 'Edit Before boss' });
		expect(screen.queryByRole('button', { name: 'Play Before boss' })).toBeNull();
	});

	it.each(['desmume', 'opera'])(
		'hides Play for a battery save on %s, not for a state',
		async (core) => {
			listed([
				save({ kind: 'battery', name: 'Battery' }),
				save({ id: 'b'.repeat(32), name: 'State' })
			]);
			render(GameSaves, { game: playable(core, 'x.nds') });

			expect(await screen.findByRole('button', { name: 'Play State' })).toBeTruthy();
			expect(screen.queryByRole('button', { name: 'Play Battery' })).toBeNull();
		}
	);

	it('plays a state at once, with the save in the URL and no disc=1 for an external DS release', async () => {
		listed([save()]);
		render(GameSaves, { game: playable('desmume', 'Tetris.(World).nds') });

		await fireEvent.click(await screen.findByRole('button', { name: 'Play Before boss' }));

		expect(iframe()!.getAttribute('src')).toBe(
			`/emulator/play.html?game=g1&rom=1&core=desmume&name=Tetris%20(World).gb&save=${id}&saveKind=state`
		);
	});

	it('asks before loading a battery save', async () => {
		listed([save({ kind: 'battery' })]);
		render(GameSaves, { game: playable('gambatte') });

		await fireEvent.click(await screen.findByRole('button', { name: 'Play Before boss' }));
		expect(iframe()).toBeNull();
		const confirm = within(
			[...document.querySelectorAll<HTMLElement>('.modal')].find((m) =>
				m.textContent?.includes('replaces the in-browser save')
			)!
		);
		await fireEvent.click(confirm.getByRole('button', { name: 'Play', hidden: true }));

		expect(iframe()!.getAttribute('src')).toMatch(new RegExp(`&save=${id}&saveKind=battery$`));
	});

	it('reloads the list after its own Play dialog saved to the library', async () => {
		listed([save()]);
		answers[`PUT /api/games/g1/saves/${save().id}/file`] = [{ status: 200, body: save() }];
		render(GameSaves, { game: playable('gambatte') });
		await fireEvent.click(await screen.findByRole('button', { name: 'Play Before boss' }));
		const frame = iframe()!;
		vi.spyOn(frame.contentWindow!, 'postMessage').mockImplementation(() => {});

		await fireEvent.click(
			screen.getByRole('button', { name: 'Save state to library', hidden: true })
		);
		window.dispatchEvent(
			new MessageEvent('message', {
				data: {
					type: 'rnrm-emulator-export-result',
					kind: 'state',
					ok: true,
					bytes: new Uint8Array([1]).buffer,
					extension: '.state'
				},
				origin: location.origin,
				source: frame.contentWindow
			})
		);

		// The first list, the overwrite lookup, then the reload.
		await vi.waitFor(() => expect(calls.filter((c) => c.key === list)).toHaveLength(3));
		// B22: opening the Play dialog also recorded one play.
		expect(writes().map((c) => c.key)).toEqual([
			expect.stringMatching(/^POST \/api\/games\/g1\/roms\/\d+\/played$/),
			`PUT /api/games/g1/saves/${save().id}/file`
		]);
	});
});
