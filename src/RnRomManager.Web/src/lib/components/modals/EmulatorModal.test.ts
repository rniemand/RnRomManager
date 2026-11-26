import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, within } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { flushSync } from 'svelte';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
// Release editing B25: jsdom has no canvas, so the trim is stubbed; by default nothing is trimmed (B27's fallback).
const trimScreenshot = vi.fn<(png: ArrayBuffer) => Promise<ArrayBuffer | null>>();
vi.mock('#lib/emulator.js', async (original) => ({
	...(await original<typeof import('#lib/emulator.js')>()),
	trimScreenshot: (png: ArrayBuffer) => trimScreenshot(png)
}));
beforeEach(() => trimScreenshot.mockReset().mockResolvedValue(null));

const { default: EmulatorModal } = await import('./EmulatorModal.svelte');

// C2: only id and file name, as a Library card passes them.
const rom = { id: 13, fileName: 'Orbit Blocks (World) (Rev 1).gb' };

/** Release editing B22: a battery-export core shows the iframe once its saves lookup has settled. */
async function frameOf(container: HTMLElement): Promise<HTMLIFrameElement> {
	await vi.waitFor(() => expect(container.querySelector('iframe')).not.toBeNull());
	return container.querySelector('iframe')!;
}

/** Answers the player's export request for `kind` from the iframe. */
function answerExport(iframe: HTMLIFrameElement, kind: string, result: Record<string, unknown>) {
	window.dispatchEvent(
		new MessageEvent('message', {
			data: { type: 'rnrm-emulator-export-result', kind, ...result },
			origin: location.origin,
			source: iframe.contentWindow
		})
	);
}

describe('EmulatorModal', () => {
	beforeEach(() => {
		resetClient();
		vi.stubGlobal('fetch', () =>
			Promise.resolve(new Response(JSON.stringify({ items: [] }), { status: 200 }))
		);
	});
	afterEach(() => vi.unstubAllGlobals());

	it('frames the play page for the release', async () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false
		});

		const iframe = await frameOf(container);
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
		const iframe = await frameOf(container);

		(container.querySelector('.btn-close') as HTMLButtonElement).click();
		// B23: the close asks for the battery save first; a game that never started has none.
		answerExport(iframe, 'battery', { ok: false, reason: 'not-started' });
		await vi.waitFor(() => expect(container.querySelector('iframe')).toBeNull());

		await rerender({ open: true });
		await frameOf(container);
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

	it('adds disc=1 for a disc release (Sega CD B6)', async () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-2',
			core: 'genesis_plus_gx',
			rom: { id: 14, fileName: 'Disc Comet (USA).cue' },
			disc: true
		});

		expect((await frameOf(container)).getAttribute('src')).toMatch(/&disc=1$/);
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
		// DeSmuME has neither Opera's close nor a battery export (release editing B23).
		const { container, posted } = openFor('desmume');

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

	async function openFor(core: string, onsaved?: () => void) {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core,
			rom,
			disc: false,
			onsaved
		});
		const iframe = await frameOf(container);
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
		async (core) => {
			const { container } = await openFor(core);
			expect(button(container, 'Save state to library')).not.toBeNull();
			expect(button(container, 'Save battery to library')).not.toBeNull();
		}
	);

	it("the save and screenshot buttons sit in the dialog's footer; the body holds only the frame (U3)", async () => {
		const { container } = await openFor('gambatte');
		for (const name of ['Save state to library', 'Save battery to library', 'Screenshot'])
			expect(button(container, name)!.closest('.modal-footer')).not.toBeNull();
		const body = container.querySelector('.modal-body')!;
		expect(body.querySelectorAll('button')).toHaveLength(0);
		expect([...body.children].map((c) => c.className)).toEqual([
			'ratio ratio-4x3 mx-auto emulator-screen'
		]);
	});

	it.each(['desmume', 'opera'])('%s offers only the state button', async (core) => {
		const { container } = await openFor(core);
		expect(button(container, 'Save state to library')).not.toBeNull();
		expect(button(container, 'Save battery to library')).toBeNull();
	});

	it('asks the player, uploads the bytes with a dated name, toasts and calls onsaved', async () => {
		const onsaved = vi.fn();
		answers['POST /api/games/g-1/saves'] = { status: 201, body: {} };
		const { container, iframe, posted } = await openFor('gambatte', onsaved);

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
		const { container, iframe } = await openFor('gambatte', onsaved);

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
		const { container, iframe } = await openFor('gambatte', onsaved);

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
		const { container, iframe } = await openFor('gambatte');
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
		const { container, iframe } = await openFor('gambatte');

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
		const { container, iframe } = await openFor('gambatte', onsaved);

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
		const { container } = await openFor('gambatte');

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

	describe('records a play (B22)', () => {
		let posts: string[];
		beforeEach(() => {
			resetClient();
			posts = [];
			vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
				if (init?.method === 'POST') posts.push(url);
				return Promise.resolve(new Response(null, { status: url.endsWith('/played') ? 500 : 204 }));
			});
		});
		afterEach(() => vi.unstubAllGlobals());
		const settle = () => new Promise((done) => setTimeout(done, 10));
		const props = { gameId: 'g-1', core: 'gambatte', rom, disc: false };

		it('once when mounted open, not again on a prop update, again on the next opening; errors ignored', async () => {
			const { rerender } = render(EmulatorModal, { ...props, open: true });
			await vi.waitFor(() => expect(posts).toEqual(['/api/games/g-1/roms/13/played']));

			await rerender({ ...props, open: true, rom: { ...rom } });
			await settle();
			expect(posts).toHaveLength(1);

			await rerender({ ...props, open: false });
			await rerender({ ...props, open: true });
			await vi.waitFor(() => expect(posts).toHaveLength(2));
		});

		it.each([
			['no release', { rom: null }],
			['no core', { core: null }],
			['no game id', { gameId: '' }]
		] as const)('records nothing with %s', async (_, missing) => {
			render(EmulatorModal, { ...props, ...missing, open: true });
			await settle();
			expect(posts).toEqual([]);
		});

		it('records nothing while closed', async () => {
			render(EmulatorModal, { ...props, open: false });
			await settle();
			expect(posts).toEqual([]);
		});
	});
});

