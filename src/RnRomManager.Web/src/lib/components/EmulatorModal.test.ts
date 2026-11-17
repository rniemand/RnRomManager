import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import { auth } from '../stores/auth.svelte';
import { toasts } from '../stores/toasts.svelte';
import { flushSync } from 'svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

const { default: EmulatorModal } = await import('./EmulatorModal.svelte');

// C2: only id and file name, as a Library card passes them.
const rom = { id: 13, fileName: 'Orbit Blocks (World) (Rev 1).gb' };

describe('EmulatorModal', () => {
	it('frames the play page for the release', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false
		});

		const iframe = container.querySelector('iframe')!;
		expect(iframe.getAttribute('src')).toBe(
			'/emulator/play.html?game=g-1&rom=13&core=gambatte&name=Orbit%20Blocks%20(World)%20(Rev%201).gb'
		);
		expect(iframe.title).toBe('Emulator: Orbit Blocks (World) (Rev 1).gb');
		expect(iframe.getAttribute('allow')).toBe('fullscreen; gamepad; autoplay');
		expect(container.querySelector('.modal-dialog')!.classList).toContain('modal-xl');
	});

	it('removes the iframe when closed', async () => {
		const { container, rerender } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false
		});
		expect(container.querySelector('iframe')).not.toBeNull();

		(container.querySelector('.btn-close') as HTMLButtonElement).click();
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();

		await rerender({ open: true });
		expect(container.querySelector('iframe')).not.toBeNull();
	});

	it('renders no iframe without a core', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: null,
			rom,
			disc: false
		});
		expect(container.querySelector('iframe')).toBeNull();
	});

	it('adds disc=1 for a disc release (Sega CD B6)', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-2',
			core: 'genesis_plus_gx',
			rom: { id: 14, fileName: 'Disc Comet (USA).cue' },
			disc: true
		});

		expect(container.querySelector('iframe')!.getAttribute('src')).toMatch(/&disc=1$/);
	});
});

describe('EmulatorModal 3DO saves (3DO B11)', () => {
	afterEach(() => vi.useRealTimers());

	const close = { type: 'rnrm-emulator-close' };

	function openFor(core: string) {
		vi.useFakeTimers();
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-3',
			core,
			rom: { id: 15, fileName: 'Opal Rally (USA).cue' },
			disc: true
		});
		const iframe = container.querySelector('iframe')!;
		const posted = vi.spyOn(iframe.contentWindow!, 'postMessage').mockImplementation(() => {});
		return { container, iframe, posted };
	}

	function reply(source: Window | null, data: unknown = { type: 'rnrm-emulator-closed' }) {
		window.dispatchEvent(new MessageEvent('message', { data, origin: location.origin, source }));
	}

	async function closeButton(container: HTMLElement) {
		(container.querySelector('.btn-close') as HTMLButtonElement).click();
		flushSync();
	}

	it('asks the player to save and closes on its reply', async () => {
		const { container, iframe, posted } = openFor('opera');

		await closeButton(container);
		expect(posted).toHaveBeenCalledWith(close, location.origin);
		expect(container.querySelector('iframe')).not.toBeNull();

		reply(window, { type: 'rnrm-emulator-closed' });
		await vi.advanceTimersByTimeAsync(0);
		flushSync();
		expect(container.querySelector('iframe')).not.toBeNull();

		reply(iframe.contentWindow);
		await vi.advanceTimersByTimeAsync(0);
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();
	});

	it('closes after 3 s without a reply', async () => {
		const { container } = openFor('opera');

		await closeButton(container);
		await vi.advanceTimersByTimeAsync(2999);
		flushSync();
		expect(container.querySelector('iframe')).not.toBeNull();
		await vi.advanceTimersByTimeAsync(1);
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();
	});

	it('closes at once and posts nothing for another core', async () => {
		const { container, posted } = openFor('snes9x');

		await closeButton(container);

		expect(posted).not.toHaveBeenCalled();
		expect(container.querySelector('iframe')).toBeNull();
	});
});

