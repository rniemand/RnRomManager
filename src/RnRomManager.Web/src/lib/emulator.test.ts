import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { configureClient, resetClient } from './api/client';
import {
	canPlay,
	opensInNewTab,
	playLinkEvents,
	playUrl,
	recordPlay,
	trimBounds,
	usesPlayDisc
} from './emulator';
import type { ReleaseFile } from './api/types';

describe('canPlay', () => {
	it('is false without a browser core', () => {
		expect(canPlay({ browserCore: null }, { externalFileName: null })).toBe(false);
	});

	it('is false for an external release', () => {
		expect(canPlay({ browserCore: 'gambatte' }, { externalFileName: 'x.nsp' })).toBe(false);
	});

	it('is true for a supported release inside the ZIP', () => {
		expect(canPlay({ browserCore: 'mgba' }, { externalFileName: null })).toBe(true);
		expect(canPlay({ browserCore: 'fceumm' }, { externalFileName: null })).toBe(true);
	});

	it('is true for an embedded SNES release and false for an external one (SNES B1)', () => {
		expect(canPlay({ browserCore: 'snes9x' }, { externalFileName: null })).toBe(true);
		expect(
			canPlay({ browserCore: 'snes9x' }, { externalFileName: 'Mode Seven Rally (USA).sfc' })
		).toBe(false);
	});

	it('is true for an external PlayStation disc', () => {
		expect(
			canPlay({ browserCore: 'pcsx_rearmed' }, { externalFileName: 'Disc (USA) (Track 1).bin' })
		).toBe(true);
	});

	it('is true for an external PSP UMD image (PSP B1)', () => {
		expect(canPlay({ browserCore: 'ppsspp' }, { externalFileName: 'Umd (USA).iso' })).toBe(true);
	});

	it.each(['Disc Runner (USA).cue', 'Disc Runner (USA).iso', 'Disc Runner (USA) (Track 1).bin'])(
		'is true for an external Sega CD %s on Genesis Plus GX (Sega CD B5)',
		(externalFileName) => {
			expect(canPlay({ browserCore: 'genesis_plus_gx' }, { externalFileName })).toBe(true);
		}
	);

	it.each(['Opal Rally (USA).cue', 'Opal Solo (USA).iso'])(
		'is true for an external 3DO %s on Opera (3DO B7)',
		(externalFileName) => {
			expect(canPlay({ browserCore: 'opera' }, { externalFileName })).toBe(true);
		}
	);

	it.each(['pcsx_rearmed', 'yabause', 'genesis_plus_gx'])(
		'is true for an external .chd on %s (CHD B5)',
		(browserCore) => {
			expect(canPlay({ browserCore }, { externalFileName: 'Disc (USA).chd' })).toBe(true);
		}
	);

	it('is false for an external release of another core, and without a core', () => {
		expect(canPlay({ browserCore: 'fceumm' }, { externalFileName: 'x.nes' })).toBe(false);
		expect(canPlay({ browserCore: null }, { externalFileName: 'Disc (Track 1).bin' })).toBe(false);
	});

	it('is true for an external Saturn bin, and false for its .nrg (Saturn B6)', () => {
		expect(
			canPlay({ browserCore: 'yabause' }, { externalFileName: 'Sky Rally (USA) (Track 1).bin' })
		).toBe(true);
		expect(canPlay({ browserCore: 'yabause' }, { externalFileName: 'Sky Rally (USA).nrg' })).toBe(
			false
		);
		expect(canPlay({ browserCore: 'yabause' }, { externalFileName: 'Sky Rally (USA).NRG' })).toBe(
			false
		);
	});

	it.each(['Disc (USA).nrg', 'Disc (USA).NRG'])(
		'is false for an external %s image, whatever the core (Saturn B6)',
		(externalFileName) => {
			expect(canPlay({ browserCore: 'pcsx_rearmed' }, { externalFileName })).toBe(false);
		}
	);

	it('still plays an external .bin with the same core (Saturn B6)', () => {
		expect(canPlay({ browserCore: 'pcsx_rearmed' }, { externalFileName: 'Disc (USA).bin' })).toBe(
			true
		);
	});
});

