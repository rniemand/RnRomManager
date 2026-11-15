import { describe, expect, it } from 'vitest';
import {
	biosError,
	biosFor,
	contentFileName,
	controlSchemeFor,
	discError,
	hidesCoreChooser,
	isOperatorCore,
	operatorCoreError,
	operatorCorePaths,
	optionalBiosError,
	parsePlayParams,
	pspBrowserError,
	psxBiosBytes,
	saturnBios,
	saturnBiosBytes,
	saturnBiosSizeError,
	segaCdBios,
	segaCdBiosBytes,
	segaCdBiosMissing,
	segaCdBiosSizeError,
	threeDoBios,
	threeDoBiosBytes,
	threeDoBiosMissing,
	threeDoBiosSizeError
} from '../../static/emulator/params.js';

const game = '0f8fad5b-d9cb-469f-a165-70867728950e';

function query(overrides: Record<string, string | null>): string {
	const values: Record<string, string | null> = {
		game,
		rom: '12',
		core: 'gambatte',
		name: 'Orbit Blocks (World).gb',
		...overrides
	};
	const params = new URLSearchParams();
	for (const [key, value] of Object.entries(values)) if (value !== null) params.set(key, value);
	return `?${params}`;
}

describe('parsePlayParams', () => {
	it('parses a valid query', () => {
		expect(parsePlayParams(query({ core: 'mgba', name: 'A #1 & B.gba' }))).toEqual({
			game,
			rom: 12,
			core: 'mgba',
			name: 'A #1 & B.gba',
			disc: false
		});
	});

	it('reads disc=1 as a disc release (Sega CD B6)', () => {
		expect(parsePlayParams(query({ core: 'pcsx_rearmed', disc: '1' }))?.disc).toBe(true);
		expect(parsePlayParams(query({ core: 'ppsspp', disc: '1' }), true)?.disc).toBe(true);
		expect(parsePlayParams(query({ core: 'genesis_plus_gx' }))?.disc).toBe(false);
	});

	it.each(['0', 'true', '', '01'])('rejects disc=%s (Sega CD B6)', (disc) => {
		expect(parsePlayParams(query({ disc }))).toBeNull();
	});

	it('rejects a repeated disc (Sega CD B6)', () => {
		expect(parsePlayParams(`${query({})}&disc=1&disc=1`)).toBeNull();
	});

	it.each([
		'handy',
		'virtualjaguar',
		'beetle_vb',
		'a5200',
		'prosystem',
		'stella2014',
		'gearcoleco',
		'smsplus',
		'fceumm',
		'pcsx_rearmed',
		'genesis_plus_gx',
		'picodrive',
		'yabause',
		'snes9x',
		'desmume',
		'opera'
	])('accepts the %s core', (core) => {
		expect(parsePlayParams(query({ core }))?.core).toBe(core);
	});

	it.each([
		['a bad GUID', { game: 'not-a-guid' }],
		['rom 0', { rom: '0' }],
		['rom -1', { rom: '-1' }],
		['rom 1.5', { rom: '1.5' }],
		['an unknown core', { core: 'bsnes' }],
		['a case variant of a core', { core: 'Snes9x' }],
		['an empty name', { name: '' }],
		['a 256-char name', { name: 'a'.repeat(256) }],
		['a missing game', { game: null }],
		['a missing rom', { rom: null }],
		['a missing core', { core: null }],
		['a missing name', { name: null }]
	])('rejects %s', (_, overrides) => {
		expect(parsePlayParams(query(overrides))).toBeNull();
	});

	it('accepts a 255-char name', () => {
		expect(parsePlayParams(query({ name: 'a'.repeat(255) }))?.name).toHaveLength(255);
	});
});

describe('biosFor', () => {
	it('names the ColecoVision BIOS for gearcoleco', () => {
		expect(biosFor('gearcoleco')).toBe('colecovision.rom');
	});

	it.each(['gambatte', 'stella2014', 'smsplus', 'prosystem', 'fceumm', 'pcsx_rearmed'])(
		'needs none for %s',
		(core) => {
			expect(biosFor(core)).toBeNull();
		}
	);
});