describe('EmulatorModal save to library (Saves B18, B19)', () => {
	type Reply = { status: number; body?: unknown };
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Reply>;

	beforeEach(() => {
		resetClient();
		toasts.items.length = 0;
		calls = [];
		answers = { 'GET /api/games/g-1/saves': { status: 200, body: { items: [] } } };
		auth.set({ id: 1, username: 'alice', roles: ['admin'], mustChangePassword: false });
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body });
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	function openFor(core: string, onsaved?: () => void) {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core,
			rom,
			disc: false,
			onsaved
		});
		const iframe = container.querySelector('iframe')!;
		const posted = vi.spyOn(iframe.contentWindow!, 'postMessage').mockImplementation(() => {});
		return { container, iframe, posted };
	}

	const button = (container: HTMLElement, name: string) =>
		within(container).queryByRole('button', { name, hidden: true }) as HTMLButtonElement | null;

	function reply(source: Window | null, data: unknown) {
		window.dispatchEvent(new MessageEvent('message', { data, origin: location.origin, source }));
	}

	const uploads = () => calls.filter((c) => c.key === 'POST /api/games/g-1/saves');

	it.each(['gambatte', 'mgba', 'fceumm', 'snes9x', 'pcsx_rearmed', 'genesis_plus_gx', 'yabause'])(
		'%s offers both buttons',
		(core) => {
			const { container } = openFor(core);
			expect(button(container, 'Save state to library')).not.toBeNull();
			expect(button(container, 'Save battery to library')).not.toBeNull();
		}
	);

	it.each(['desmume', 'opera'])('%s offers only the state button', (core) => {
		const { container } = openFor(core);
		expect(button(container, 'Save state to library')).not.toBeNull();
		expect(button(container, 'Save battery to library')).toBeNull();
	});

	it('asks the player, uploads the bytes with a dated name, toasts and calls onsaved', async () => {
		const onsaved = vi.fn();
		answers['POST /api/games/g-1/saves'] = { status: 201, body: {} };
		const { container, iframe, posted } = openFor('gambatte', onsaved);

		await fireEvent.click(button(container, 'Save battery to library')!);
		expect(posted).toHaveBeenCalledWith(
			{ type: 'rnrm-emulator-export', kind: 'battery' },
			location.origin
		);
		expect(button(container, 'Save state to library')!.disabled).toBe(true);
		expect(button(container, 'Save battery to library')!.disabled).toBe(true);

		reply(iframe.contentWindow, {
			type: 'rnrm-emulator-export-result',
			kind: 'battery',
			ok: true,
			bytes: new Uint8Array([1, 2, 3]).buffer,
			extension: '.srm'
		});

		await vi.waitFor(() => expect(onsaved).toHaveBeenCalledTimes(1));
		const form = uploads()[0].body as FormData;
		expect([form.get('romId'), form.get('kind')]).toEqual(['13', 'battery']);
		expect(form.get('name')).toMatch(/^Battery \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
		expect(form.has('description')).toBe(false);
		expect((form.get('file') as File).name).toBe('save.srm');
		expect((form.get('file') as File).size).toBe(3);
		expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([['Saved to library', 'success']]);
		expect(button(container, 'Save state to library')!.disabled).toBe(false);
	});

	it("overwrites the user's newest save of the same kind for the release instead of adding one", async () => {
		const onsaved = vi.fn();
		// S5: ownership is matched ignoring case.
		const save = (id: string, kind: string, releaseFileName: string) => ({
			id,
			kind,
			releaseFileName,
			uploadedBy: 'ALICE'
		});
		answers['GET /api/games/g-1/saves'] = {
			status: 200,
			body: {
				items: [
					save('other-release', 'state', 'Other.gb'),
					save('battery', 'battery', rom.fileName),
					save('newest', 'state', rom.fileName),
					save('older', 'state', rom.fileName)
				]
			}
		};
		answers['PUT /api/games/g-1/saves/newest/file'] = { status: 200, body: {} };
		const { container, iframe } = openFor('gambatte', onsaved);

		await fireEvent.click(button(container, 'Save state to library')!);
		reply(iframe.contentWindow, {
			type: 'rnrm-emulator-export-result',
			kind: 'state',
			ok: true,
			bytes: new Uint8Array([1, 2]).buffer,
			extension: '.state'
		});

		await vi.waitFor(() => expect(onsaved).toHaveBeenCalledTimes(1));
		expect(uploads()).toEqual([]);
		const put = calls.find((c) => c.key === 'PUT /api/games/g-1/saves/newest/file')!;
		expect(((put.body as FormData).get('file') as File).size).toBe(2);
	});

	it("adds a new save rather than replace another user's newer save of that kind (S5)", async () => {
		const onsaved = vi.fn();
		answers['GET /api/games/g-1/saves'] = {
			status: 200,
			body: {
				items: [
					{ id: 'bobs', kind: 'state', releaseFileName: rom.fileName, uploadedBy: 'bob' },
					{ id: 'unowned', kind: 'state', releaseFileName: rom.fileName, uploadedBy: null }
				]
			}
		};
		answers['POST /api/games/g-1/saves'] = { status: 201, body: {} };
		const { container, iframe } = openFor('gambatte', onsaved);

		await fireEvent.click(button(container, 'Save state to library')!);
		reply(iframe.contentWindow, {
			type: 'rnrm-emulator-export-result',
			kind: 'state',
			ok: true,
			bytes: new Uint8Array([1]).buffer,
			extension: '.state'
		});

		await vi.waitFor(() => expect(onsaved).toHaveBeenCalledTimes(1));
		expect(uploads()).toHaveLength(1);
		expect(calls.some((c) => c.key.startsWith('PUT '))).toBe(false);
	});

	it('refuses a save over 256 MiB without uploading', async () => {
		const { container, iframe } = openFor('gambatte');
		const huge = Object.create(ArrayBuffer.prototype, {
			byteLength: { value: 256 * 1024 * 1024 + 1 }
		});

		await fireEvent.click(button(container, 'Save state to library')!);
		reply(iframe.contentWindow, {
			type: 'rnrm-emulator-export-result',
			kind: 'state',
			ok: true,
			bytes: huge,
			extension: '.state'
		});

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toEqual(['This save is larger than 256 MiB'])
		);
		expect(uploads()).toEqual([]);
	});

	it.each([
		['not-started', 'Start the game first.'],
		['busy', 'The emulator is saving. Try again in a moment.'],
		['unsupported', "This emulator can't save states."],
		['empty', 'There is no save to copy yet.'],
		['error', "Couldn't read the save from the emulator."]
	])('toasts the reason %s and uploads nothing', async (reason, text) => {
		const { container, iframe } = openFor('gambatte');

		await fireEvent.click(button(container, 'Save state to library')!);
		reply(iframe.contentWindow, {
			type: 'rnrm-emulator-export-result',
			kind: 'state',
			ok: false,
			reason
		});

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([[text, 'warning']])
		);
		expect(uploads()).toEqual([]);
	});

	it('a JobRunning answer to the upload is the job-running toast', async () => {
		answers['POST /api/games/g-1/saves'] = {
			status: 409,
			body: { code: 'JobRunning', title: 'Another job is running' }
		};
		const onsaved = vi.fn();
		const { container, iframe } = openFor('gambatte', onsaved);

		await fireEvent.click(button(container, 'Save state to library')!);
		reply(iframe.contentWindow, {
			type: 'rnrm-emulator-export-result',
			kind: 'state',
			ok: true,
			bytes: new Uint8Array([1]).buffer,
			extension: '.state'
		});

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toEqual([
				'Another job is running. Try again when it has finished.'
			])
		);
		expect(onsaved).not.toHaveBeenCalled();
	});

	it('ignores a result from another source', async () => {
		const { container } = openFor('gambatte');

		await fireEvent.click(button(container, 'Save state to library')!);
		reply(window, {
			type: 'rnrm-emulator-export-result',
			kind: 'state',
			ok: false,
			reason: 'empty'
		});
		await new Promise((r) => setTimeout(r, 20));

		expect(toasts.items).toEqual([]);
		expect(button(container, 'Save state to library')!.disabled).toBe(true);
	});

	it('passes a library save into the play URL (Saves B17)', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false,
			save: { saveId: 'a'.repeat(32), saveKind: 'state' }
		});

		expect(container.querySelector('iframe')!.getAttribute('src')).toMatch(
			new RegExp(`&save=${'a'.repeat(32)}&saveKind=state$`)
		);
	});
});
