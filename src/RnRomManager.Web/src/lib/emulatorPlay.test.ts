import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Browser emulators B5/B8: the play page itself, with fetch and the loader script mocked.
const game = '0f8fad5b-d9cb-469f-a165-70867728950e';
const romUrl = `/api/games/${game}/roms/12/download`;
const biosUrl = '/api/emulator/bios/colecovision.rom';

type FakeResponse = {
	ok: boolean;
	status: number;
	body: { cancel: () => Promise<void> } | null;
	blob: () => Promise<Blob>;
};

const ejsGlobals = [
	'EJS_player',
	'EJS_core',
	'EJS_gameUrl',
	'EJS_gameName',
	'EJS_pathtodata',
	'EJS_disableDatabases',
	'EJS_disableAutoLang',
	'EJS_biosUrl',
	'EJS_hideSettings',
	'EJS_controlScheme',
	'EJS_externalFiles',
	'EJS_Buttons',
	'EJS_defaultOptions',
	'EJS_paths',
	'EJS_threads',
	'EJS_disableCue',
	'EJS_onGameStart'
];

const win = window as unknown as Record<string, unknown>;

function response(status: number, cancel?: () => Promise<void>): FakeResponse {
	return {
		ok: status >= 200 && status < 300,
		status,
		body: cancel ? { cancel } : null,
		blob: async () => new Blob(['rom'])
	};
}

let fetchMock: ReturnType<typeof vi.fn>;
/** The EJS_* globals as they were when the loader script was added, or null if it never was. */
let atLoader: Record<string, unknown> | null;
const messageListeners: EventListenerOrEventListenerObject[] = [];

function serve(routes: Record<string, () => Promise<FakeResponse>>) {
	fetchMock = vi.fn(
		(url: string) => routes[url]?.() ?? Promise.reject(new Error(`unexpected ${url}`))
	);
	vi.stubGlobal('fetch', fetchMock);
}

/** Sega CD B6: `disc` adds `disc=1`, as the SPA does for an external (disc) release. */
async function play(core: string, name: string, disc = false): Promise<string> {
	const query = new URLSearchParams({ game, rom: '12', core, name });
	if (disc) query.set('disc', '1');
	history.replaceState(null, '', `/emulator/play.html?${query}`);
	const { started } = await import('../../static/emulator/play.js');
	await started;
	return document.getElementById('status')!.textContent ?? '';
}

function urls(): string[] {
	return fetchMock.mock.calls.map((c) => c[0] as string);
}

beforeEach(() => {
	vi.resetModules();
	document.body.innerHTML =
		'<p id="status"></p><progress id="loading" hidden></progress><span id="downloaded" hidden></span><div id="game"></div>';
	delete document.body.dataset.player;
	for (const key of ejsGlobals) delete win[key];
	delete win.EJS_emulator;
	atLoader = null;
	// 3DO B11: each import of the page adds a message listener; drop them between tests.
	const add = window.addEventListener.bind(window);
	vi.spyOn(window, 'addEventListener').mockImplementation((type, listener, options) => {
		if (type === 'message' && listener) messageListeners.push(listener);
		add(type, listener, options);
	});
	const append = document.body.append.bind(document.body);
	vi.spyOn(document.body, 'append').mockImplementation((...nodes) => {
		atLoader = Object.fromEntries(ejsGlobals.map((k) => [k, win[k]]));
		append(...nodes);
	});
});

afterEach(() => {
	for (const listener of messageListeners.splice(0))
		window.removeEventListener('message', listener);
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
});

describe('play page loading indicator', () => {
	it.each([
		['load', '', true],
		['error', "Couldn't load the emulator.", false]
	])(
		'reports bytes received and clears the startup message on loader %s',
		async (event, message, knownLength) => {
			const { readable, writable } = new TransformStream<Uint8Array, Uint8Array>();
			const writer = writable.getWriter();
			const url = knownLength ? romUrl : `/api/games/${game}/roms/12/play-disc`;
			serve({
				'/api/emulator/bios/scph5501.bin': async () => response(404),
				[url]: async () =>
					new Response(readable, { headers: knownLength ? { 'Content-Length': '4' } : {} })
			});
			const finished = play(knownLength ? 'gambatte' : 'pcsx_rearmed', 'Game');
			const progress = document.querySelector('progress')!;
			const downloaded = document.getElementById('downloaded')!;

			await writer.write(new Uint8Array([1, 2]));
			expect(progress.hidden).toBe(false);
			expect(progress.hasAttribute('value')).toBe(knownLength);
			expect(downloaded.hidden).toBe(false);
			if (knownLength) {
				expect(progress.max).toBe(4);
				expect(progress.value).toBe(2);
				expect(downloaded.textContent).toBe('50%');
			} else {
				expect(downloaded.textContent).toBe(
					new Intl.NumberFormat(undefined, {
						style: 'unit',
						unit: 'byte',
						unitDisplay: 'long'
					}).format(2)
				);
			}
			expect(atLoader).toBeNull();

			await writer.write(new Uint8Array([3, 4]));
			await writer.close();
			expect(await finished).toBe('Starting emulator…');
			expect((atLoader?.EJS_gameUrl as File).size).toBe(4);
			expect(progress.hidden).toBe(true);
			expect(downloaded.hidden).toBe(true);
			document.querySelector('script')!.dispatchEvent(new Event(event));
			expect(document.getElementById('status')!.textContent).toBe(message);
		}
	);

	async function playWithTotalHeader(chunks: number[][]) {
		const { readable, writable } = new TransformStream<Uint8Array, Uint8Array>();
		const writer = writable.getWriter();
		serve({
			[romUrl]: async () => new Response(readable, { headers: { 'X-Content-Bytes': '4' } })
		});
		const finished = play('gambatte', 'Game.gb');
		for (const chunk of chunks) await writer.write(new Uint8Array(chunk));
		return { writer, finished };
	}

	it('uses X-Content-Bytes as the total when there is no Content-Length (P3)', async () => {
		const { writer, finished } = await playWithTotalHeader([[1, 2]]);
		const progress = document.querySelector('progress')!;
		expect(progress.hidden).toBe(false);
		expect(progress.max).toBe(4);
		expect(progress.value).toBe(2);
		expect(document.getElementById('downloaded')!.textContent).toBe('50%');
		await writer.close();
		await finished;
	});

	it('caps the percent at 100% when more bytes than X-Content-Bytes arrive (P3)', async () => {
		const { writer, finished } = await playWithTotalHeader([
			[1, 2, 3],
			[4, 5, 6]
		]);
		expect(document.querySelector('progress')!.value).toBe(4);
		expect(document.getElementById('downloaded')!.textContent).toBe('100%');
		await writer.close();
		expect(await finished).toBe('Starting emulator…');
	});

	it('hides the indicator if the ROM transfer fails', async () => {
		const { readable, writable } = new TransformStream<Uint8Array, Uint8Array>();
		const writer = writable.getWriter();
		serve({ [romUrl]: async () => new Response(readable, { headers: { 'Content-Length': '4' } }) });

		const finished = play('gambatte', 'Game.gb');
		await writer.write(new Uint8Array([1, 2]));
		const progress = document.querySelector('progress')!;
		expect(progress.hidden).toBe(false);
		await writer.abort(new Error('connection lost'));

		expect(await finished).toBe("Couldn't load the ROM.");
		expect(progress.hidden).toBe(true);
		expect(atLoader).toBeNull();
	});
});

describe('play page WebGL drawing buffer (E2)', () => {
	it('creates WebGL contexts with preserveDrawingBuffer so canvas screenshots are not black', async () => {
		const getContext = vi
			.spyOn(HTMLCanvasElement.prototype, 'getContext')
			.mockImplementation((() => ({}) as RenderingContext) as never);
		serve({ [romUrl]: async () => response(200) });
		await play('gambatte', 'Game');
		const canvas = document.createElement('canvas');

		canvas.getContext('webgl2', { alpha: false });
		canvas.getContext('webgl');
		canvas.getContext('2d', { alpha: false });

		expect(getContext.mock.calls).toEqual([
			['webgl2', { alpha: false, preserveDrawingBuffer: true }],
			['webgl', { preserveDrawingBuffer: true }],
			['2d', { alpha: false }]
		]);
	});
});

describe('play page BIOS check', () => {
	it('checks the BIOS with credentials before fetching the ROM, then sets EJS_biosUrl', async () => {
		const cancel = vi.fn(async () => {});
		serve({ [biosUrl]: async () => response(200, cancel), [romUrl]: async () => response(200) });

		await play('gearcoleco', 'Pixel Pursuit (USA).col');

		expect(urls()).toEqual([biosUrl, romUrl]);
		expect(fetchMock.mock.calls[0][1]).toEqual({ credentials: 'same-origin' });
		expect(cancel).toHaveBeenCalledOnce();
		expect(atLoader).toMatchObject({
			EJS_biosUrl: biosUrl,
			EJS_disableDatabases: true,
			EJS_core: 'gearcoleco'
		});
		expect(document.querySelector('script[src="/emulator/vendor/data/loader.js"]')).not.toBeNull();
	});

	it('checks disksys.rom for a Famicom Disk System release', async () => {
		const disksys = '/api/emulator/bios/disksys.rom';
		serve({ [disksys]: async () => response(200), [romUrl]: async () => response(200) });

		await play('fceumm', 'Disk Quest (Japan).fds');

		expect(urls()).toEqual([disksys, romUrl]);
		expect(atLoader).toMatchObject({ EJS_biosUrl: disksys, EJS_core: 'fceumm' });
	});

	it('checks no BIOS for an NES release on the same core', async () => {
		serve({ [romUrl]: async () => response(200) });

		await play('fceumm', 'Cart Quest (USA).nes');

		expect(urls()).toEqual([romUrl]);
	});

	it('goes on when cancelling the BIOS body fails', async () => {
		const cancel = vi.fn(async () => {
			throw new Error('cancel failed');
		});
		serve({ [biosUrl]: async () => response(200, cancel), [romUrl]: async () => response(200) });

		const status = await play('gearcoleco', 'Pixel Pursuit (USA).col');

		expect(status).toBe('Starting emulator…');
		expect(atLoader?.EJS_biosUrl).toBe(biosUrl);
	});

	it.each([
		[401, 'Your session has expired. Sign in again.'],
		[
			404,
			'This system needs its BIOS: ask an Admin to put colecovision.rom in the BIOS folder (Paths:Bios).'
		],
		[500, "Couldn't load the BIOS (HTTP 500)."]
	])('stops on %i with its message, before the ROM or the loader', async (code, message) => {
		serve({ [biosUrl]: async () => response(code), [romUrl]: async () => response(200) });

		expect(await play('gearcoleco', 'Pixel Pursuit (USA).col')).toBe(message);
		expect(urls()).toEqual([biosUrl]);
		expect(atLoader).toBeNull();
	});

	it('shows its own message when the BIOS request fails', async () => {
		serve({
			[biosUrl]: () => Promise.reject(new TypeError('network')),
			[romUrl]: async () => response(200)
		});

		expect(await play('gearcoleco', 'Pixel Pursuit (USA).col')).toBe("Couldn't load the BIOS.");
		expect(urls()).toEqual([biosUrl]);
		expect(atLoader).toBeNull();
	});

	it('keeps the ROM error for a failed ROM request', async () => {
		serve({ [biosUrl]: async () => response(200), [romUrl]: async () => response(500) });

		expect(await play('gearcoleco', 'Pixel Pursuit (USA).col')).toBe(
			"Couldn't load the ROM (HTTP 500)."
		);
		expect(atLoader).toBeNull();
	});

	it('fetches only the ROM for a core without a BIOS', async () => {
		serve({ [romUrl]: async () => response(200) });

		await play('stella2014', 'Laser Lanes (USA).a26');

		expect(urls()).toEqual([romUrl]);
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		expect(atLoader?.EJS_core).toBe('stella2014');
	});
});