describe('biosError', () => {
	it.each([200, 204])('is null for %i', (status) => {
		expect(biosError('colecovision.rom', status)).toBeNull();
	});

	it('asks to sign in again on 401', () => {
		expect(biosError('colecovision.rom', 401)).toBe('Your session has expired. Sign in again.');
	});

	it('names the missing file and folder on 404', () => {
		expect(biosError('colecovision.rom', 404)).toBe(
			'This system needs its BIOS: ask an Admin to put colecovision.rom in the BIOS folder (Paths:Bios).'
		);
	});

	it('shows the status otherwise', () => {
		expect(biosError('colecovision.rom', 500)).toBe("Couldn't load the BIOS (HTTP 500).");
	});
});

describe('controlSchemeFor', () => {
	it.each(['Pocket Drift (World).gg', 'POCKET.GG'])('uses segaGG for smsplus %s', (name) => {
		expect(controlSchemeFor('smsplus', name)).toBe('segaGG');
	});

	it('keeps the default for an smsplus .sms release', () => {
		expect(controlSchemeFor('smsplus', 'Harbor Patrol (World).sms')).toBeNull();
	});

	it('keeps the default for another core with a .gg name', () => {
		expect(controlSchemeFor('gambatte', 'Odd.gg')).toBeNull();
	});
});

describe('hidesCoreChooser', () => {
	it.each(['smsplus', 'fceumm', 'pcsx_rearmed'])('hides it for the pinned %s', (core) => {
		expect(hidesCoreChooser(core)).toBe(true);
	});

	it.each(['gambatte', 'gearcoleco'])('keeps it for %s', (core) => {
		expect(hidesCoreChooser(core)).toBe(false);
	});
});

describe('optionalBiosError', () => {
	it.each([200, 404])('goes on for %i (404 means HLE)', (status) => {
		expect(optionalBiosError(status)).toBeNull();
	});

	it('asks to sign in again on 401', () => {
		expect(optionalBiosError(401)).toBe('Your session has expired. Sign in again.');
	});

	it('shows the status for other failures, and a plain message for a network error', () => {
		expect(optionalBiosError(500)).toBe("Couldn't load the BIOS (HTTP 500).");
		expect(optionalBiosError(403)).toBe("Couldn't load the BIOS (HTTP 403).");
		expect(optionalBiosError(null)).toBe("Couldn't load the BIOS.");
	});

	it('expects exactly 512 KiB', () => {
		expect(psxBiosBytes).toBe(524288);
	});
});

describe('discError', () => {
	it('asks to sign in again on 401', () => {
		expect(discError(401, 'Unauthorized')).toBe('Your session has expired. Sign in again.');
	});

	it('names a disc that is too large or invalid', () => {
		expect(discError(409, 'PlayDiscTooLarge')).toBe(
			'This disc is larger than 1 GiB, too large to play in the browser.'
		);
		expect(discError(409, 'InvalidPlayDisc')).toBe(
			"This disc can't be played in the browser: its cue sheet or files are invalid or don't match."
		);
	});

	it('shows a disc-loading message otherwise', () => {
		expect(discError(404, 'NotFound')).toBe("Couldn't load the disc (HTTP 404).");
		expect(discError(500, null)).toBe("Couldn't load the disc (HTTP 500).");
		expect(discError(null, null)).toBe("Couldn't load the disc.");
	});
});

describe('contentFileName', () => {
	it("replaces '#' only", () => {
		expect(contentFileName('gambatte', game, 'A #1.gb')).toBe('A _1.gb');
		expect(contentFileName('pcsx_rearmed', game, 'Disc #2 (Track 1).bin')).toBe(
			'Disc _2 (Track 1).bin'
		);
	});

	it.each(['scph5501.bin', 'SCPH5501.BIN'])(
		'scopes a standalone PlayStation image named %s to the game',
		(name) => {
			expect(contentFileName('pcsx_rearmed', game, name)).toBe(`${game}-${name}`);
		}
	);

	it('leaves the BIOS-like name alone for another core', () => {
		expect(contentFileName('gambatte', game, 'scph5501.bin')).toBe('scph5501.bin');
	});
});

