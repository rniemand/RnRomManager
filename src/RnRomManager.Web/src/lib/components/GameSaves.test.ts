import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { GameDetail, SaveItem } from '../api/types';
import { toasts } from '../stores/toasts.svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

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
	it('lists each save with its description, kind, release, size and who updated it', async () => {
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

		const rows = await screen.findAllByRole('row');
		const first = within(rows[1]);
		expect(first.getByText('Before boss')).toBeTruthy();
		expect(first.getByText(/line 1/)).toBeTruthy();
		expect(first.getByText('State')).toBeTruthy();
		expect(first.getByText('Tetris (World).gb')).toBeTruthy();
		expect(first.getByText('bob')).toBeTruthy();
		const orphan = within(rows[2]);
		expect(orphan.getByText('Battery')).toBeTruthy();
		expect(orphan.getByText('Release removed').closest('td')!.getAttribute('title')).toBe(
			'Gone (World).gb'
		);
		expect(first.getByRole('link', { name: 'Download Before boss' }).getAttribute('href')).toBe(
			`/api/games/g1/saves/${'a'.repeat(32)}/download`
		);
		for (const action of ['Edit', 'Replace', 'Delete'])
			expect(first.getByRole('button', { name: `${action} Before boss` })).toBeTruthy();
	});

	it('shows an empty state without saves', async () => {
		listed([]);
		render(GameSaves, { game });

		expect(await screen.findByText('No saves yet')).toBeTruthy();
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
						"This game's ROMs are being moved out of its ZIP. Try again when the storage migration job has finished."
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
			'being moved out of its ZIP'
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
		expect(writes().map((c) => c.key)).toEqual([`PUT /api/games/g1/saves/${save().id}/file`]);
	});
});