describe('play page Sega settings', () => {
	beforeEach(() => serve({ [romUrl]: async () => response(200) }));

	it('hides only the core selector for smsplus and uses segaGG for a .gg release', async () => {
		await play('smsplus', 'Pocket Drift (World).gg');

		expect(atLoader?.EJS_hideSettings).toEqual(['retroarch_core']);
		expect(atLoader?.EJS_controlScheme).toBe('segaGG');
	});

	it('leaves the control scheme unset for an .sms release', async () => {
		await play('smsplus', 'Harbor Patrol (World).sms');

		expect(atLoader?.EJS_hideSettings).toEqual(['retroarch_core']);
		expect(atLoader?.EJS_controlScheme).toBeUndefined();
	});

	it('changes nothing for another core', async () => {
		await play('gambatte', 'Odd (World).gg');

		expect(atLoader?.EJS_hideSettings).toBeUndefined();
		expect(atLoader?.EJS_controlScheme).toBeUndefined();
	});
});

describe('play page PlayStation', () => {
	const psxBiosUrl = '/api/emulator/bios/scph5501.bin';
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const disc = new Uint8Array([0x50, 0x4b, 3, 4, 9, 9]);

	/** A BIOS response whose body arrives in the given chunks, recording a reader cancel. */
	function biosBody(chunks: Uint8Array[], status = 200) {
		const cancelled = vi.fn();
		const stream = new ReadableStream<Uint8Array>({
			pull(controller) {
				const next = chunks.shift();
				if (next) controller.enqueue(next);
				else controller.close();
			},
			cancel: cancelled
		});
		return { response: { ok: true, status, body: stream } as unknown as FakeResponse, cancelled };
	}

	function discResponse(status: number, problem?: object) {
		return {
			ok: status >= 200 && status < 300,
			status,
			body: null,
			blob: async () => new Blob([disc]),
			json: async () => {
				if (!problem) throw new SyntaxError('no body');
				return problem;
			}
		};
	}

	function firmware(size = 512 * 1024) {
		return new Uint8Array(size).map((_, i) => i % 251);
	}

	it('gets the BIOS once, then the disc, and hands the exact bytes to EmulatorJS', async () => {
		const bytes = firmware();
		const { response: bios } = biosBody([bytes.slice(0, 200_000), bytes.slice(200_000)]);
		serve({ [psxBiosUrl]: async () => bios, [discUrl]: async () => discResponse(200) });

		await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true);

		expect(urls()).toEqual([psxBiosUrl, discUrl]);
		expect(fetchMock.mock.calls.map((c) => c[1])).toEqual([
			{ credentials: 'same-origin' },
			{ credentials: 'same-origin' }
		]);
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/scph5501.bin']);
		expect(new Uint8Array(files['/scph5501.bin'])).toEqual(bytes);
		expect(atLoader).toMatchObject({
			EJS_core: 'pcsx_rearmed',
			EJS_gameName: 'Disc (USA) (Track 1).bin',
			EJS_hideSettings: ['retroarch_core']
		});
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe('Disc (USA) (Track 1).bin');
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	it('uses HLE when there is no BIOS (404)', async () => {
		serve({ [psxBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('pcsx_rearmed', 'Solo (USA).iso', true);

		expect(urls()).toEqual([psxBiosUrl, discUrl]);
		expect(atLoader?.EJS_externalFiles).toBeUndefined();
		expect(atLoader?.EJS_core).toBe('pcsx_rearmed');
	});

	it.each([
		['a short', [firmware(512 * 1024 - 1)]],
		['an oversized', [firmware(), new Uint8Array(1)]]
	])('stops on %s BIOS before fetching the disc', async (_, chunks) => {
		const { response: bios, cancelled } = biosBody(chunks);
		serve({ [psxBiosUrl]: async () => bios, [discUrl]: async () => discResponse(200) });

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true)).toBe(
			'The PlayStation BIOS in the BIOS folder (Paths:Bios) must be the 512 KiB scph5501.bin: ask an Admin to replace it.'
		);
		expect(urls()).toEqual([psxBiosUrl]);
		expect(atLoader).toBeNull();
		if (chunks.length === 2) expect(cancelled).toHaveBeenCalledOnce();
	});

	it.each([
		[401, 'Your session has expired. Sign in again.'],
		[500, "Couldn't load the BIOS (HTTP 500)."]
	])('stops on a BIOS %i before fetching the disc', async (code, message) => {
		serve({ [psxBiosUrl]: async () => response(code), [discUrl]: async () => discResponse(200) });

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true)).toBe(message);
		expect(urls()).toEqual([psxBiosUrl]);
		expect(atLoader).toBeNull();
	});

	it('stops when the BIOS request or its body fails', async () => {
		const failing = new ReadableStream<Uint8Array>({
			pull(controller) {
				controller.error(new TypeError('reset'));
			}
		});
		serve({
			[psxBiosUrl]: async () =>
				({ ok: true, status: 200, body: failing }) as unknown as FakeResponse,
			[discUrl]: async () => discResponse(200)
		});
		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true)).toBe(
			"Couldn't load the BIOS."
		);
		expect(urls()).toEqual([psxBiosUrl]);

		vi.resetModules();
		serve({
			[psxBiosUrl]: () => Promise.reject(new TypeError('network')),
			[discUrl]: async () => discResponse(200)
		});
		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true)).toBe(
			"Couldn't load the BIOS."
		);
		expect(atLoader).toBeNull();
	});

	it.each([
		[
			409,
			{ code: 'PlayDiscTooLarge' },
			'This disc is larger than 2 GiB, too large to play in the browser.'
		],
		[
			409,
			{ code: 'InvalidPlayDisc' },
			"This disc can't be played in the browser: its cue sheet or files are invalid or don't match."
		],
		[401, { code: 'Unauthorized' }, 'Your session has expired. Sign in again.'],
		[404, { code: 'NotFound' }, "Couldn't load the disc (HTTP 404)."],
		[502, undefined, "Couldn't load the disc (HTTP 502)."]
	])('stops on a disc %i with its message, without the loader', async (code, problem, message) => {
		serve({
			[psxBiosUrl]: async () => response(404),
			[discUrl]: async () => discResponse(code, problem)
		});

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true)).toBe(message);
		expect(atLoader).toBeNull();
	});

	it('stops when the disc request fails', async () => {
		serve({
			[psxBiosUrl]: async () => response(404),
			[discUrl]: () => Promise.reject(new TypeError('network'))
		});

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin', true)).toBe(
			"Couldn't load the disc."
		);
		expect(atLoader).toBeNull();
	});

	it('scopes a standalone image named like the BIOS to the game', async () => {
		serve({ [psxBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('pcsx_rearmed', 'SCPH5501.BIN', true);

		expect((atLoader?.EJS_gameUrl as File).name).toBe(`${game}-SCPH5501.BIN`);
		expect(atLoader?.EJS_gameName).toBe('SCPH5501.BIN');
	});
	it('loads a standalone .chd from play-disc under its own name (CHD B5)', async () => {
		serve({ [psxBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('pcsx_rearmed', 'Game.chd', true);

		expect(urls()).toEqual([psxBiosUrl, discUrl]);
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe('Game.chd');
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	const setUrl = `/api/games/${game}/roms/12/play-disc-set`;

	async function playSet(name: string): Promise<string> {
		const query = new URLSearchParams({
			game,
			rom: '12',
			core: 'pcsx_rearmed',
			name,
			disc: '1',
			set: '1'
		});
		history.replaceState(null, '', `/emulator/play.html?${query}`);
		const { started } = await import('../../static/emulator/play.js');
		await started;
		return document.getElementById('status')!.textContent ?? '';
	}

	it('loads a disc set from play-disc-set after the BIOS check, named after its playlist (multi-disc M6)', async () => {
		serve({ [psxBiosUrl]: async () => response(404), [setUrl]: async () => discResponse(200) });

		await playSet('Odyssey #1 (USA).m3u');

		expect(urls()).toEqual([psxBiosUrl, setUrl]);
		expect(atLoader).toMatchObject({
			EJS_core: 'pcsx_rearmed',
			EJS_gameName: 'Odyssey #1 (USA).m3u'
		});
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe('Odyssey _1 (USA).m3u');
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	it.each([
		[
			'PlayDiscTooLarge',
			'This disc set is larger than 2 GiB, too large to play in the browser. CHD images are much smaller.'
		],
		[
			'InvalidPlayDisc',
			"This disc can't be played in the browser: its cue sheet or files are invalid or don't match."
		]
	])(
		'shows the set message for a 409 %s before the loader (multi-disc M6)',
		async (code, message) => {
			serve({
				[psxBiosUrl]: async () => response(404),
				[setUrl]: async () => new Response(JSON.stringify({ code }), { status: 409 })
			});

			expect(await playSet('Odyssey (USA).m3u')).toBe(message);
			expect(atLoader).toBeNull();
		}
	);
});

describe('play page NES', () => {
	it('loads the cartridge through the download route, with only the core chooser hidden', async () => {
		serve({ [romUrl]: async () => response(200) });

		await play('fceumm', 'Pixel Quest (USA).nes');

		expect(urls()).toEqual([romUrl]);
		expect(atLoader).toMatchObject({ EJS_core: 'fceumm', EJS_hideSettings: ['retroarch_core'] });
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		expect(atLoader?.EJS_externalFiles).toBeUndefined();
		expect((atLoader?.EJS_gameUrl as File).name).toBe('Pixel Quest (USA).nes');
	});
});

describe('play page PSP (PSP B3)', () => {
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const iso = new Uint8Array([0x43, 0x44, 0x30, 0x30, 0x31, 7]);

	function isoResponse(status: number, problem?: object, blob = async () => new Blob([iso])) {
		return {
			ok: status >= 200 && status < 300,
			status,
			body: null,
			blob,
			json: async () => {
				if (!problem) throw new SyntaxError('no body');
				return problem;
			}
		};
	}

	/** A browser context; each flag defaults to what PPSSPP needs. */
	function browser({ secure = true, isolated = true, webgl2 = true } = {}) {
		vi.stubGlobal('isSecureContext', secure);
		vi.stubGlobal('crossOriginIsolated', isolated);
		vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(((kind: string) =>
			kind === 'webgl2' && webgl2 ? ({} as RenderingContext) : null) as never);
	}

	async function playPsp(core: string, name: string, page = 'play-psp.html'): Promise<string> {
		if (page === 'play-psp.html') document.body.dataset.player = 'psp';
		const query = new URLSearchParams({ game, rom: '12', core, name });
		history.replaceState(null, '', `/emulator/${page}?${query}`);
		const { started } = await import('../../static/emulator/play.js');
		await started;
		return document.getElementById('status')!.textContent ?? '';
	}

	it('fetches only the ISO from play-disc and starts PPSSPP threaded, with no BIOS', async () => {
		browser();
		serve({ [discUrl]: async () => isoResponse(200) as unknown as FakeResponse });

		await playPsp('ppsspp', 'Pocket #Ninja (USA).iso');

		expect(urls()).toEqual([discUrl]);
		expect(fetchMock.mock.calls[0][1]).toEqual({ credentials: 'same-origin' });
		expect(atLoader).toMatchObject({ EJS_core: 'ppsspp', EJS_gameName: 'Pocket #Ninja (USA).iso' });
		expect(win.EJS_threads).toBe(true);
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		expect(atLoader?.EJS_externalFiles).toBeUndefined();
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe('Pocket _Ninja (USA).iso');
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(iso);
		delete win.EJS_threads;
	});

	it.each([
		[{ secure: false }, 'PSP games need a secure page'],
		[{ isolated: false }, "This page isn't cross-origin isolated"],
		[{ webgl2: false }, 'PSP games need WebGL 2']
	])(
		'stops before any ISO or loader request when the browser lacks %o',
		async (missing, message) => {
			browser(missing);
			serve({});

			expect(await playPsp('ppsspp', 'Umd (USA).iso')).toMatch(new RegExp(`^${message}`));
			expect(fetchMock).not.toHaveBeenCalled();
			expect(atLoader).toBeNull();
		}
	);

	it('rejects PPSSPP on the modal page and other cores on the PSP page', async () => {
		browser();
		serve({});

		expect(await playPsp('ppsspp', 'Umd (USA).iso', 'play.html')).toBe('Invalid emulator link.');
		vi.resetModules();
		expect(await playPsp('pcsx_rearmed', 'Disc (USA).iso')).toBe('Invalid emulator link.');
		expect(fetchMock).not.toHaveBeenCalled();
		expect(atLoader).toBeNull();
	});

	it.each([
		[
			409,
			{ code: 'PlayDiscTooLarge' },
			'This UMD image is larger than 2 GiB, too large to play in the browser.'
		],
		[
			409,
			{ code: 'InvalidPlayDisc' },
			"This UMD image can't be played in the browser: it must be one non-empty .iso, .cso or .pbp whose name matches the release."
		],
		[401, undefined, 'Your session has expired. Sign in again.'],
		[500, { code: 'IoError' }, "Couldn't load the disc (HTTP 500)."]
	])('shows the PSP message for HTTP %i', async (status, problem, message) => {
		browser();
		serve({ [discUrl]: async () => isoResponse(status, problem) as unknown as FakeResponse });

		expect(await playPsp('ppsspp', 'Umd (USA).iso')).toBe(message);
		expect(atLoader).toBeNull();
	});

	it('shows a disc-loading error when the ISO body fails to read', async () => {
		browser();
		serve({
			[discUrl]: async () =>
				isoResponse(200, undefined, async () => {
					throw new TypeError('network');
				}) as unknown as FakeResponse
		});

		expect(await playPsp('ppsspp', 'Umd (USA).iso')).toBe("Couldn't load the disc.");
		expect(atLoader).toBeNull();
	});
});

describe('play page Genesis and Master System (Genesis B4, B5)', () => {
	const check = '/api/emulator/cores/genesis_plus_gx';

	it.each(['Turbo Comet (USA).md', 'Turbo Comet (USA).gen', 'Turbo Comet (USA).bin'])(
		'checks the core files with one HEAD, then loads %s with the API paths and Mega Drive controls',
		async (name) => {
			serve({ [check]: async () => response(204), [romUrl]: async () => response(200) });

			await play('genesis_plus_gx', name);

			expect(urls()).toEqual([check, romUrl]);
			expect(fetchMock.mock.calls[0][1]).toEqual({ method: 'HEAD', credentials: 'same-origin' });
			expect(atLoader).toMatchObject({
				EJS_core: 'genesis_plus_gx',
				EJS_gameName: name,
				EJS_controlScheme: 'segaMD',
				EJS_hideSettings: ['retroarch_core'],
				EJS_paths: {
					'genesis_plus_gx-wasm.data':
						'/api/emulator/cores/genesis_plus_gx/genesis_plus_gx-wasm.data',
					'genesis_plus_gx-legacy-wasm.data':
						'/api/emulator/cores/genesis_plus_gx/genesis_plus_gx-legacy-wasm.data',
					'genesis_plus_gx.json': '/api/emulator/cores/genesis_plus_gx/genesis_plus_gx.json'
				}
			});
			expect(atLoader?.EJS_threads).toBeUndefined();
			expect(atLoader?.EJS_biosUrl).toBeUndefined();
			// Sega CD B6/B7: a cartridge has no disc flag, so no Sega CD BIOS, external files or cue setting.
			expect(atLoader?.EJS_externalFiles).toBeUndefined();
			expect(atLoader?.EJS_disableCue).toBeUndefined();
			expect((atLoader?.EJS_gameUrl as File).name).toBe(name);
		}
	);

	it.each([
		[
			404,
			'Genesis and Sega CD games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).'
		],
		[
			409,
			"The Genesis Plus GX core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:GenesisCorePath)."
		],
		[401, 'Your session has expired. Sign in again.'],
		[500, "Couldn't load the Genesis core (HTTP 500)."]
	])('stops on HTTP %i before fetching the ROM or starting the loader', async (status, message) => {
		serve({ [check]: async () => response(status), [romUrl]: async () => response(200) });

		expect(await play('genesis_plus_gx', 'Turbo Comet (USA).md')).toBe(message);
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});

	it('stops when the availability check fails to reach the server', async () => {
		serve({
			[check]: async () => Promise.reject(new TypeError('network')),
			[romUrl]: async () => response(200)
		});

		expect(await play('genesis_plus_gx', 'Turbo Comet (USA).md')).toBe(
			"Couldn't load the Genesis core."
		);
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});

	it('plays a Master System release with SMS Plus GX and never asks for Genesis files', async () => {
		serve({ [romUrl]: async () => response(200) });

		await play('smsplus', 'Harbor Patrol (USA).sms');

		expect(urls()).toEqual([romUrl]);
		expect(atLoader?.EJS_core).toBe('smsplus');
		expect(atLoader?.EJS_paths).toBeUndefined();
		expect(atLoader?.EJS_controlScheme).toBeUndefined();
	});
});

describe('play page 32X (B3)', () => {
	const check = '/api/emulator/cores/picodrive';
	const name = 'Star Relay (USA).32x';

	it('checks the core files with one HEAD, then loads the ROM with the API paths and 32X controls', async () => {
		serve({ [check]: async () => response(204), [romUrl]: async () => response(200) });

		await play('picodrive', name);

		expect(urls()).toEqual([check, romUrl]);
		expect(fetchMock.mock.calls[0][1]).toEqual({ method: 'HEAD', credentials: 'same-origin' });
		expect(atLoader).toMatchObject({
			EJS_core: 'picodrive',
			EJS_gameName: name,
			EJS_controlScheme: 'sega32x',
			EJS_hideSettings: ['retroarch_core'],
			EJS_paths: {
				'picodrive-wasm.data': '/api/emulator/cores/picodrive/picodrive-wasm.data',
				'picodrive-legacy-wasm.data': '/api/emulator/cores/picodrive/picodrive-legacy-wasm.data',
				'picodrive.json': '/api/emulator/cores/picodrive/picodrive.json'
			}
		});
		expect(atLoader?.EJS_threads).toBeUndefined();
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		expect(atLoader?.EJS_externalFiles).toBeUndefined();
		expect((atLoader?.EJS_gameUrl as File).name).toBe(name);
	});

	it.each([
		[
			404,
			'32X games need the PicoDrive core files: ask an Admin to set them up (Emulator:PicodriveCorePath).'
		],
		[
			409,
			"The PicoDrive core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:PicodriveCorePath)."
		],
		[401, 'Your session has expired. Sign in again.'],
		[503, "Couldn't load the PicoDrive core (HTTP 503)."]
	])('stops on HTTP %i before fetching the ROM or starting the loader', async (status, message) => {
		serve({ [check]: async () => response(status), [romUrl]: async () => response(200) });

		expect(await play('picodrive', name)).toBe(message);
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});

	it('stops when the availability check fails to reach the server', async () => {
		serve({
			[check]: async () => Promise.reject(new TypeError('network')),
			[romUrl]: async () => response(200)
		});

		expect(await play('picodrive', name)).toBe("Couldn't load the PicoDrive core.");
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});
});

describe('play page Saturn (B6, B7)', () => {
	const saturnBiosUrl = '/api/emulator/bios/saturn_bios.bin';
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const disc = new Uint8Array([0x50, 0x4b, 3, 4, 7, 7]);
	const name = 'Sky Rally (USA) (Track 1).bin';

	function body(bytes: Uint8Array) {
		const cancelled = vi.fn();
		const chunks = [bytes];
		const stream = new ReadableStream<Uint8Array>({
			pull(controller) {
				const next = chunks.shift();
				if (next) controller.enqueue(next);
				else controller.close();
			},
			cancel: cancelled
		});
		return {
			response: { ok: true, status: 200, body: stream } as unknown as FakeResponse,
			cancelled
		};
	}

	function discResponse(status: number) {
		return {
			ok: status >= 200 && status < 300,
			status,
			body: null,
			blob: async () => new Blob([disc]),
			json: async () => {
				throw new SyntaxError('no body');
			}
		};
	}

	it('gets saturn_bios.bin once, then the disc from play-disc, and hands both to EmulatorJS', async () => {
		const bytes = new Uint8Array(512 * 1024).map((_, i) => i % 239);
		serve({
			[saturnBiosUrl]: async () => body(bytes).response,
			[discUrl]: async () => discResponse(200)
		});

		await play('yabause', name, true);

		expect(urls()).toEqual([saturnBiosUrl, discUrl]);
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/saturn_bios.bin']);
		expect(new Uint8Array(files['/saturn_bios.bin'])).toEqual(bytes);
		expect(atLoader).toMatchObject({
			EJS_core: 'yabause',
			EJS_gameName: name,
			EJS_controlScheme: 'segaSaturn',
			EJS_hideSettings: ['retroarch_core']
		});
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		expect(atLoader?.EJS_paths).toBeUndefined();
		expect(atLoader?.EJS_threads).toBeUndefined();
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe(name);
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	it('uses the HLE BIOS when there is no saturn_bios.bin (404)', async () => {
		serve({ [saturnBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('yabause', name, true);

		expect(urls()).toEqual([saturnBiosUrl, discUrl]);
		expect(atLoader?.EJS_externalFiles).toBeUndefined();
		expect(atLoader?.EJS_core).toBe('yabause');
	});

	const setUrl = `/api/games/${game}/roms/12/play-disc-set`;

	async function playSaturnSet(): Promise<string> {
		const query = new URLSearchParams({
			game,
			rom: '12',
			core: 'yabause',
			name: 'Ark (USA).m3u',
			disc: '1',
			set: '1'
		});
		history.replaceState(null, '', `/emulator/play.html?${query}`);
		const { started } = await import('../../static/emulator/play.js');
		await started;
		return document.getElementById('status')!.textContent ?? '';
	}

	it('stops a disc set without saturn_bios.bin before the set is requested (Saturn and Sega CD S6)', async () => {
		serve({ [saturnBiosUrl]: async () => response(404), [setUrl]: async () => discResponse(200) });

		expect(await playSaturnSet()).toBe(
			'Playing all Saturn discs needs a BIOS: ask an Admin to put saturn_bios.bin in the BIOS folder (Paths:Bios).'
		);
		expect(urls()).toEqual([saturnBiosUrl]);
		expect(atLoader).toBeNull();
	});

	it('loads a disc set from play-disc-set with the BIOS (Saturn and Sega CD S6)', async () => {
		const bytes = new Uint8Array(512 * 1024).map((_, i) => i % 241);
		serve({
			[saturnBiosUrl]: async () => body(bytes).response,
			[setUrl]: async () => discResponse(200)
		});

		await playSaturnSet();

		expect(urls()).toEqual([saturnBiosUrl, setUrl]);
		expect(atLoader).toMatchObject({ EJS_core: 'yabause', EJS_gameName: 'Ark (USA).m3u' });
		expect(Object.keys(atLoader?.EJS_externalFiles as object)).toEqual(['/saturn_bios.bin']);
		expect((atLoader?.EJS_gameUrl as File).name).toBe('Ark (USA).m3u');
	});

	it.each([512 * 1024 - 1, 512 * 1024 + 1])(
		'stops on a %i-byte saturn_bios.bin before the disc is requested',
		async (size) => {
			serve({
				[saturnBiosUrl]: async () => body(new Uint8Array(size)).response,
				[discUrl]: async () => discResponse(200)
			});

			expect(await play('yabause', name, true)).toBe(
				'The Saturn BIOS in the BIOS folder (Paths:Bios) must be the 512 KiB saturn_bios.bin: ask an Admin to replace it.'
			);
			expect(urls()).toEqual([saturnBiosUrl]);
			expect(atLoader).toBeNull();
		}
	);

	it.each([
		[401, 'Your session has expired. Sign in again.'],
		[500, "Couldn't load the BIOS (HTTP 500)."]
	])('stops on a BIOS HTTP %i before the disc is requested', async (status, message) => {
		serve({
			[saturnBiosUrl]: async () => response(status),
			[discUrl]: async () => discResponse(200)
		});

		expect(await play('yabause', name, true)).toBe(message);
		expect(urls()).toEqual([saturnBiosUrl]);
		expect(atLoader).toBeNull();
	});

	it('stops when the BIOS request fails to reach the server', async () => {
		serve({
			[saturnBiosUrl]: async () => Promise.reject(new TypeError('network')),
			[discUrl]: async () => discResponse(200)
		});

		expect(await play('yabause', name, true)).toBe("Couldn't load the BIOS.");
		expect(urls()).toEqual([saturnBiosUrl]);
	});

	it('names a standalone disc called like a Saturn BIOS after the game', async () => {
		serve({ [saturnBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('yabause', 'Sega_101.BIN', true);

		expect((atLoader?.EJS_gameUrl as File).name).toBe(`${game}-Sega_101.BIN`);
		expect(atLoader?.EJS_gameName).toBe('Sega_101.BIN');
	});
	it('loads a standalone .chd from play-disc under its own name (CHD B5)', async () => {
		serve({ [saturnBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('yabause', 'Game.chd', true);

		expect(urls()).toEqual([saturnBiosUrl, discUrl]);
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe('Game.chd');
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});
});

describe('play page Sega CD (Sega CD B7)', () => {
	const check = '/api/emulator/cores/genesis_plus_gx';
	const bios = (name: string) => `/api/emulator/bios/${name}`;
	const [biosU, biosE, biosJ] = ['bios_CD_U.bin', 'bios_CD_E.bin', 'bios_CD_J.bin'].map(bios);
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const disc = new Uint8Array([0x50, 0x4b, 3, 4, 5, 5]);
	const name = 'Disc Comet (USA).cue';
	const firmware = (seed: number, size = 131072) =>
		new Uint8Array(size).map((_, i) => (i + seed) % 233);

	function body(bytes: Uint8Array): FakeResponse {
		const chunks = [bytes];
		const stream = new ReadableStream<Uint8Array>({
			pull(controller) {
				const next = chunks.shift();
				if (next) controller.enqueue(next);
				else controller.close();
			}
		});
		return { ok: true, status: 200, body: stream } as unknown as FakeResponse;
	}

	function discResponse(): FakeResponse {
		return {
			ok: true,
			status: 200,
			body: null,
			blob: async () => new Blob([disc])
		} as unknown as FakeResponse;
	}

	/** Serves the core check, the disc and the given BIOS responses (the others are 404). */
	function serveSegaCd(found: Record<string, () => Promise<FakeResponse>>, core = 204) {
		serve({
			[check]: async () => response(core),
			[biosU]: async () => response(404),
			[biosE]: async () => response(404),
			[biosJ]: async () => response(404),
			...found,
			[discUrl]: async () => discResponse(),
			[romUrl]: async () => response(200)
		});
	}

	it('loads a disc set from play-disc-set after the core and BIOS checks (Saturn and Sega CD S6)', async () => {
		const setUrl = `/api/games/${game}/roms/12/play-disc-set`;
		const u = firmware(2);
		serveSegaCd({ [biosU]: async () => body(u), [setUrl]: async () => discResponse() });
		const query = new URLSearchParams({
			game,
			rom: '12',
			core: 'genesis_plus_gx',
			name: 'Bay (USA).m3u',
			disc: '1',
			set: '1'
		});
		history.replaceState(null, '', `/emulator/play.html?${query}`);
		const { started } = await import('../../static/emulator/play.js');
		await started;

		expect(urls()).toEqual([check, biosU, biosE, biosJ, setUrl]);
		expect(Object.keys(atLoader?.EJS_externalFiles as object)).toEqual(['/bios_CD_U.bin']);
		expect(atLoader).toMatchObject({ EJS_core: 'genesis_plus_gx', EJS_gameName: 'Bay (USA).m3u' });
	});

	it('checks the core, then asks for U, E and J one after another, then the disc', async () => {
		const u = firmware(1);
		serveSegaCd({ [biosU]: async () => body(u) });

		await play('genesis_plus_gx', name, true);

		expect(urls()).toEqual([check, biosU, biosE, biosJ, discUrl]);
		expect(fetchMock.mock.calls[0][1]).toEqual({ method: 'HEAD', credentials: 'same-origin' });
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/bios_CD_U.bin']);
		expect(new Uint8Array(files['/bios_CD_U.bin'])).toEqual(u);
		expect(atLoader).toMatchObject({
			EJS_core: 'genesis_plus_gx',
			EJS_gameName: name,
			EJS_controlScheme: 'segaMD',
			EJS_hideSettings: ['retroarch_core'],
			EJS_disableCue: false,
			EJS_paths: {
				'genesis_plus_gx-wasm.data':
					'/api/emulator/cores/genesis_plus_gx/genesis_plus_gx-wasm.data',
				'genesis_plus_gx-legacy-wasm.data':
					'/api/emulator/cores/genesis_plus_gx/genesis_plus_gx-legacy-wasm.data',
				'genesis_plus_gx.json': '/api/emulator/cores/genesis_plus_gx/genesis_plus_gx.json'
			}
		});
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe(name);
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	it.each([
		['E only', { E: 2 }],
		['U and J', { U: 1, J: 3 }],
		['all three', { U: 1, E: 2, J: 3 }]
	] as const)('hands over every BIOS found: %s', async (_, seeds) => {
		const urlOf = { U: biosU, E: biosE, J: biosJ };
		const found = Object.entries(seeds) as ['U' | 'E' | 'J', number][];
		serveSegaCd(
			Object.fromEntries(
				found.map(([region, seed]) => [urlOf[region], async () => body(firmware(seed))])
			)
		);

		await play('genesis_plus_gx', name, true);

		expect(urls()).toEqual([check, biosU, biosE, biosJ, discUrl]);
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(found.map(([region]) => `/bios_CD_${region}.bin`));
		for (const [region, seed] of found)
			expect(new Uint8Array(files[`/bios_CD_${region}.bin`])).toEqual(firmware(seed));
	});

	it('stops with the missing message when no BIOS file is there, before the disc or loader', async () => {
		serveSegaCd({});

		expect(await play('genesis_plus_gx', name, true)).toBe(
			'Sega CD games need a BIOS: ask an Admin to put bios_CD_U.bin, bios_CD_E.bin or bios_CD_J.bin in the BIOS folder (Paths:Bios).'
		);
		expect(urls()).toEqual([check, biosU, biosE, biosJ]);
		expect(atLoader).toBeNull();
	});

	it('stops on a wrong-size bios_CD_E.bin after a valid bios_CD_U.bin, without asking for J or the disc', async () => {
		serveSegaCd({
			[biosU]: async () => body(firmware(1)),
			[biosE]: async () => body(firmware(2, 131073))
		});

		expect(await play('genesis_plus_gx', name, true)).toBe(
			'The Sega CD BIOS bios_CD_E.bin in the BIOS folder (Paths:Bios) must be 128 KiB: ask an Admin to replace it.'
		);
		expect(urls()).toEqual([check, biosU, biosE]);
		expect(atLoader).toBeNull();
	});

	it.each([
		[500, "Couldn't load the BIOS (HTTP 500)."],
		[401, 'Your session has expired. Sign in again.']
	])('stops on a BIOS HTTP %i', async (status, message) => {
		serveSegaCd({ [biosU]: async () => response(status) });

		expect(await play('genesis_plus_gx', name, true)).toBe(message);
		expect(urls()).toEqual([check, biosU]);
		expect(atLoader).toBeNull();
	});

	it('stops on a core 404 before any BIOS request', async () => {
		serveSegaCd({}, 404);

		expect(await play('genesis_plus_gx', name, true)).toBe(
			'Genesis and Sega CD games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).'
		);
		expect(urls()).toEqual([check]);
	});

	it('names a disc called like a Sega CD BIOS after the game', async () => {
		serveSegaCd({ [biosJ]: async () => body(firmware(3)) });

		await play('genesis_plus_gx', 'BIOS_CD_U.BIN', true);

		expect((atLoader?.EJS_gameUrl as File).name).toBe(`${game}-BIOS_CD_U.BIN`);
		expect(Object.keys(atLoader?.EJS_externalFiles as object)).toEqual(['/bios_CD_J.bin']);
	});
	it('loads a standalone .chd from play-disc after the BIOS, with cue generation on (CHD B5)', async () => {
		serveSegaCd({ [biosU]: async () => body(firmware(4)) });

		await play('genesis_plus_gx', 'Game.chd', true);

		expect(urls()).toEqual([check, biosU, biosE, biosJ, discUrl]);
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe('Game.chd');
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
		expect(atLoader?.EJS_disableCue).toBe(false);
	});
});

describe('play page TurboGrafx-16', () => {
	it('loads a HuCard from /download with no BIOS request, external files or hidden settings', async () => {
		serve({ [romUrl]: async () => response(200) });

		expect(await play('mednafen_pce', 'Neon Circuit (USA).pce')).toBe('Starting emulator…');

		expect(urls()).toEqual([romUrl]);
		expect(atLoader?.EJS_core).toBe('mednafen_pce');
		expect((atLoader?.EJS_gameUrl as File).name).toBe('Neon Circuit (USA).pce');
		expect(atLoader?.EJS_hideSettings).toBeUndefined();
		expect(atLoader?.EJS_externalFiles).toBeUndefined();
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
	});
});

describe('play page TurboGrafx-CD (TurboGrafx-CD B7, B8)', () => {
	const bios = (name: string) => `/api/emulator/bios/${name}`;
	const cards = ['syscard3.pce', 'syscard3u.pce', 'syscard2.pce', 'syscard2u.pce', 'syscard1.pce'];
	const cardUrls = cards.map(bios);
	const gexpress = bios('gexpress.pce');
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const disc = new Uint8Array([0x50, 0x4b, 3, 4, 9, 9]);
	const name = 'Disc Ninja (Japan).cue';
	const firmware = (seed: number, size = 262144) =>
		new Uint8Array(size).map((_, i) => (i + seed) % 241);

	function body(bytes: Uint8Array): FakeResponse {
		const chunks = [bytes];
		const stream = new ReadableStream<Uint8Array>({
			pull(controller) {
				const next = chunks.shift();
				if (next) controller.enqueue(next);
				else controller.close();
			}
		});
		return { ok: true, status: 200, body: stream } as unknown as FakeResponse;
	}

	function discResponse(): FakeResponse {
		return {
			ok: true,
			status: 200,
			body: null,
			blob: async () => new Blob([disc])
		} as unknown as FakeResponse;
	}

	/** Serves every card and gexpress.pce as 404 unless given, and the disc. */
	function serveCd(found: Record<string, () => Promise<FakeResponse>>) {
		serve({
			...Object.fromEntries([...cardUrls, gexpress].map((url) => [url, async () => response(404)])),
			...found,
			[discUrl]: async () => discResponse()
		});
	}

	it('with only syscard3.pce: asks for it, then gexpress.pce, then the disc, and hands the card over', async () => {
		serveCd({ [cardUrls[0]]: async () => body(firmware(1)) });

		expect(await play('mednafen_pce', name, true)).toBe('Starting emulator…');

		expect(urls()).toEqual([cardUrls[0], gexpress, discUrl]);
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/syscard3.pce']);
		expect(new Uint8Array(files['/syscard3.pce'])).toEqual(firmware(1));
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe(name);
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	it('stops at the first card found, and hands any card over as syscard3.pce', async () => {
		serveCd({
			[bios('syscard2u.pce')]: async () => body(firmware(4)),
			[bios('syscard1.pce')]: async () => body(firmware(5))
		});

		await play('mednafen_pce', name, true);

		expect(urls()).toEqual([...cardUrls.slice(0, 4), gexpress, discUrl]);
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/syscard3.pce']);
		expect(new Uint8Array(files['/syscard3.pce'])).toEqual(firmware(4));
	});

	it('hands over gexpress.pce too when it is there', async () => {
		serveCd({
			[cardUrls[0]]: async () => body(firmware(1)),
			[gexpress]: async () => body(firmware(9, 32768))
		});

		await play('mednafen_pce', name, true);

		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/syscard3.pce', '/gexpress.pce']);
		expect(new Uint8Array(files['/gexpress.pce'])).toEqual(firmware(9, 32768));
	});

	it('accepts a card with a 512-byte copier header (256 KiB + 512) and passes it whole (B8)', async () => {
		serveCd({ [cardUrls[0]]: async () => body(firmware(2, 262656)) });

		await play('mednafen_pce', name, true);

		const card = (atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>)['/syscard3.pce'];
		expect(card.byteLength).toBe(262656);
		expect(new Uint8Array(card)).toEqual(firmware(2, 262656));
	});

	it('stops with the missing message when no card is there, before gexpress.pce, the disc or the loader', async () => {
		serveCd({ [gexpress]: async () => body(firmware(9, 32768)) });

		expect(await play('mednafen_pce', name, true)).toBe(
			'TurboGrafx-CD games need a System Card BIOS: ask an Admin to put syscard3.pce in the BIOS folder (Paths:Bios).'
		);
		expect(urls()).toEqual(cardUrls);
		expect(atLoader).toBeNull();
	});

	it.each([
		['empty', 0],
		['over 1 MiB', 1048577]
	])('stops on a card that is %s, naming it, with no later request', async (_, size) => {
		serveCd({ [cardUrls[1]]: async () => body(firmware(3, size)) });

		expect(await play('mednafen_pce', name, true)).toBe(
			'The TurboGrafx-CD BIOS syscard3u.pce in the BIOS folder (Paths:Bios) is empty or larger than 1 MiB: ask an Admin to replace it.'
		);
		expect(urls()).toEqual(cardUrls.slice(0, 2));
		expect(atLoader).toBeNull();
	});

	it('stops on a card HTTP 500 with the BIOS message', async () => {
		serveCd({ [cardUrls[0]]: async () => response(500) });

		expect(await play('mednafen_pce', name, true)).toBe("Couldn't load the BIOS (HTTP 500).");
		expect(urls()).toEqual([cardUrls[0]]);
		expect(atLoader).toBeNull();
	});

	it.each([
		['empty', 0],
		['over 1 MiB', 1048577]
	])('stops on a gexpress.pce that is %s before the disc', async (_, size) => {
		serveCd({
			[cardUrls[0]]: async () => body(firmware(1)),
			[gexpress]: async () => body(firmware(9, size))
		});

		expect(await play('mednafen_pce', name, true)).toBe(
			'The TurboGrafx-CD BIOS gexpress.pce in the BIOS folder (Paths:Bios) is empty or larger than 1 MiB: ask an Admin to replace it.'
		);
		expect(urls()).toEqual([cardUrls[0], gexpress]);
		expect(atLoader).toBeNull();
	});

	it('stops on a gexpress.pce HTTP 500 before the disc', async () => {
		serveCd({
			[cardUrls[0]]: async () => body(firmware(1)),
			[gexpress]: async () => response(500)
		});

		expect(await play('mednafen_pce', name, true)).toBe("Couldn't load the BIOS (HTTP 500).");
		expect(urls()).toEqual([cardUrls[0], gexpress]);
		expect(atLoader).toBeNull();
	});

	it('hides pce_cdbios, sets no core option and leaves cue handling as upstream', async () => {
		serveCd({ [cardUrls[0]]: async () => body(firmware(1)) });

		await play('mednafen_pce', 'Disc Ninja (Japan).chd', true);

		expect(atLoader?.EJS_hideSettings).toEqual(['pce_cdbios']);
		expect(atLoader?.EJS_disableCue).toBeUndefined();
		expect(atLoader?.EJS_defaultOptions).not.toHaveProperty('pce_cdbios');
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
	});

	it('names a disc called like a System Card after the game', async () => {
		serveCd({ [cardUrls[0]]: async () => body(firmware(1)) });

		await play('mednafen_pce', 'SYSCARD3.PCE', true);

		expect((atLoader?.EJS_gameUrl as File).name).toBe(`${game}-SYSCARD3.PCE`);
	});
});

describe('play page SNES (SNES B3)', () => {
	const check = '/api/emulator/cores/snes9x';

	it.each(['Mode Seven Rally (USA).sfc', 'Legacy Cart (USA).smc'])(
		'checks the core files with one HEAD, then loads %s with the API paths and upstream controls',
		async (name) => {
			serve({ [check]: async () => response(204), [romUrl]: async () => response(200) });

			await play('snes9x', name);

			expect(urls()).toEqual([check, romUrl]);
			expect(fetchMock.mock.calls[0][1]).toEqual({ method: 'HEAD', credentials: 'same-origin' });
			expect(fetchMock.mock.calls[1][1]).toEqual({ credentials: 'same-origin' });
			expect(atLoader).toMatchObject({
				EJS_core: 'snes9x',
				EJS_gameName: name,
				EJS_disableDatabases: true,
				EJS_hideSettings: ['retroarch_core'],
				EJS_defaultOptions: { 'save-save-interval': '30' },
				EJS_paths: {
					'snes9x-wasm.data': '/api/emulator/cores/snes9x/snes9x-wasm.data',
					'snes9x-legacy-wasm.data': '/api/emulator/cores/snes9x/snes9x-legacy-wasm.data',
					'snes9x.json': '/api/emulator/cores/snes9x/snes9x.json'
				}
			});
			expect(atLoader?.EJS_controlScheme).toBeUndefined();
			expect(atLoader?.EJS_threads).toBeUndefined();
			expect(atLoader?.EJS_biosUrl).toBeUndefined();
			expect(atLoader?.EJS_externalFiles).toBeUndefined();
			expect(atLoader?.EJS_disableCue).toBeUndefined();
			expect((atLoader?.EJS_gameUrl as File).name).toBe(name);
		}
	);

	it.each([
		[
			404,
			'SNES games need the Snes9x core files: ask an Admin to set them up (Emulator:Snes9xCorePath).'
		],
		[
			409,
			"The Snes9x core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:Snes9xCorePath)."
		],
		[401, 'Your session has expired. Sign in again.'],
		[500, "Couldn't load the Snes9x core (HTTP 500)."]
	])('stops on HTTP %i before fetching the ROM or starting the loader', async (status, message) => {
		serve({ [check]: async () => response(status), [romUrl]: async () => response(200) });

		expect(await play('snes9x', 'Mode Seven Rally (USA).sfc')).toBe(message);
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});

	it('stops when the availability check fails to reach the server', async () => {
		serve({
			[check]: async () => Promise.reject(new TypeError('network')),
			[romUrl]: async () => response(200)
		});

		expect(await play('snes9x', 'Mode Seven Rally (USA).sfc')).toBe(
			"Couldn't load the Snes9x core."
		);
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});

	it.each([
		[401, 'Your session has expired. Sign in again.'],
		[500, "Couldn't load the ROM (HTTP 500)."]
	])('keeps the ROM error for HTTP %i after a passing check', async (status, message) => {
		serve({ [check]: async () => response(204), [romUrl]: async () => response(status) });

		expect(await play('snes9x', 'Mode Seven Rally (USA).sfc')).toBe(message);
		expect(urls()).toEqual([check, romUrl]);
		expect(atLoader).toBeNull();
	});

	it.each([
		['request', () => Promise.reject(new TypeError('network'))],
		[
			'body read',
			async () => ({
				...response(200),
				blob: () => Promise.reject(new TypeError('read'))
			})
		]
	])('shows the generic ROM error when the ROM %s fails', async (_, rom) => {
		serve({ [check]: async () => response(204), [romUrl]: rom });

		expect(await play('snes9x', 'Mode Seven Rally (USA).sfc')).toBe("Couldn't load the ROM.");
		expect(urls()).toEqual([check, romUrl]);
		expect(atLoader).toBeNull();
	});
});

describe('play page Commodore 64 (C64 B7)', () => {
	const check = '/api/emulator/cores/vice_x64sc';

	it.each(['Pixel Pilot (Europe).crt', 'Acrojet (Europe) (Side 2).tap', 'Disk Demo (Europe).nib'])(
		'checks the core files with one HEAD, then loads %s from the ZIP with the API paths',
		async (name) => {
			serve({ [check]: async () => response(204), [romUrl]: async () => response(200) });

			await play('vice_x64sc', name);

			expect(urls()).toEqual([check, romUrl]);
			expect(atLoader).toMatchObject({
				EJS_core: 'vice_x64sc',
				EJS_gameName: name,
				EJS_hideSettings: ['retroarch_core'],
				EJS_paths: {
					'vice_x64sc-wasm.data': '/api/emulator/cores/vice_x64sc/vice_x64sc-wasm.data',
					'vice_x64sc-legacy-wasm.data':
						'/api/emulator/cores/vice_x64sc/vice_x64sc-legacy-wasm.data',
					'vice_x64sc.json': '/api/emulator/cores/vice_x64sc/vice_x64sc.json'
				}
			});
			expect(atLoader?.EJS_biosUrl).toBeUndefined();
			expect(atLoader?.EJS_controlScheme).toBeUndefined();
			expect((atLoader?.EJS_gameUrl as File).name).toBe(name);
		}
	);

	it('stops on a missing core before fetching the ROM', async () => {
		serve({ [check]: async () => response(404), [romUrl]: async () => response(200) });

		expect(await play('vice_x64sc', 'Pixel Pilot (Europe).crt')).toBe(
			'Commodore 64 games need the VICE core files: ask an Admin to set them up (Emulator:ViceCorePath).'
		);
		expect(urls()).toEqual([check]);
		expect(atLoader).toBeNull();
	});
});

describe('play page 3DO (3DO B8)', () => {
	const check = '/api/emulator/cores/opera';
	const names = ['panafz10.bin', 'panafz1.bin', 'panafz1j.bin', 'goldstar.bin', 'sanyotry.bin'];
	const biosUrls = names.map((name) => `/api/emulator/bios/${name}`);
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const disc = new Uint8Array([0x50, 0x4b, 3, 4, 3, 0xd0]);
	const name = 'Opal Rally (USA).cue';
	const firmware = (seed: number, size = 1048576) => new Uint8Array(size).fill(seed);
	/** 1 MiB buffers: a byte compare, since a deep equality of a million elements is too slow. */
	const sameBytes = (actual: ArrayBuffer, expected: Uint8Array) =>
		Buffer.compare(Buffer.from(actual), Buffer.from(expected)) === 0;

	function body(bytes: Uint8Array): FakeResponse {
		const chunks = [bytes];
		const stream = new ReadableStream<Uint8Array>({
			pull(controller) {
				const next = chunks.shift();
				if (next) controller.enqueue(next);
				else controller.close();
			}
		});
		return { ok: true, status: 200, body: stream } as unknown as FakeResponse;
	}

	function discResponse(): FakeResponse {
		return {
			ok: true,
			status: 200,
			body: null,
			blob: async () => new Blob([disc])
		} as unknown as FakeResponse;
	}

	/** Serves the core check, the disc and the given BIOS responses (the others are 404). */
	function serveThreeDo(found: Record<string, () => Promise<FakeResponse>>, core = 204) {
		serve({
			[check]: async () => response(core),
			...Object.fromEntries(biosUrls.map((url) => [url, async () => response(404)])),
			...found,
			[discUrl]: async () => discResponse(),
			[romUrl]: async () => response(200)
		});
	}

	it('checks the core, then asks for the five BIOS files one after another, then the disc', async () => {
		const goldstar = firmware(4);
		serveThreeDo({ [biosUrls[3]]: async () => body(goldstar) });

		await play('opera', name, true);

		expect(urls()).toEqual([check, ...biosUrls, discUrl]);
		expect(fetchMock.mock.calls[0][1]).toEqual({ method: 'HEAD', credentials: 'same-origin' });
		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(['/goldstar.bin']);
		expect(sameBytes(files['/goldstar.bin'], goldstar)).toBe(true);
		expect(atLoader).toMatchObject({
			EJS_core: 'opera',
			EJS_gameName: name,
			EJS_hideSettings: ['retroarch_core'],
			EJS_defaultOptions: { 'save-save-interval': '30' },
			EJS_paths: {
				'opera-wasm.data': '/api/emulator/cores/opera/opera-wasm.data',
				'opera-legacy-wasm.data': '/api/emulator/cores/opera/opera-legacy-wasm.data',
				'opera.json': '/api/emulator/cores/opera/opera.json'
			}
		});
		expect(Object.keys(atLoader?.EJS_defaultOptions as object)).toEqual(['save-save-interval']);
		expect(atLoader?.EJS_disableCue).toBeUndefined();
		expect(atLoader?.EJS_controlScheme).toBeUndefined();
		expect(atLoader?.EJS_biosUrl).toBeUndefined();
		expect((atLoader?.EJS_Buttons as Record<string, boolean>).exitEmulation).toBe(true);
		const file = atLoader?.EJS_gameUrl as File;
		expect(file.name).toBe(name);
		expect(new Uint8Array(await file.arrayBuffer())).toEqual(disc);
	});

	it('hands over all five BIOS files when all are there', async () => {
		serveThreeDo(
			Object.fromEntries(biosUrls.map((url, i) => [url, async () => body(firmware(i + 1))]))
		);

		await play('opera', name, true);

		const files = atLoader?.EJS_externalFiles as Record<string, ArrayBuffer>;
		expect(Object.keys(files)).toEqual(names.map((n) => `/${n}`));
		names.forEach((n, i) => expect(sameBytes(files[`/${n}`], firmware(i + 1)), n).toBe(true));
	});

	it('stops with the missing message when no BIOS file is there, before the disc or loader', async () => {
		serveThreeDo({});

		expect(await play('opera', name, true)).toBe(
			'3DO games need a BIOS: ask an Admin to put panafz10.bin, panafz1.bin, panafz1j.bin, goldstar.bin or sanyotry.bin in the BIOS folder (Paths:Bios).'
		);
		expect(urls()).toEqual([check, ...biosUrls]);
		expect(atLoader).toBeNull();
	});

	it('stops on a wrong-size panafz1.bin after a valid panafz10.bin, without later requests', async () => {
		serveThreeDo({
			[biosUrls[0]]: async () => body(firmware(1)),
			[biosUrls[1]]: async () => body(firmware(2, 1048577))
		});

		expect(await play('opera', name, true)).toBe(
			'The 3DO BIOS panafz1.bin in the BIOS folder (Paths:Bios) must be 1 MiB: ask an Admin to replace it.'
		);
		expect(urls()).toEqual([check, biosUrls[0], biosUrls[1]]);
		expect(atLoader).toBeNull();
	});

	it('stops on a BIOS HTTP 500', async () => {
		serveThreeDo({ [biosUrls[0]]: async () => response(500) });

		expect(await play('opera', name, true)).toBe("Couldn't load the BIOS (HTTP 500).");
		expect(urls()).toEqual([check, biosUrls[0]]);
		expect(atLoader).toBeNull();
	});

	it('stops on a core 404 before any BIOS request', async () => {
		serveThreeDo({}, 404);

		expect(await play('opera', name, true)).toBe(
			'3DO games need the Opera core files: ask an Admin to set them up (Emulator:OperaCorePath).'
		);
		expect(urls()).toEqual([check]);
	});

	it('loads from play-disc without disc=1, never /download', async () => {
		serveThreeDo({ [biosUrls[0]]: async () => body(firmware(1)) });

		await play('opera', 'Opal Solo (USA).iso');

		expect(urls()).toEqual([check, ...biosUrls, discUrl]);
		expect(urls()).not.toContain(romUrl);
	});

	it('names a disc called like a 3DO BIOS after the game', async () => {
		serveThreeDo({ [biosUrls[4]]: async () => body(firmware(5)) });

		await play('opera', 'PANAFZ10.BIN', true);

		expect((atLoader?.EJS_gameUrl as File).name).toBe(`${game}-PANAFZ10.BIN`);
	});

	it('keeps Exit hidden for other cores', async () => {
		serve({
			['/api/emulator/cores/snes9x']: async () => response(204),
			[romUrl]: async () => response(200)
		});

		await play('snes9x', 'Mode Seven Rally (USA).sfc');

		expect((atLoader?.EJS_Buttons as Record<string, boolean>).exitEmulation).toBe(false);
	});
});

describe('play page 3DO saves (3DO B11)', () => {
	const discUrl = `/api/games/${game}/roms/12/play-disc`;
	const close = { type: 'rnrm-emulator-close' };
	const closed = { type: 'rnrm-emulator-closed' };

	let restart: ReturnType<typeof vi.fn>;
	let syncfs: ReturnType<typeof vi.fn>;
	let posted: ReturnType<typeof vi.spyOn>;

	async function playCore(core: string, started = true) {
		const check = `/api/emulator/cores/${core}`;
		const firmware = new Uint8Array(1048576);
		const stream = () =>
			({
				ok: true,
				status: 200,
				body: new ReadableStream<Uint8Array>({
					start(controller) {
						controller.enqueue(firmware);
						controller.close();
					}
				})
			}) as unknown as FakeResponse;
		serve({
			[check]: async () => response(204),
			'/api/emulator/bios/panafz10.bin': async () => stream(),
			'/api/emulator/bios/panafz1.bin': async () => response(404),
			'/api/emulator/bios/panafz1j.bin': async () => response(404),
			'/api/emulator/bios/goldstar.bin': async () => response(404),
			'/api/emulator/bios/sanyotry.bin': async () => response(404),
			[discUrl]: async () => response(200),
			[romUrl]: async () => response(200)
		});
		await play(
			core,
			core === 'opera' ? 'Opal Rally (USA).cue' : 'Mode Seven Rally (USA).sfc',
			true
		);
		restart = vi.fn();
		syncfs = vi.fn();
		if (started) win.EJS_emulator = { gameManager: { restart, FS: { syncfs } } };
		posted = vi.spyOn(window, 'postMessage').mockImplementation(() => {});
		vi.useFakeTimers();
	}

	function send(data: unknown, init: Partial<MessageEventInit> = {}) {
		window.dispatchEvent(
			new MessageEvent('message', { data, origin: location.origin, source: window, ...init })
		);
	}

	afterEach(() => vi.useRealTimers());

	it.each([
		['without an error', undefined],
		['with an error', new Error('quota')]
	])('resets, syncs after 250 ms, then replies once (%s)', async (_, error) => {
		await playCore('opera');

		send(close);
		expect(restart).toHaveBeenCalledTimes(1);
		expect(syncfs).not.toHaveBeenCalled();
		await vi.advanceTimersByTimeAsync(249);
		expect(syncfs).not.toHaveBeenCalled();
		await vi.advanceTimersByTimeAsync(1);
		expect(syncfs).toHaveBeenCalledTimes(1);
		expect(syncfs.mock.calls[0][0]).toBe(false);
		expect(posted).not.toHaveBeenCalled();

		syncfs.mock.calls[0][1](error);
		await vi.advanceTimersByTimeAsync(0);
		expect(posted).toHaveBeenCalledTimes(1);
		expect(posted).toHaveBeenCalledWith(closed, location.origin);
	});

	it('answers two requests during one save with one reset and one reply', async () => {
		await playCore('opera');

		send(close);
		send(close);
		await vi.advanceTimersByTimeAsync(250);
		syncfs.mock.calls[0][1]();
		await vi.advanceTimersByTimeAsync(0);

		expect(restart).toHaveBeenCalledTimes(1);
		expect(syncfs).toHaveBeenCalledTimes(1);
		expect(posted).toHaveBeenCalledTimes(1);
	});

	it('replies at once before the game has started', async () => {
		await playCore('opera', false);

		send(close);

		expect(posted).toHaveBeenCalledTimes(1);
		expect(posted).toHaveBeenCalledWith(closed, location.origin);
	});

	it.each([
		['another origin', close, { origin: 'https://evil.example' }],
		['another source', close, { source: null }],
		['other data', { type: 'rnrm-emulator-close', extra: 1 }, {}],
		['a string', 'rnrm-emulator-close', {}],
		['the reply', closed, {}]
	])('ignores %s', async (_, data, init) => {
		await playCore('opera');

		send(data, init as Partial<MessageEventInit>);
		await vi.advanceTimersByTimeAsync(500);

		expect(restart).not.toHaveBeenCalled();
		expect(posted).not.toHaveBeenCalled();
	});

	it('does nothing on another core', async () => {
		await playCore('snes9x');

		send(close);
		await vi.advanceTimersByTimeAsync(500);

		expect(restart).not.toHaveBeenCalled();
		expect(posted).not.toHaveBeenCalled();
	});
});

describe('play page library saves (Saves B17, B18)', () => {
	const saveId = 'a'.repeat(32);
	const saveUrl = `/api/games/${game}/saves/${saveId}/download`;
	const saveBytes = new Uint8Array([7, 8, 9]);

	function saveResponse(status: number) {
		return async () =>
			({
				ok: status >= 200 && status < 300,
				status,
				arrayBuffer: async () => saveBytes.slice().buffer
			}) as unknown as FakeResponse;
	}

	async function playWithSave(kind: 'state' | 'battery', status = 200): Promise<string> {
		serve({ [romUrl]: async () => response(200), [saveUrl]: saveResponse(status) });
		const query = new URLSearchParams({
			game,
			rom: '12',
			core: 'gambatte',
			name: 'Orbit Blocks (World).gb',
			save: saveId,
			saveKind: kind
		});
		history.replaceState(null, '', `/emulator/play.html?${query}`);
		const { started } = await import('../../static/emulator/play.js');
		await started;
		return document.getElementById('status')!.textContent ?? '';
	}

	/** A fake EmulatorJS 4.2.3 game manager recording what is called, in order. */
	function manager(
		options: {
			states?: boolean;
			existing?: string[];
			saveFile?: Uint8Array | null;
			path?: string;
		} = {}
	) {
		const order: string[] = [];
		const existing = new Set(options.existing ?? []);
		const fake = {
			order,
			supportsStates: vi.fn(() => options.states ?? true),
			getState: vi.fn(() => new Uint8Array([1, 2, 3])),
			loadState: vi.fn((state: Uint8Array) => void order.push(`loadState ${[...state]}`)),
			getSaveFile: vi.fn(() =>
				options.saveFile === undefined ? new Uint8Array([4, 5]) : options.saveFile
			),
			getSaveFilePath: vi.fn(() => options.path ?? '/data/saves/Orbit Blocks (World).srm'),
			loadSaveFiles: vi.fn(() => void order.push('loadSaveFiles')),
			restart: vi.fn(() => void order.push('restart')),
			FS: {
				analyzePath: vi.fn((p: string) => ({ exists: existing.has(p) })),
				mkdir: vi.fn((p: string) => void order.push(`mkdir ${p}`)),
				unlink: vi.fn((p: string) => void order.push(`unlink ${p}`)),
				writeFile: vi.fn(
					(p: string, data: Uint8Array) => void order.push(`writeFile ${p} ${[...data]}`)
				),
				syncfs: vi.fn((populate: boolean) => void order.push(`syncfs ${populate}`))
			}
		};
		win.EJS_emulator = { gameManager: fake };
		return fake;
	}

	const gameStart = () => (atLoader?.EJS_onGameStart as () => void)();

	afterEach(() => vi.useRealTimers());

	it('fetches the save after the ROM and before the loader', async () => {
		expect(await playWithSave('state')).toBe('Starting emulator…');
		expect(urls()).toEqual([romUrl, saveUrl]);
		expect(atLoader?.EJS_onGameStart).toBeTypeOf('function');
	});

	it.each([
		[401, 'Your session has expired. Sign in again.'],
		[404, "Couldn't load the save."],
		[500, "Couldn't load the save."]
	])('a %i on the save stops before the loader', async (status, message) => {
		expect(await playWithSave('state', status)).toBe(message);
		expect(atLoader).toBeNull();
	});

	it('keeps a start handler set before the page ran', async () => {
		const earlier = vi.fn();
		win.EJS_onGameStart = earlier;
		await playWithSave('state');
		manager();
		// Fake timers drop the 10 ms save apply with the test; a real one would fire into a later test's manager.
		vi.useFakeTimers();

		gameStart();

		expect(earlier).toHaveBeenCalledTimes(1);
	});

	it('applies a state once, 10 ms after start, even if start fires twice', async () => {
		await playWithSave('state');
		const fake = manager();
		vi.useFakeTimers();

		gameStart();
		gameStart();
		await vi.advanceTimersByTimeAsync(9);
		expect(fake.loadState).not.toHaveBeenCalled();
		await vi.advanceTimersByTimeAsync(1);

		expect(fake.order).toEqual(['loadState 7,8,9']);
	});

	it('does not load a state the core cannot, and says so', async () => {
		await playWithSave('state');
		const fake = manager({ states: false });
		vi.useFakeTimers();

		gameStart();
		await vi.advanceTimersByTimeAsync(10);

		expect(fake.loadState).not.toHaveBeenCalled();
		expect(document.getElementById('status')!.textContent).toBe(
			"This emulator can't load save states."
		);
	});

	it('applies a battery save like the upstream import, then restarts and syncs', async () => {
		await playWithSave('battery');
		const fake = manager({ existing: ['/data', '/data/saves/Orbit Blocks (World).srm'] });
		vi.useFakeTimers();

		gameStart();
		await vi.advanceTimersByTimeAsync(10);

		expect(fake.order).toEqual([
			'mkdir /data/saves',
			'unlink /data/saves/Orbit Blocks (World).srm',
			'writeFile /data/saves/Orbit Blocks (World).srm 7,8,9',
			'loadSaveFiles',
			'restart',
			'syncfs false'
		]);
	});

	it('reports a failure to apply the save and keeps the game running', async () => {
		await playWithSave('state');
		const fake = manager();
		fake.loadState.mockImplementation(() => {
			throw new Error('bad state');
		});
		vi.useFakeTimers();

		gameStart();
		await vi.advanceTimersByTimeAsync(10);

		expect(document.getElementById('status')!.textContent).toBe("Couldn't load the save.");
	});

	describe('exports', () => {
		let posted: ReturnType<typeof vi.spyOn>;

		async function ready(
			core = 'gambatte',
			options: Parameters<typeof manager>[0] = {},
			start = true
		) {
			serve({ [romUrl]: async () => response(200) });
			await play(core, 'Orbit Blocks (World).gb');
			const fake = manager(options);
			if (start) gameStart();
			posted = vi.spyOn(window, 'postMessage').mockImplementation(() => {});
			return fake;
		}

		function send(data: unknown, init: Partial<MessageEventInit> = {}) {
			window.dispatchEvent(
				new MessageEvent('message', { data, origin: location.origin, source: window, ...init })
			);
		}

		const result = () => posted.mock.calls[0][0] as Record<string, unknown>;

		it('sends the state as a transferred buffer with the .state extension', async () => {
			await ready();

			send({ type: 'rnrm-emulator-export', kind: 'state' });

			expect(posted).toHaveBeenCalledTimes(1);
			const [message, origin, transfer] = posted.mock.calls[0];
			expect(origin).toBe(location.origin);
			expect(message).toMatchObject({
				type: 'rnrm-emulator-export-result',
				kind: 'state',
				ok: true,
				extension: '.state'
			});
			expect([...new Uint8Array((message as { bytes: ArrayBuffer }).bytes)]).toEqual([1, 2, 3]);
			expect(transfer).toEqual([(message as { bytes: ArrayBuffer }).bytes]);
		});

		it.each([
			['/data/saves/Orbit Blocks (World).SAV', '.sav'],
			['/data/saves/noextension', '.srm']
		])('sends the battery save, its extension from %s', async (path, extension) => {
			await ready('gambatte', { path });

			send({ type: 'rnrm-emulator-export', kind: 'battery' });

			expect(result()).toMatchObject({ kind: 'battery', ok: true, extension });
			expect([...new Uint8Array(result().bytes as ArrayBuffer)]).toEqual([4, 5]);
		});

		it.each([
			['not-started', 'state', {}, false],
			['unsupported', 'state', { states: false }, true],
			['empty', 'battery', { saveFile: null }, true]
		] as const)('answers %s', async (reason, kind, options, start) => {
			await ready('gambatte', options, start);

			send({ type: 'rnrm-emulator-export', kind });

			expect(result()).toEqual({ type: 'rnrm-emulator-export-result', kind, ok: false, reason });
		});

		it('answers error when the emulator throws', async () => {
			const fake = await ready();
			fake.getState.mockImplementation(() => {
				throw new Error('boom');
			});

			send({ type: 'rnrm-emulator-export', kind: 'state' });

			expect(result()).toEqual({
				type: 'rnrm-emulator-export-result',
				kind: 'state',
				ok: false,
				reason: 'error'
			});
		});

		it('answers busy while the 3DO close sync runs', async () => {
			const firmware = new Uint8Array(1048576);
			serve({
				'/api/emulator/cores/opera': async () => response(204),
				'/api/emulator/bios/panafz10.bin': async () =>
					({
						ok: true,
						status: 200,
						body: new ReadableStream<Uint8Array>({
							start(controller) {
								controller.enqueue(firmware);
								controller.close();
							}
						})
					}) as unknown as FakeResponse,
				'/api/emulator/bios/panafz1.bin': async () => response(404),
				'/api/emulator/bios/panafz1j.bin': async () => response(404),
				'/api/emulator/bios/goldstar.bin': async () => response(404),
				'/api/emulator/bios/sanyotry.bin': async () => response(404),
				[`/api/games/${game}/roms/12/play-disc`]: async () => response(200)
			});
			await play('opera', 'Opal Rally (USA).cue', true);
			manager();
			gameStart();
			posted = vi.spyOn(window, 'postMessage').mockImplementation(() => {});
			vi.useFakeTimers();

			send({ type: 'rnrm-emulator-close' });
			send({ type: 'rnrm-emulator-export', kind: 'state' });

			expect(result()).toEqual({
				type: 'rnrm-emulator-export-result',
				kind: 'state',
				ok: false,
				reason: 'busy'
			});
		});

		it.each([
			[
				'another origin',
				{ type: 'rnrm-emulator-export', kind: 'state' },
				{ origin: 'https://evil.example' }
			],
			['another source', { type: 'rnrm-emulator-export', kind: 'state' }, { source: null }],
			['an unknown kind', { type: 'rnrm-emulator-export', kind: 'sram' }, {}],
			['extra keys', { type: 'rnrm-emulator-export', kind: 'state', extra: 1 }, {}]
		])('ignores %s', async (_, data, init) => {
			await ready();

			send(data, init as Partial<MessageEventInit>);

			expect(posted).not.toHaveBeenCalled();
		});

		describe('screenshot (E2)', () => {
			const png = new Uint8Array([137, 80, 78, 71]);
			/** EmulatorJS 4.2.3's `screenshot(callback, source, format, upscale)`; the callback is held until `answer`. */
			function camera() {
				let callback: ((blob: Blob, format: string) => void) | null = null;
				const screenshot = vi.fn((done: (blob: Blob, format: string) => void) => {
					callback = done;
				});
				(win.EJS_emulator as Record<string, unknown>).screenshot = screenshot;
				return {
					screenshot,
					answer: (blob: Blob = new Blob([png], { type: 'image/png' })) => callback!(blob, 'png')
				};
			}

			it('asks EmulatorJS for a canvas PNG at upscale 1 and answers with its bytes, transferred', async () => {
				await ready();
				const { screenshot, answer } = camera();

				send({ type: 'rnrm-emulator-export', kind: 'screenshot' });
				expect(screenshot).toHaveBeenCalledWith(expect.any(Function), 'canvas', 'png', 1);
				expect(posted).not.toHaveBeenCalled();
				answer();

				await vi.waitFor(() => expect(posted).toHaveBeenCalledTimes(1));
				const [message, origin, transfer] = posted.mock.calls[0];
				expect(origin).toBe(location.origin);
				expect(Object.keys(message as object).sort()).toEqual([
					'bytes',
					'extension',
					'kind',
					'ok',
					'type'
				]);
				expect(message).toMatchObject({
					type: 'rnrm-emulator-export-result',
					kind: 'screenshot',
					ok: true,
					extension: '.png'
				});
				expect([...new Uint8Array((message as { bytes: ArrayBuffer }).bytes)]).toEqual([...png]);
				expect(transfer).toEqual([(message as { bytes: ArrayBuffer }).bytes]);
			});

			it('answers not-started before the game starts, without calling screenshot', async () => {
				await ready('gambatte', {}, false);
				const { screenshot } = camera();

				send({ type: 'rnrm-emulator-export', kind: 'screenshot' });

				expect(screenshot).not.toHaveBeenCalled();
				expect(result()).toEqual({
					type: 'rnrm-emulator-export-result',
					kind: 'screenshot',
					ok: false,
					reason: 'not-started'
				});
			});

			it('answers error when screenshot throws or the blob cannot be read', async () => {
				await ready();
				const { screenshot, answer } = camera();
				screenshot.mockImplementationOnce(() => {
					throw new Error('boom');
				});
				send({ type: 'rnrm-emulator-export', kind: 'screenshot' });
				expect(result()).toMatchObject({ kind: 'screenshot', ok: false, reason: 'error' });

				send({ type: 'rnrm-emulator-export', kind: 'screenshot' });
				const broken = new Blob([png]);
				broken.arrayBuffer = () => Promise.reject(new Error('unreadable'));
				answer(broken);
				await vi.waitFor(() => expect(posted).toHaveBeenCalledTimes(2));
				expect(posted.mock.calls[1][0]).toMatchObject({
					kind: 'screenshot',
					ok: false,
					reason: 'error'
				});
			});

			it('a 3DO close request during a pending screenshot runs as usual, and the screenshot still replies', async () => {
				const firmware = new Uint8Array(1048576);
				serve({
					'/api/emulator/cores/opera': async () => response(204),
					'/api/emulator/bios/panafz10.bin': async () =>
						({
							ok: true,
							status: 200,
							body: new ReadableStream<Uint8Array>({
								start(controller) {
									controller.enqueue(firmware);
									controller.close();
								}
							})
						}) as unknown as FakeResponse,
					'/api/emulator/bios/panafz1.bin': async () => response(404),
					'/api/emulator/bios/panafz1j.bin': async () => response(404),
					'/api/emulator/bios/goldstar.bin': async () => response(404),
					'/api/emulator/bios/sanyotry.bin': async () => response(404),
					[`/api/games/${game}/roms/12/play-disc`]: async () => response(200)
				});
				await play('opera', 'Opal Rally (USA).cue', true);
				const fake = manager();
				gameStart();
				const { answer } = camera();
				posted = vi.spyOn(window, 'postMessage').mockImplementation(() => {});

				send({ type: 'rnrm-emulator-export', kind: 'screenshot' });
				send({ type: 'rnrm-emulator-close' });
				expect(fake.restart).toHaveBeenCalledTimes(1);
				answer();

				await vi.waitFor(() =>
					expect(
						posted.mock.calls.map((c: unknown[]) => (c[0] as { type: string }).type)
					).toContain('rnrm-emulator-export-result')
				);
			});
		});
	});
});

describe('play page touch controls (E1)', () => {
	function touch(coarse: boolean) {
		vi.stubGlobal(
			'matchMedia',
			vi.fn((query: string) => ({ matches: coarse && query === '(pointer: coarse)' }))
		);
	}

	async function start(setting?: string) {
		serve({ [romUrl]: async () => response(200) });
		await play('gambatte', 'Orbit Blocks (World).gb');
		const toggleVirtualGamepad = vi.fn();
		win.EJS_emulator = {
			gameManager: {},
			toggleVirtualGamepad,
			getSettingValue: vi.fn((id: string) => (id === 'virtual-gamepad' ? setting : undefined))
		};
		(atLoader?.EJS_onGameStart as () => void)();
		return toggleVirtualGamepad;
	}

	it('a touch-first device enables the gamepad by default and shows it at start', async () => {
		touch(true);
		const toggle = await start();

		expect(atLoader?.EJS_defaultOptions).toEqual({
			'save-save-interval': '30',
			'virtual-gamepad': 'enabled'
		});
		expect(toggle).toHaveBeenCalledWith(true);
	});

	it('also for a game started with a library save to apply', async () => {
		touch(true);
		const saveId = 'a'.repeat(32);
		serve({
			[romUrl]: async () => response(200),
			[`/api/games/${game}/saves/${saveId}/download`]: async () =>
				({
					ok: true,
					status: 200,
					arrayBuffer: async () => new ArrayBuffer(1)
				}) as unknown as FakeResponse
		});
		const query = new URLSearchParams({
			game,
			rom: '12',
			core: 'gambatte',
			name: 'Orbit Blocks (World).gb',
			save: saveId,
			saveKind: 'state'
		});
		history.replaceState(null, '', `/emulator/play.html?${query}`);
		await (
			await import('../../static/emulator/play.js')
		).started;
		const toggleVirtualGamepad = vi.fn();
		win.EJS_emulator = { gameManager: {}, toggleVirtualGamepad, getSettingValue: () => undefined };
		vi.useFakeTimers();
		(atLoader?.EJS_onGameStart as () => void)();
		vi.useRealTimers();

		expect(toggleVirtualGamepad).toHaveBeenCalledWith(true);
	});

	it("keeps it off when the user disabled it in EmulatorJS's settings", async () => {
		touch(true);
		expect(await start('disabled')).not.toHaveBeenCalled();
	});

	it('a mouse or trackpad device changes nothing', async () => {
		touch(false);
		const toggle = await start();

		expect(atLoader?.EJS_defaultOptions).toEqual({ 'save-save-interval': '30' });
		expect(toggle).not.toHaveBeenCalled();
	});
});

describe('play page Nintendo DS (Nintendo DS B4)', () => {
	const buttons = {
		saveState: false,
		loadState: false,
		quickSave: false,
		quickLoad: false,
		netplay: false,
		cheat: false,
		exitEmulation: false
	};

	it('loads the cartridge through the download route with the DS defaults and no BIOS', async () => {
		serve({ [romUrl]: async () => response(200) });

		await play('desmume', 'Marker Test (World).nds');

		expect(urls()).toEqual([romUrl]);
		expect(atLoader).toMatchObject({
			EJS_core: 'desmume',
			EJS_gameName: 'Marker Test (World).nds',
			EJS_disableDatabases: true
		});
		expect(atLoader?.EJS_hideSettings).toEqual(['retroarch_core', 'ejs_threads']);
		expect(atLoader?.EJS_defaultOptions).toEqual({
			'save-save-interval': '30',
			desmume_screens_layout: 'top/bottom',
			desmume_pointer_mouse: 'enabled',
			desmume_pointer_type: 'touch',
			lockMouse: 'disabled'
		});
		expect(atLoader?.EJS_Buttons).toEqual({ ...buttons, saveSavFiles: false, loadSavFiles: false });
		for (const key of [
			'EJS_biosUrl',
			'EJS_externalFiles',
			'EJS_paths',
			'EJS_threads',
			'EJS_controlScheme'
		])
			expect(atLoader?.[key], key).toBeUndefined();
		expect((atLoader?.EJS_gameUrl as File).name).toBe('Marker Test (World).nds');
	});

	it('leaves the buttons, defaults and settings of an existing core unchanged', async () => {
		serve({ [romUrl]: async () => response(200) });

		await play('gambatte', 'Orbit Blocks (World).gb');

		expect(atLoader?.EJS_Buttons).toEqual(buttons);
		expect(atLoader?.EJS_defaultOptions).toEqual({ 'save-save-interval': '30' });
		expect(atLoader?.EJS_hideSettings).toBeUndefined();
	});
});