describe('PSP page (PSP B3)', () => {
	it('play-psp.html accepts only ppsspp, and play.html rejects it', () => {
		expect(parsePlayParams(query({ core: 'ppsspp', name: 'Umd (USA).iso' }), true)).toEqual({
			game,
			rom: 12,
			core: 'ppsspp',
			name: 'Umd (USA).iso',
			disc: false
		});
		expect(parsePlayParams(query({ core: 'ppsspp' }))).toBeNull();
		expect(parsePlayParams(query({ core: 'ppsspp' }), false)).toBeNull();
		for (const core of ['gambatte', 'pcsx_rearmed', 'smsplus'])
			expect(parsePlayParams(query({ core }), true), core).toBeNull();
	});

	it('names the PSP limits for a too-large or invalid UMD image', () => {
		expect(discError(409, 'PlayDiscTooLarge', 'ppsspp')).toBe(
			'This UMD image is larger than 2 GiB, too large to play in the browser.'
		);
		expect(discError(409, 'InvalidPlayDisc', 'ppsspp')).toBe(
			"This UMD image can't be played in the browser: it must be one non-empty .iso whose name matches the release."
		);
		expect(discError(401, 'PlayDiscTooLarge', 'ppsspp')).toBe(
			'Your session has expired. Sign in again.'
		);
		expect(discError(null, null, 'ppsspp')).toBe("Couldn't load the disc.");
		expect(discError(409, 'PlayDiscTooLarge')).toBe(
			'This disc is larger than 1 GiB, too large to play in the browser.'
		);
	});

	const capable = { secure: true, isolated: true, sharedArrayBuffer: true, webgl2: true };

	it('lets a secure, isolated, WebGL 2 browser go on', () => {
		expect(pspBrowserError(capable)).toBeNull();
	});

	it.each([
		[{ secure: false }, 'PSP games need a secure page'],
		[{ isolated: false }, "This page isn't cross-origin isolated"],
		[{ sharedArrayBuffer: false }, "This page isn't cross-origin isolated"],
		[{ webgl2: false }, 'PSP games need WebGL 2']
	])('reports %o', (missing, message) => {
		expect(pspBrowserError({ ...capable, ...missing })).toMatch(new RegExp(`^${message}`));
	});

	it('reports an insecure context first', () => {
		expect(
			pspBrowserError({ secure: false, isolated: false, sharedArrayBuffer: false, webgl2: false })
		).toBe(
			"PSP games need a secure page: open RnRomManager over HTTPS or on localhost. A plain-HTTP network address can't run them."
		);
	});
});

describe('Genesis (Genesis B4, B5)', () => {
	it('accepts genesis_plus_gx on the modal page only', () => {
		expect(
			parsePlayParams(query({ core: 'genesis_plus_gx', name: 'Turbo Comet (USA).md' }))?.core
		).toBe('genesis_plus_gx');
		expect(parsePlayParams(query({ core: 'genesis_plus_gx' }), true)).toBeNull();
	});

	it.each(['Turbo Comet (USA).md', 'Turbo Comet (USA).gen', 'Turbo Comet (USA).bin', 'X.GG'])(
		'uses the Mega Drive controls for %s',
		(name) => {
			expect(controlSchemeFor('genesis_plus_gx', name)).toBe('segaMD');
		}
	);

	it('keeps SMS Plus GX on its own schemes', () => {
		expect(controlSchemeFor('smsplus', 'Harbor Patrol (USA).sms')).toBeNull();
		expect(controlSchemeFor('smsplus', 'Pocket Drift (World).gg')).toBe('segaGG');
	});

	it('hides the core chooser for Genesis Plus GX', () => {
		expect(hidesCoreChooser('genesis_plus_gx')).toBe(true);
	});

	it('maps the three operator files to the API by name', () => {
		expect(operatorCorePaths('genesis_plus_gx')).toEqual({
			'genesis_plus_gx-wasm.data': '/api/emulator/cores/genesis_plus_gx/genesis_plus_gx-wasm.data',
			'genesis_plus_gx-legacy-wasm.data':
				'/api/emulator/cores/genesis_plus_gx/genesis_plus_gx-legacy-wasm.data',
			'genesis_plus_gx.json': '/api/emulator/cores/genesis_plus_gx/genesis_plus_gx.json'
		});
	});

	it.each([
		[204, null],
		[200, null],
		[401, 'Your session has expired. Sign in again.'],
		[
			404,
			'Genesis and Sega CD games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).'
		],
		[
			409,
			"The Genesis Plus GX core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:GenesisCorePath)."
		],
		[500, "Couldn't load the Genesis core (HTTP 500)."],
		[null, "Couldn't load the Genesis core."]
	])('reports the availability check %s', (status, message) => {
		expect(operatorCoreError('genesis_plus_gx', status)).toBe(message);
	});
});