describe('canPlay on mednafen_pce (TurboGrafx-CD B5)', () => {
	const file = { id: 1, fileName: 'Disc (USA).cue', sizeBytes: 100 } as ReleaseFile;

	it('plays a cue set whose main file is a .bin', () => {
		expect(
			canPlay(
				{ browserCore: 'mednafen_pce' },
				{ externalFileName: 'Disc (USA) (Track 02).bin', files: [file] }
			)
		).toBe(true);
	});

	it('plays an external .chd', () => {
		expect(canPlay({ browserCore: 'mednafen_pce' }, { externalFileName: 'Disc (USA).chd' })).toBe(
			true
		);
	});

	it.each([null, []])('refuses a lone external .BIN (files %j)', (files) => {
		expect(
			canPlay({ browserCore: 'mednafen_pce' }, { externalFileName: 'Disc (USA).BIN', files })
		).toBe(false);
	});

	it('plays an embedded TurboGrafx-16 .pce', () => {
		expect(canPlay({ browserCore: 'mednafen_pce' }, { externalFileName: null })).toBe(true);
	});
});

describe('S8 Nintendo DS', () => {
	it('plays an external DS release on DeSmuME', () => {
		expect(canPlay({ browserCore: 'desmume' }, { externalFileName: 'Quest.(USA).nds' })).toBe(true);
	});

	it.each(['pcsx_rearmed', 'ppsspp', 'yabause', 'genesis_plus_gx', 'opera'])(
		'usesPlayDisc is true for an external %s release',
		(core) => {
			expect(usesPlayDisc(core, { externalFileName: 'Disc (USA).cue' })).toBe(true);
		}
	);

	it('usesPlayDisc is false for DeSmuME and for an embedded release', () => {
		expect(usesPlayDisc('desmume', { externalFileName: 'Quest.(USA).nds' })).toBe(false);
		expect(usesPlayDisc('pcsx_rearmed', { externalFileName: null })).toBe(false);
	});
});

describe('playUrl', () => {
	it('adds the save to load at start (Saves B17)', () => {
		const id = 'a'.repeat(32);
		expect(
			playUrl('g-1', { id: 7, fileName: 'x.gb' }, 'gambatte', false, {
				saveId: id,
				saveKind: 'battery'
			})
		).toBe(
			`/emulator/play.html?game=g-1&rom=7&core=gambatte&name=x.gb&save=${id}&saveKind=battery`
		);
		expect(
			playUrl('g-1', { id: 7, fileName: 'x.cue' }, 'opera', true, { saveId: id, saveKind: 'state' })
		).toBe(
			`/emulator/play.html?game=g-1&rom=7&core=opera&name=x.cue&disc=1&save=${id}&saveKind=state`
		);
	});

	it('encodes the file name', () => {
		expect(playUrl('g-1', { id: 7, fileName: 'A & B #1 (World).gb' }, 'gambatte', false)).toBe(
			'/emulator/play.html?game=g-1&rom=7&core=gambatte&name=A%20%26%20B%20%231%20(World).gb'
		);
	});

	it('points PPSSPP at the isolated PSP page (PSP B3)', () => {
		expect(playUrl('g-2', { id: 9, fileName: 'Umd & Co (USA).iso' }, 'ppsspp', false)).toBe(
			'/emulator/play-psp.html?game=g-2&rom=9&core=ppsspp&name=Umd%20%26%20Co%20(USA).iso'
		);
		for (const core of ['pcsx_rearmed', 'fceumm', 'smsplus'])
			expect(playUrl('g', { id: 1, fileName: 'x' }, core, false)).toMatch(
				/^\/emulator\/play\.html\?/
			);
	});

	it('adds &disc=1 after name only for a disc release (Sega CD B6)', () => {
		expect(
			playUrl('g-3', { id: 4, fileName: 'Disc & Co (USA).cue' }, 'genesis_plus_gx', true)
		).toBe(
			'/emulator/play.html?game=g-3&rom=4&core=genesis_plus_gx&name=Disc%20%26%20Co%20(USA).cue&disc=1'
		);
		expect(
			playUrl('g-3', { id: 4, fileName: 'Comet (USA).md' }, 'genesis_plus_gx', false)
		).not.toContain('disc');
		expect(playUrl('g-4', { id: 5, fileName: 'Umd (USA).iso' }, 'ppsspp', true)).toBe(
			'/emulator/play-psp.html?game=g-4&rom=5&core=ppsspp&name=Umd%20(USA).iso&disc=1'
		);
	});

	it('names a disc set after its playlist and adds &set=1, keeping Disc 1 and a save (multi-disc M5)', () => {
		const discOne = { id: 6, fileName: 'Odyssey (USA) (Disc 1) (Track 1).bin' };
		expect(playUrl('g-5', discOne, 'pcsx_rearmed', true, null, 'Odyssey & Co (USA).m3u')).toBe(
			'/emulator/play.html?game=g-5&rom=6&core=pcsx_rearmed&name=Odyssey%20%26%20Co%20(USA).m3u&disc=1&set=1'
		);
		expect(
			playUrl(
				'g-5',
				discOne,
				'pcsx_rearmed',
				true,
				{ saveId: 'a1', saveKind: 'battery' },
				'Odyssey (USA).m3u'
			)
		).toBe(
			'/emulator/play.html?game=g-5&rom=6&core=pcsx_rearmed&name=Odyssey%20(USA).m3u&disc=1&set=1&save=a1&saveKind=battery'
		);
		expect(playUrl('g-5', discOne, 'pcsx_rearmed', true)).not.toContain('set=');
	});
});

