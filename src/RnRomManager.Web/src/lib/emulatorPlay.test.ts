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