describe('32X (B3)', () => {
	it('accepts picodrive on the modal page only', () => {
		expect(parsePlayParams(query({ core: 'picodrive', name: 'Star Relay (USA).32x' }))?.core).toBe(
			'picodrive'
		);
		expect(parsePlayParams(query({ core: 'picodrive' }), true)).toBeNull();
	});

	it.each(['Star Relay (USA).32x', 'X.GG', 'X.md'])('uses the 32X controls for %s', (name) => {
		expect(controlSchemeFor('picodrive', name)).toBe('sega32x');
	});

	it('hides the core chooser for PicoDrive', () => {
		expect(hidesCoreChooser('picodrive')).toBe(true);
	});

	it('maps the three operator files to the API by name', () => {
		expect(operatorCorePaths('picodrive')).toEqual({
			'picodrive-wasm.data': '/api/emulator/cores/picodrive/picodrive-wasm.data',
			'picodrive-legacy-wasm.data': '/api/emulator/cores/picodrive/picodrive-legacy-wasm.data',
			'picodrive.json': '/api/emulator/cores/picodrive/picodrive.json'
		});
	});

	it.each([
		[204, null],
		[401, 'Your session has expired. Sign in again.'],
		[
			404,
			'32X games need the PicoDrive core files: ask an Admin to set them up (Emulator:PicodriveCorePath).'
		],
		[
			409,
			"The PicoDrive core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:PicodriveCorePath)."
		],
		[500, "Couldn't load the PicoDrive core (HTTP 500)."],
		[null, "Couldn't load the PicoDrive core."]
	])('reports the availability check %s', (status, message) => {
		expect(operatorCoreError('picodrive', status)).toBe(message);
	});
});

describe('Saturn (B6, B7)', () => {
	it('accepts yabause on the modal page only', () => {
		expect(
			parsePlayParams(query({ core: 'yabause', name: 'Sky Rally (USA) (Track 1).bin' }))?.core
		).toBe('yabause');
		expect(parsePlayParams(query({ core: 'yabause' }), true)).toBeNull();
	});

	it('uses the Saturn controls and hides the core chooser', () => {
		expect(controlSchemeFor('yabause', 'Sky Rally (USA).cue')).toBe('segaSaturn');
		expect(hidesCoreChooser('yabause')).toBe(true);
	});

	it.each([
		'saturn_bios.bin',
		'SATURN_BIOS.BIN',
		'sega_101.bin',
		'Sega_101.Bin',
		'mpr-17933.bin',
		'MPR-17933.BIN'
	])('scopes a Saturn image named %s to the game', (name) => {
		expect(contentFileName('yabause', game, name)).toBe(`${game}-${name}`);
	});

	it('leaves other Saturn names, and other cores, alone', () => {
		expect(contentFileName('yabause', game, 'Sky Rally (USA) (Track 1).bin')).toBe(
			'Sky Rally (USA) (Track 1).bin'
		);
		expect(contentFileName('yabause', game, 'scph5501.bin')).toBe('scph5501.bin');
		expect(contentFileName('pcsx_rearmed', game, 'saturn_bios.bin')).toBe('saturn_bios.bin');
	});

	it('pins saturn_bios.bin at 512 KiB with its own size message', () => {
		expect(saturnBios).toBe('saturn_bios.bin');
		expect(saturnBiosBytes).toBe(512 * 1024);
		expect(saturnBiosSizeError).toBe(
			'The Saturn BIOS in the BIOS folder (Paths:Bios) must be the 512 KiB saturn_bios.bin: ask an Admin to replace it.'
		);
	});

	it.each([
		[409, 'PlayDiscTooLarge', 'This disc is larger than 1 GiB, too large to play in the browser.'],
		[
			409,
			'InvalidPlayDisc',
			"This disc can't be played in the browser: its cue sheet or files are invalid or don't match."
		],
		[500, null, "Couldn't load the disc (HTTP 500)."]
	])('words a play-disc %i %s like PlayStation', (status, code, message) => {
		expect(discError(status, code, 'yabause')).toBe(message);
	});
});

