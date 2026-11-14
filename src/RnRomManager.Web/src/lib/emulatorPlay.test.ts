import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Browser emulators B5/B8: the play page itself, with fetch and the loader script mocked.
const game = '0f8fad5b-d9cb-469f-a165-70867728950e';
const romUrl = `/api/games/${game}/roms/12/download`;
const biosUrl = '/api/emulator/bios/colecovision.rom';

type FakeResponse = {
	ok: boolean;
	status: number;
	body: { cancel: () => Promise<void> };
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
	'EJS_disableCue'
];

const win = window as unknown as Record<string, unknown>;

function response(status: number, cancel = vi.fn(async () => {})): FakeResponse {
	return {
		ok: status >= 200 && status < 300,
		status,
		body: { cancel },
		blob: async () => new Blob(['rom'])
	};
}

let fetchMock: ReturnType<typeof vi.fn>;
/** The EJS_* globals as they were when the loader script was added, or null if it never was. */
let atLoader: Record<string, unknown> | null;

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
	document.body.innerHTML = '<p id="status"></p><div id="game"></div>';
	delete document.body.dataset.player;
	for (const key of ejsGlobals) delete win[key];
	atLoader = null;
	const append = document.body.append.bind(document.body);
	vi.spyOn(document.body, 'append').mockImplementation((...nodes) => {
		atLoader = Object.fromEntries(ejsGlobals.map((k) => [k, win[k]]));
		append(...nodes);
	});
});

afterEach(() => {
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
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

	it('goes on when cancelling the BIOS body fails', async () => {
		const cancel = vi.fn(async () => {
			throw new Error('cancel failed');
		});
		serve({ [biosUrl]: async () => response(200, cancel), [romUrl]: async () => response(200) });

		const status = await play('gearcoleco', 'Pixel Pursuit (USA).col');

		expect(status).toBe('Loading…');
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
			body: { cancel: async () => {} },
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
			'This disc is larger than 1 GiB, too large to play in the browser.'
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
			body: { cancel: async () => {} },
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
			"This UMD image can't be played in the browser: it must be one non-empty .iso whose name matches the release."
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
			expect((atLoader?.EJS_gameUrl as File).name).toBe(name);
		}
	);

	it.each([
		[
			404,
			'Genesis games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).'
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
			body: { cancel: async () => {} },
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