describe('EmulatorModal screenshot (E2)', () => {
	type Reply = { status: number; body?: unknown };
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Reply>;
	let urls: number;

	beforeEach(() => {
		resetClient();
		toasts.items.length = 0;
		calls = [];
		answers = {};
		urls = 0;
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
		vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:shot-${++urls}`);
		vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		vi.restoreAllMocks();
		auth.set(null);
	});

	async function openFor(onimagesaved?: () => void) {
		const view = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false,
			onimagesaved
		});
		const iframe = await frameOf(view.container);
		const posted = vi.spyOn(iframe.contentWindow!, 'postMessage').mockImplementation(() => {});
		return { ...view, iframe, posted };
	}

	const button = (container: HTMLElement, name: string) =>
		within(container).queryByRole('button', { name, hidden: true }) as HTMLButtonElement | null;

	function shot(
		source: Window | null,
		result: Record<string, unknown> = {
			ok: true,
			bytes: new Uint8Array([137, 80]).buffer,
			extension: '.png'
		}
	) {
		window.dispatchEvent(
			new MessageEvent('message', {
				data: { type: 'rnrm-emulator-export-result', kind: 'screenshot', ...result },
				origin: location.origin,
				source
			})
		);
	}

	const preview = (container: HTMLElement) =>
		container.querySelector('img[alt="Screenshot preview"]') as HTMLImageElement | null;

	it('only Admins get the button, after the save buttons, with the camera icon', async () => {
		const { container, unmount } = await openFor();
		const shotButton = button(container, 'Screenshot')!;
		expect(shotButton.className).toBe('btn btn-sm btn-outline-primary');
		expect(shotButton.querySelector('i')!.className.trim()).toBe('bi bi-camera');
		expect(shotButton.previousElementSibling!.textContent!.trim()).toBe('Save battery to library');
		unmount();

		auth.set({ id: 2, username: 'bob', roles: [], mustChangePassword: false });
		const user = await openFor();
		expect(button(user.container, 'Screenshot')).toBeNull();
	});

	it('asks the player and shows the preview over the game; disabled while exporting', async () => {
		const { container, iframe, posted } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);

		expect(posted).toHaveBeenCalledWith(
			{ type: 'rnrm-emulator-export', kind: 'screenshot' },
			location.origin
		);
		expect(button(container, 'Screenshot')!.disabled).toBe(true);
		expect(button(container, 'Save state to library')!.disabled).toBe(true);

		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());
		const overlay = preview(container)!.parentElement!;
		expect(overlay.parentElement!.classList).toContain('ratio');
		expect(overlay.className).toBe(
			'bg-body d-flex flex-column align-items-center justify-content-center gap-2 p-2'
		);
		expect(preview(container)!.getAttribute('src')).toBe('blob:shot-1');
		expect(button(container, 'Screenshot')!.disabled).toBe(false);
		expect(posted).toHaveBeenCalledTimes(1);
	});

	it('a second screenshot replaces the preview', async () => {
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)!.getAttribute('src')).toBe('blob:shot-2'));
		expect(container.querySelectorAll('img[alt="Screenshot preview"]')).toHaveLength(1);
	});

	it('Save as Snap uploads screenshot.png as a Snap, toasts, closes the preview and calls onimagesaved', async () => {
		const onimagesaved = vi.fn();
		answers['POST /api/games/g-1/images'] = { status: 200, body: {} };
		const { container, iframe } = await openFor(onimagesaved);
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		await fireEvent.click(button(container, 'Save as Snap')!);

		await vi.waitFor(() => expect(onimagesaved).toHaveBeenCalledTimes(1));
		const form = calls.find((c) => c.key === 'POST /api/games/g-1/images')!.body as FormData;
		const file = form.get('file') as File;
		expect([file.name, file.type, file.size, form.get('type')]).toEqual([
			'screenshot.png',
			'image/png',
			2,
			'Snap'
		]);
		expect(toasts.items.at(-1)).toMatchObject({
			text: "Screenshot saved to the game's images",
			kind: 'success'
		});
		expect(preview(container)).toBeNull();
	});

	it('Discard closes the preview', async () => {
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		await fireEvent.click(button(container, 'Discard')!);
		expect(preview(container)).toBeNull();
	});

	it('a 409 shows the job-running toast and other errors their title; the preview stays', async () => {
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		answers['POST /api/games/g-1/images'] = {
			status: 409,
			body: { status: 409, code: 'JobRunning', title: 'A job is running' }
		};
		await fireEvent.click(button(container, 'Save as Snap')!);
		await vi.waitFor(() =>
			expect(toasts.items.at(-1)?.text).toBe(
				'Another job is running. Try again when it has finished.'
			)
		);

		answers['POST /api/games/g-1/images'] = {
			status: 400,
			body: { status: 400, code: 'InvalidImage', title: 'The image could not be read' }
		};
		await fireEvent.click(button(container, 'Save as Snap')!);
		await vi.waitFor(() =>
			expect(toasts.items.at(-1)).toMatchObject({
				text: 'The image could not be read',
				kind: 'danger'
			})
		);
		expect(preview(container)).not.toBeNull();
	});

	it.each([
		['not-started', 'Start the game first.'],
		['busy', 'The emulator is saving. Try again in a moment.'],
		['error', "Couldn't take a screenshot."]
	])('a %s failure shows a warning toast', async (reason, text) => {
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow, { ok: false, reason });

		await vi.waitFor(() => expect(toasts.items.at(-1)).toMatchObject({ text, kind: 'warning' }));
		expect(preview(container)).toBeNull();
	});

	it('shows Keep border only when the border was trimmed; Save as Snap uploads the shown image (B25, B26)', async () => {
		trimScreenshot.mockResolvedValue(new Uint8Array([1, 2, 3]).buffer);
		answers['POST /api/games/g-1/images'] = { status: 200, body: {} };
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		const keep = container.querySelector<HTMLInputElement>('#screenshot-keep-border')!;
		expect(keep.checked).toBe(false);
		expect(within(container).getByLabelText('Keep border', { selector: 'input' })).toBe(keep);
		expect(keep.closest('.form-check')!.nextElementSibling!.textContent).toContain('Save as Snap');
		expect(preview(container)!.getAttribute('src')).toBe('blob:shot-2');

		await fireEvent.click(keep);
		expect(preview(container)!.getAttribute('src')).toBe('blob:shot-1');
		await fireEvent.click(button(container, 'Save as Snap')!);
		await vi.waitFor(() =>
			expect(calls.some((c) => c.key === 'POST /api/games/g-1/images')).toBe(true)
		);
		const file = (calls.find((c) => c.key === 'POST /api/games/g-1/images')!.body as FormData).get(
			'file'
		) as File;
		expect(file.size).toBe(2);
	});

	it('uploads the trimmed image by default', async () => {
		trimScreenshot.mockResolvedValue(new Uint8Array([1, 2, 3]).buffer);
		answers['POST /api/games/g-1/images'] = { status: 200, body: {} };
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		await fireEvent.click(button(container, 'Save as Snap')!);
		await vi.waitFor(() =>
			expect(calls.some((c) => c.key === 'POST /api/games/g-1/images')).toBe(true)
		);
		const form = calls.find((c) => c.key === 'POST /api/games/g-1/images')!.body as FormData;
		expect([(form.get('file') as File).size, (form.get('file') as File).name]).toEqual([
			3,
			'screenshot.png'
		]);
	});

	it('nothing trimmed, or a decode failure, keeps the original without the checkbox (B27)', async () => {
		const { container, iframe } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());

		expect(trimScreenshot).toHaveBeenCalledTimes(1);
		expect(container.querySelector('#screenshot-keep-border')).toBeNull();
		expect(preview(container)!.getAttribute('src')).toBe('blob:shot-1');
		expect(toasts.items).toEqual([]);
	});

	it('closing discards the preview, and a result after the close is ignored', async () => {
		const { container, iframe, rerender } = await openFor();
		await fireEvent.click(button(container, 'Screenshot')!);
		shot(iframe.contentWindow);
		await vi.waitFor(() => expect(preview(container)).not.toBeNull());
		await rerender({ open: false });
		await rerender({ open: true });
		expect(preview(container)).toBeNull();

		const late = await frameOf(container);
		vi.spyOn(late.contentWindow!, 'postMessage').mockImplementation(() => {});
		await fireEvent.click(button(container, 'Screenshot')!);
		await rerender({ open: false });
		await rerender({ open: true });
		shot(late.contentWindow);
		await new Promise((r) => setTimeout(r, 0));
		expect(preview(container)).toBeNull();
		expect(toasts.items).toHaveLength(0);
	});
});

describe('EmulatorModal battery saves (release editing B22–B24)', () => {
	type Reply = { status: number; body?: unknown };
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Reply>;

	const listKey = 'GET /api/games/g-1/saves';
	const battery = (id: string, uploadedBy: string | null, releaseFileName = rom.fileName) => ({
		id,
		kind: 'battery',
		releaseFileName,
		uploadedBy
	});

	beforeEach(() => {
		resetClient();
		toasts.items.length = 0;
		calls = [];
		answers = { [listKey]: { status: 200, body: { items: [] } } };
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
	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
		auth.set(null);
	});

	async function openFor(
		props: Record<string, unknown> = {},
		onsaved?: () => void
	): Promise<{
		container: HTMLElement;
		iframe: HTMLIFrameElement;
		posted: ReturnType<typeof vi.spyOn>;
	}> {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false,
			onsaved,
			...props
		});
		const iframe = await frameOf(container);
		const posted = vi.spyOn(iframe.contentWindow!, 'postMessage').mockImplementation(() => {});
		return { container, iframe, posted };
	}

	const close = (container: HTMLElement) =>
		(container.querySelector('.btn-close') as HTMLButtonElement).click();
	const closed = (container: HTMLElement) =>
		vi.waitFor(() => expect(container.querySelector('iframe')).toBeNull());
	const writes = () => calls.filter((c) => /^(POST|PUT) \/api\/games\/g-1\/saves/.test(c.key));
	const ok = { ok: true, bytes: new Uint8Array([4, 5]).buffer, extension: '.srm' };

	it("auto-loads the user's newest battery save of the release (B22)", async () => {
		answers[listKey] = {
			status: 200,
			body: {
				items: [
					battery('bobs', 'bob'),
					{ ...battery('state', 'alice'), kind: 'state' },
					battery('other-release', 'alice', 'Other.gb'),
					battery('mine-new', 'ALICE', rom.fileName.toUpperCase()),
					battery('mine-old', 'alice')
				]
			}
		};
		const { iframe } = await openFor();

		expect(iframe.getAttribute('src')).toMatch(/&save=mine-new&saveKind=battery$/);
	});

	it('shows a spinner, not the player, until the lookup settles', async () => {
		let release!: () => void;
		const pending = new Promise<void>((r) => (release = r));
		const answer = globalThis.fetch;
		vi.stubGlobal('fetch', async (url: string, init?: RequestInit) => {
			await pending;
			return answer(url, init);
		});
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false
		});
		await new Promise((r) => setTimeout(r, 10));
		expect(container.querySelector('iframe')).toBeNull();
		expect(container.querySelector('.ratio .spinner-border')).not.toBeNull();

		release();
		await frameOf(container);
	});

	it('an explicit save wins, without a lookup; a failed lookup plays without a save', async () => {
		const explicit = await openFor({ save: { saveId: 'given', saveKind: 'state' } });
		expect(explicit.iframe.getAttribute('src')).toMatch(/&save=given&saveKind=state$/);
		expect(calls.filter((c) => c.key === listKey)).toEqual([]);

		answers[listKey] = { status: 500, body: { status: 500, title: 'Boom' } };
		const failed = await openFor();
		expect(failed.iframe.getAttribute('src')).not.toMatch(/&save=/);
		expect(toasts.items).toEqual([]);
	});

	it('on close, replaces the auto-loaded save, toasts and calls onsaved (B23)', async () => {
		answers[listKey] = { status: 200, body: { items: [battery('mine', 'alice')] } };
		answers['PUT /api/games/g-1/saves/mine/file'] = { status: 200, body: {} };
		const onsaved = vi.fn();
		const { container, iframe, posted } = await openFor({}, onsaved);

		close(container);
		expect(posted).toHaveBeenCalledWith(
			{ type: 'rnrm-emulator-export', kind: 'battery' },
			location.origin
		);
		expect(container.querySelector('iframe')).not.toBeNull();
		answerExport(iframe, 'battery', ok);
		await closed(container);

		expect(writes().map((c) => c.key)).toEqual(['PUT /api/games/g-1/saves/mine/file']);
		expect(onsaved).toHaveBeenCalledTimes(1);
		expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([
			['Battery save stored in the library', 'success']
		]);
	});

	it("a disc set plays from Disc 1's release: its titles, set URL, auto-loaded battery save and close store (multi-disc M8)", async () => {
		answers[listKey] = { status: 200, body: { items: [battery('disc-one', 'alice')] } };
		answers['PUT /api/games/g-1/saves/disc-one/file'] = { status: 200, body: {} };
		const { container, iframe } = await openFor({
			core: 'pcsx_rearmed',
			discSet: 'Orbit Blocks (World).m3u'
		});

		expect(iframe.getAttribute('src')).toBe(
			'/emulator/play.html?game=g-1&rom=13&core=pcsx_rearmed&name=Orbit%20Blocks%20(World).m3u&disc=1&set=1&save=disc-one&saveKind=battery'
		);
		expect(iframe.getAttribute('title')).toBe('Emulator: Orbit Blocks (World).m3u');
		expect(document.body.textContent).toContain('Play Orbit Blocks (World).m3u');
		const buttons = [...container.querySelectorAll('button')].map((b) => b.textContent?.trim());
		expect(buttons).not.toContain('Save state to library');
		expect(buttons).toContain('Save battery to library');

		close(container);
		answerExport(iframe, 'battery', ok);
		await closed(container);
		expect(writes().map((c) => c.key)).toEqual(['PUT /api/games/g-1/saves/disc-one/file']);
	});

	it('without a played save, overwrites the newest own one, else adds one', async () => {
		answers['POST /api/games/g-1/saves'] = { status: 201, body: {} };
		const first = await openFor({ save: { saveId: 'a-state', saveKind: 'state' } });
		answers[listKey] = {
			status: 200,
			body: { items: [battery('newest', 'alice'), battery('older', 'alice')] }
		};
		close(first.container);
		answerExport(first.iframe, 'battery', ok);
		await closed(first.container);
		expect(writes().map((c) => c.key)).toEqual(['PUT /api/games/g-1/saves/newest/file']);

		calls.length = 0;
		answers[listKey] = { status: 200, body: { items: [] } };
		const second = await openFor();
		close(second.container);
		answerExport(second.iframe, 'battery', ok);
		await closed(second.container);
		const form = writes()[0].body as FormData;
		expect(writes().map((c) => c.key)).toEqual(['POST /api/games/g-1/saves']);
		expect([form.get('kind'), form.get('romId')]).toEqual(['battery', '13']);
		expect(form.get('name')).toMatch(/^Battery \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
	});

	it("an Admin playing another user's save adds their own instead of overwriting it", async () => {
		answers[listKey] = { status: 200, body: { items: [battery('bobs', 'bob')] } };
		answers['POST /api/games/g-1/saves'] = { status: 201, body: {} };
		const { container, iframe } = await openFor({ save: { saveId: 'bobs', saveKind: 'battery' } });

		close(container);
		answerExport(iframe, 'battery', ok);
		await closed(container);

		expect(writes().map((c) => c.key)).toEqual(['POST /api/games/g-1/saves']);
	});

	it.each(['not-started', 'empty', 'unsupported'])(
		'%s stores nothing and says nothing',
		async (reason) => {
			const { container, iframe } = await openFor();
			close(container);
			answerExport(iframe, 'battery', { ok: false, reason });
			await closed(container);

			expect(writes()).toEqual([]);
			expect(toasts.items).toEqual([]);
		}
	);

	it('no answer within 5 s closes silently', async () => {
		const { container } = await openFor();
		vi.useFakeTimers();
		close(container);
		await vi.advanceTimersByTimeAsync(4999);
		flushSync();
		expect(container.querySelector('iframe')).not.toBeNull();
		await vi.advanceTimersByTimeAsync(1);
		flushSync();

		expect(container.querySelector('iframe')).toBeNull();
		expect([writes(), toasts.items]).toEqual([[], []]);
	});

	it('a busy player or a failed upload warns, and the modal still closes', async () => {
		const busy = await openFor();
		close(busy.container);
		answerExport(busy.iframe, 'battery', { ok: false, reason: 'busy' });
		await closed(busy.container);
		expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([
			["Couldn't store the battery save", 'warning']
		]);

		toasts.items.length = 0;
		answers['POST /api/games/g-1/saves'] = {
			status: 409,
			body: { status: 409, code: 'JobRunning', title: 'A job is running' }
		};
		const failed = await openFor();
		close(failed.container);
		answerExport(failed.iframe, 'battery', ok);
		await closed(failed.container);
		expect(toasts.items.map((t) => [t.text, t.kind])).toEqual([
			["Couldn't store the battery save", 'warning']
		]);
	});

	it('a close during a running "Save battery to library" waits for it, then stores', async () => {
		answers['POST /api/games/g-1/saves'] = { status: 201, body: {} };
		const { container, iframe, posted } = await openFor();
		await fireEvent.click(
			within(container).getByRole('button', { name: 'Save battery to library', hidden: true })
		);
		close(container);
		await new Promise((r) => setTimeout(r, 10));
		expect(posted).toHaveBeenCalledTimes(1);

		answerExport(iframe, 'battery', ok);
		await vi.waitFor(() => expect(posted).toHaveBeenCalledTimes(2));
		expect(container.querySelector('iframe')).not.toBeNull();
		expect(writes()).toHaveLength(1);
		answerExport(iframe, 'battery', ok);
		await closed(container);

		expect(writes()).toHaveLength(2);
		expect(toasts.items.map((t) => t.text)).toEqual([
			'Saved to library',
			'Battery save stored in the library'
		]);
	});

	it('a screenshot nobody answers within 30 s reads as an error', async () => {
		const { container } = await openFor();
		vi.useFakeTimers();
		await fireEvent.click(
			within(container).getByRole('button', { name: 'Screenshot', hidden: true })
		);
		await vi.advanceTimersByTimeAsync(30000);

		expect(toasts.items.at(-1)).toMatchObject({
			text: "Couldn't take a screenshot.",
			kind: 'warning'
		});
	});
});