describe('opensInNewTab', () => {
	it('is true only for PPSSPP', () => {
		expect(opensInNewTab('ppsspp')).toBe(true);
		for (const core of ['pcsx_rearmed', 'gambatte', 'smsplus', null])
			expect(opensInNewTab(core)).toBe(false);
	});
});

describe('recordPlay (B22)', () => {
	let calls: string[];
	let status: number;
	const navigate = vi.fn();

	beforeEach(() => {
		resetClient();
		configureClient({ navigate });
		navigate.mockClear();
		calls = [];
		status = 204;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			calls.push(`${init?.method ?? 'GET'} ${url}`);
			if (url === '/api/auth/antiforgery')
				return Promise.resolve(new Response(null, { status: 204 }));
			return Promise.resolve(new Response(null, { status }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	const played = () => calls.filter((c) => c.startsWith('POST'));

	it('POSTs played for the release', async () => {
		recordPlay('g 1', 7);
		await vi.waitFor(() => expect(played()).toEqual(['POST /api/games/g%201/roms/7/played']));
	});

	it('swallows errors, and a 401 does not navigate to the login page', async () => {
		status = 401;
		recordPlay('g1', 7);
		await vi.waitFor(() => expect(played()).toHaveLength(1));
		await new Promise((done) => setTimeout(done, 10));
		expect(navigate).not.toHaveBeenCalled();

		status = 500;
		expect(() => recordPlay('g1', 7)).not.toThrow();
		await vi.waitFor(() => expect(played()).toHaveLength(2));
	});

	it('a PSP link records on click and on a middle-button click only', async () => {
		const events = playLinkEvents('g1', 7);
		events.onclick();
		events.onauxclick(new MouseEvent('auxclick', { button: 2 }));
		events.onauxclick(new MouseEvent('auxclick', { button: 1 }));
		await vi.waitFor(() => expect(played()).toHaveLength(2));
	});
});

describe('trimBounds (release editing B25)', () => {
	/** A width x height RGBA image of `bg`, with the given pixels set to `fg`. */
	function image(width: number, height: number, lit: [number, number][], fg = 255, bg = 0) {
		const data = new Uint8ClampedArray(width * height * 4).fill(bg);
		for (let i = 3; i < data.length; i += 4) data[i] = 255;
		for (const [x, y] of lit) data.set([fg, 0, 0], (y * width + x) * 4);
		return data;
	}

	it('cuts black rows and columns on each side', () => {
		expect(
			trimBounds(
				image(6, 5, [
					[2, 1],
					[3, 3]
				]),
				6,
				5
			)
		).toEqual({ x: 2, y: 1, width: 2, height: 3 });
		expect(trimBounds(image(4, 4, [[0, 3]]), 4, 4)).toEqual({ x: 0, y: 3, width: 1, height: 1 });
	});

	it('treats a channel of 16 as border and 17 as content', () => {
		expect(trimBounds(image(3, 3, [[1, 1]], 16), 3, 3)).toEqual({
			x: 0,
			y: 0,
			width: 3,
			height: 3
		});
		expect(trimBounds(image(3, 3, [[1, 1]], 17), 3, 3)).toEqual({
			x: 1,
			y: 1,
			width: 1,
			height: 1
		});
	});

	it('keeps an all-black image, and an image without border, whole', () => {
		expect(trimBounds(image(4, 2, []), 4, 2)).toEqual({ x: 0, y: 0, width: 4, height: 2 });
		expect(trimBounds(image(2, 2, [], 0, 200), 2, 2)).toEqual({ x: 0, y: 0, width: 2, height: 2 });
	});

	it('trims columns only within the rows left', () => {
		// A lit pixel in a trimmed-away row doesn't keep its column.
		const data = image(5, 5, [
			[2, 2],
			[4, 4]
		]);
		data.set([0, 0, 0], (4 * 5 + 4) * 4);
		expect(trimBounds(data, 5, 5)).toEqual({ x: 2, y: 2, width: 1, height: 1 });
	});
});