describe('SNES (SNES B3)', () => {
	it('accepts snes9x on the modal page only', () => {
		expect(
			parsePlayParams(query({ core: 'snes9x', name: 'Mode Seven Rally (USA).sfc' }))?.core
		).toBe('snes9x');
		expect(parsePlayParams(query({ core: 'snes9x' }), true)).toBeNull();
	});

	it.each(['Mode Seven Rally (USA).sfc', 'Legacy (USA).smc', 'X.GG'])(
		'leaves the control scheme to upstream for %s',
		(name) => {
			expect(controlSchemeFor('snes9x', name)).toBeNull();
		}
	);

	it('needs no BIOS and hides the core chooser', () => {
		expect(biosFor('snes9x')).toBeNull();
		expect(hidesCoreChooser('snes9x')).toBe(true);
	});

	it('keeps the release name as the content file name', () => {
		expect(contentFileName('snes9x', game, 'Mode Seven #2 (USA).smc')).toBe(
			'Mode Seven _2 (USA).smc'
		);
	});

	it('maps the three operator files to the API by name', () => {
		expect(operatorCorePaths('snes9x')).toEqual({
			'snes9x-wasm.data': '/api/emulator/cores/snes9x/snes9x-wasm.data',
			'snes9x-legacy-wasm.data': '/api/emulator/cores/snes9x/snes9x-legacy-wasm.data',
			'snes9x.json': '/api/emulator/cores/snes9x/snes9x.json'
		});
	});

	it.each([
		[204, null],
		[401, 'Your session has expired. Sign in again.'],
		[
			404,
			'SNES games need the Snes9x core files: ask an Admin to set them up (Emulator:Snes9xCorePath).'
		],
		[
			409,
			"The Snes9x core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:Snes9xCorePath)."
		],
		[500, "Couldn't load the Snes9x core (HTTP 500)."],
		[null, "Couldn't load the Snes9x core."]
	])('reports the availability check %s', (status, message) => {
		expect(operatorCoreError('snes9x', status)).toBe(message);
	});
});

describe('3DO (B6)', () => {
	it('accepts opera on the modal page only', () => {
		expect(parsePlayParams(query({ core: 'opera', name: 'Opal Rally (USA).cue' }))?.core).toBe(
			'opera'
		);
		expect(parsePlayParams(query({ core: 'opera' }), true)).toBeNull();
	});

	it('is an operator core that hides the chooser and keeps upstream controls', () => {
		expect(isOperatorCore('opera')).toBe(true);
		expect(hidesCoreChooser('opera')).toBe(true);
		for (const name of ['Opal Rally (USA).cue', 'Opal Solo (USA).iso', 'X.GG'])
			expect(controlSchemeFor('opera', name)).toBeNull();
	});

	it('maps the three operator files to the API by name', () => {
		expect(operatorCorePaths('opera')).toEqual({
			'opera-wasm.data': '/api/emulator/cores/opera/opera-wasm.data',
			'opera-legacy-wasm.data': '/api/emulator/cores/opera/opera-legacy-wasm.data',
			'opera.json': '/api/emulator/cores/opera/opera.json'
		});
	});

	it.each([
		[204, null],
		[401, 'Your session has expired. Sign in again.'],
		[
			404,
			'3DO games need the Opera core files: ask an Admin to set them up (Emulator:OperaCorePath).'
		],
		[
			409,
			"The Opera core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:OperaCorePath)."
		],
		[500, "Couldn't load the Opera core (HTTP 500)."],
		[null, "Couldn't load the Opera core."]
	])('reports the availability check %s', (status, message) => {
		expect(operatorCoreError('opera', status)).toBe(message);
	});
});

