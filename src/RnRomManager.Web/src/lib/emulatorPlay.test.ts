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
	'EJS_defaultOptions'
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

async function play(core: string, name: string): Promise<string> {
	const query = new URLSearchParams({ game, rom: '12', core, name });
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

		await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin');

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

		await play('pcsx_rearmed', 'Solo (USA).iso');

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

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin')).toBe(
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

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin')).toBe(message);
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
		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin')).toBe("Couldn't load the BIOS.");
		expect(urls()).toEqual([psxBiosUrl]);

		vi.resetModules();
		serve({
			[psxBiosUrl]: () => Promise.reject(new TypeError('network')),
			[discUrl]: async () => discResponse(200)
		});
		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin')).toBe("Couldn't load the BIOS.");
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

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin')).toBe(message);
		expect(atLoader).toBeNull();
	});

	it('stops when the disc request fails', async () => {
		serve({
			[psxBiosUrl]: async () => response(404),
			[discUrl]: () => Promise.reject(new TypeError('network'))
		});

		expect(await play('pcsx_rearmed', 'Disc (USA) (Track 1).bin')).toBe("Couldn't load the disc.");
		expect(atLoader).toBeNull();
	});

	it('scopes a standalone image named like the BIOS to the game', async () => {
		serve({ [psxBiosUrl]: async () => response(404), [discUrl]: async () => discResponse(200) });

		await play('pcsx_rearmed', 'SCPH5501.BIN');

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