describe('3DO (B8)', () => {
	it('fetches the five BIOS files in order, each 1 MiB', () => {
		expect(threeDoBios).toEqual([
			'panafz10.bin',
			'panafz1.bin',
			'panafz1j.bin',
			'goldstar.bin',
			'sanyotry.bin'
		]);
		expect(threeDoBiosBytes).toBe(1048576);
	});

	it.each(['panafz10.bin', 'PANAFZ1.BIN', 'Panafz1j.bin', 'goldstar.BIN', 'SanyoTry.bin'])(
		'names a disc called %s after the game',
		(name) => {
			expect(contentFileName('opera', game, name)).toBe(`${game}-${name}`);
		}
	);

	it('leaves other names alone, and BIOS names on other cores', () => {
		expect(contentFileName('opera', game, 'Opal Rally #2 (USA).cue')).toBe(
			'Opal Rally _2 (USA).cue'
		);
		expect(contentFileName('opera', game, 'panafz10.iso')).toBe('panafz10.iso');
		expect(contentFileName('snes9x', game, 'panafz10.bin')).toBe('panafz10.bin');
	});

	it('words the size and missing messages', () => {
		expect(threeDoBiosSizeError('goldstar.bin')).toBe(
			'The 3DO BIOS goldstar.bin in the BIOS folder (Paths:Bios) must be 1 MiB: ask an Admin to replace it.'
		);
		expect(threeDoBiosMissing).toBe(
			'3DO games need a BIOS: ask an Admin to put panafz10.bin, panafz1.bin, panafz1j.bin, goldstar.bin or sanyotry.bin in the BIOS folder (Paths:Bios).'
		);
	});
});

describe('Nintendo DS (Nintendo DS B4)', () => {
	it('accepts desmume on the modal page only', () => {
		expect(parsePlayParams(query({ core: 'desmume', name: 'Marker Test (World).nds' }))).toEqual({
			game,
			rom: 12,
			core: 'desmume',
			name: 'Marker Test (World).nds',
			disc: false
		});
		expect(parsePlayParams(query({ core: 'desmume' }), true)).toBeNull();
	});

	it('needs no BIOS, is vendored and hides the core chooser', () => {
		expect(biosFor('desmume')).toBeNull();
		expect(isOperatorCore('desmume')).toBe(false);
		expect(hidesCoreChooser('desmume')).toBe(true);
	});

	it('keeps the generic nds controls and the release name as the content file name', () => {
		expect(controlSchemeFor('desmume', 'Marker Test (World).nds')).toBeNull();
		expect(contentFileName('desmume', game, 'Marker #1 (World).nds')).toBe('Marker _1 (World).nds');
	});
});

describe('Sega CD (B7, B8)', () => {
	it('fetches the three regional BIOS files in U, E, J order, each exactly 128 KiB', () => {
		expect(segaCdBios).toEqual(['bios_CD_U.bin', 'bios_CD_E.bin', 'bios_CD_J.bin']);
		expect(segaCdBiosBytes).toBe(131072);
	});

	it.each(['bios_CD_U.bin', 'BIOS_CD_E.BIN', 'bios_cd_j.bin'])(
		'renames content called %s after the game, for a disc and a cartridge alike',
		(name) => {
			expect(parsePlayParams(query({ core: 'genesis_plus_gx', name, disc: '1' }))?.disc).toBe(true);
			expect(parsePlayParams(query({ core: 'genesis_plus_gx', name }))?.disc).toBe(false);
			expect(contentFileName('genesis_plus_gx', game, name)).toBe(`${game}-${name}`);
		}
	);

	it('leaves other Genesis Plus GX names and other cores alone', () => {
		expect(contentFileName('genesis_plus_gx', game, 'Disc Comet (USA).cue')).toBe(
			'Disc Comet (USA).cue'
		);
		expect(contentFileName('picodrive', game, 'bios_CD_U.bin')).toBe('bios_CD_U.bin');
		expect(contentFileName('yabause', game, 'bios_CD_U.bin')).toBe('bios_CD_U.bin');
	});

	it('names the BIOS file in the size message, and all three in the missing message', () => {
		expect(segaCdBiosSizeError('bios_CD_E.bin')).toBe(
			'The Sega CD BIOS bios_CD_E.bin in the BIOS folder (Paths:Bios) must be 128 KiB: ask an Admin to replace it.'
		);
		expect(segaCdBiosMissing).toBe(
			'Sega CD games need a BIOS: ask an Admin to put bios_CD_U.bin, bios_CD_E.bin or bios_CD_J.bin in the BIOS folder (Paths:Bios).'
		);
	});

	it('labels the shared core for both systems (B8)', () => {
		expect(operatorCoreError('genesis_plus_gx', 404)).toBe(
			'Genesis and Sega CD games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).'
		);
	});
});
