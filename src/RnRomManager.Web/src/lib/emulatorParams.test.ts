import { describe, expect, it } from 'vitest';
import { formatSize } from './format';
import {
	contentSize,
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
	isolatedBrowserError,
	psxBiosBytes,
	saturnBios,
	saturnBiosBytes,
	saturnBiosSizeError,
	saturnSetBiosMissing,
	segaCdBios,
	segaCdBiosBytes,
	segaCdBiosMissing,
	segaCdBiosSizeError,
	threeDoBios,
	threeDoBiosBytes,
	threeDoBiosMissing,
	threeDoBiosSizeError,
	turboGrafxCdBiosMaxBytes,
	turboGrafxCdBiosMissing,
	turboGrafxCdBiosNames,
	turboGrafxCdBiosSizeError,
	turboGrafxCdCards,
	turboGrafxCdGamesExpress
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

describe('parsePlayParams saves (Saves B17)', () => {
	const id = 'a'.repeat(32);

	it.each(['state', 'battery'] as const)('reads save + saveKind=%s', (kind) => {
		expect(parsePlayParams(query({ save: id, saveKind: kind }))?.save).toEqual({ id, kind });
	});

	it('has no save without either', () => {
		expect(parsePlayParams(query({}))?.save).toBeNull();
	});

	it.each([
		['a lone save', { save: id }],
		['a lone saveKind', { saveKind: 'state' }],
		['an uppercase id', { save: 'A'.repeat(32), saveKind: 'state' }],
		['a short id', { save: 'a'.repeat(31), saveKind: 'state' }],
		['a GUID with dashes', { save: game, saveKind: 'state' }],
		['an unknown kind', { save: id, saveKind: 'sram' }]
	])('refuses %s', (_, overrides) => {
		expect(parsePlayParams(query(overrides))).toBeNull();
	});

	it('refuses a repeated save', () => {
		expect(
			parsePlayParams(`${query({ save: id, saveKind: 'state' })}&save=${id}&saveKind=state`)
		).toBeNull();
	});
});

describe('parsePlayParams', () => {
	it('parses a valid query', () => {
		expect(parsePlayParams(query({ core: 'mgba', name: 'A #1 & B.gba' }))).toEqual({
			game,
			rom: 12,
			core: 'mgba',
			name: 'A #1 & B.gba',
			disc: false,
			set: false,
			save: null
		});
	});

	it('reads disc=1 as a disc release (Sega CD B6)', () => {
		expect(parsePlayParams(query({ core: 'pcsx_rearmed', disc: '1' }))?.disc).toBe(true);
		expect(parsePlayParams(query({ core: 'ppsspp', disc: '1' }), 'psp')?.disc).toBe(true);
		expect(parsePlayParams(query({ core: 'genesis_plus_gx' }))?.disc).toBe(false);
	});

	it.each(['0', 'true', '', '01'])('rejects disc=%s (Sega CD B6)', (disc) => {
		expect(parsePlayParams(query({ disc }))).toBeNull();
	});

	it('rejects a repeated disc (Sega CD B6)', () => {
		expect(parsePlayParams(`${query({})}&disc=1&disc=1`)).toBeNull();
	});

	it('reads set=1 for PCSX ReARMed (multi-disc M5)', () => {
		expect(parsePlayParams(query({ core: 'pcsx_rearmed', disc: '1', set: '1' }))?.set).toBe(true);
		expect(parsePlayParams(query({ core: 'pcsx_rearmed', disc: '1' }))?.set).toBe(false);
	});

	it.each(['0', 'true', '', '01'])('rejects set=%s (multi-disc M5)', (set) => {
		expect(parsePlayParams(query({ core: 'pcsx_rearmed', set }))).toBeNull();
	});

	it.each(['opera', 'mgba', 'smsplus'])('rejects set=1 with %s (multi-disc M5)', (core) => {
		expect(parsePlayParams(query({ core, set: '1' }))).toBeNull();
		expect(parsePlayParams(query({ core, disc: '1', set: '1' }))).toBeNull();
	});

	it('reads set=1 for Yabause, and for Genesis Plus GX only with disc=1 (Saturn and Sega CD S5)', () => {
		expect(parsePlayParams(query({ core: 'yabause', set: '1' }))?.set).toBe(true);
		expect(parsePlayParams(query({ core: 'yabause', disc: '1', set: '1' }))?.set).toBe(true);
		expect(parsePlayParams(query({ core: 'genesis_plus_gx', disc: '1', set: '1' }))?.set).toBe(
			true
		);
		expect(parsePlayParams(query({ core: 'genesis_plus_gx', set: '1' }))).toBeNull();
	});

	it('reads set=1 for PUAE only with disc=1 (Amiga F6)', () => {
		expect(parsePlayParams(query({ core: 'puae', disc: '1', set: '1' }))?.set).toBe(true);
		expect(parsePlayParams(query({ core: 'puae', set: '1' }))).toBeNull();
	});

	it('names saturn_bios.bin when a Saturn set has no BIOS (Saturn and Sega CD S6)', () => {
		expect(saturnSetBiosMissing).toBe(
			'Playing all Saturn discs needs a BIOS: ask an Admin to put saturn_bios.bin in the BIOS folder (Paths:Bios).'
		);
	});

	it('rejects a repeated set (multi-disc M5)', () => {
		expect(parsePlayParams(`${query({ core: 'pcsx_rearmed' })}&set=1&set=1`)).toBeNull();
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
		'opera',
		'vice_x64sc',
		'vice_xpet',
		'vice_xvic',
		'puae',
		'mednafen_pce',
		'mednafen_ngp',
		'mupen64plus_next',
		'mame2003_plus'
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
		expect(biosFor('gearcoleco', 'X (USA).col')).toBe('colecovision.rom');
	});

	it.each(['X (Japan).fds', 'X (Japan).FDS'])(
		'names disksys.rom for an FCEUmm %s release',
		(name) => {
			expect(biosFor('fceumm', name)).toBe('disksys.rom');
		}
	);

	it.each([
		['X (Japan).st', 'STBIOS.bin'],
		['X (Japan).ST', 'STBIOS.bin'],
		['X (Japan).bs', 'BS-X.bin'],
		['X (Japan).BS', 'BS-X.bin']
	])('names the Snes9x BIOS for a %s release', (name, bios) => {
		expect(biosFor('snes9x', name)).toBe(bios);
	});

	it.each(['X (USA).sfc', 'X (USA).smc'])('needs none for a Snes9x %s release', (name) => {
		expect(biosFor('snes9x', name)).toBeNull();
	});

	it.each(['gambatte', 'stella2014', 'smsplus', 'prosystem', 'fceumm', 'pcsx_rearmed'])(
		'needs none for %s',
		(core) => {
			expect(biosFor(core, 'X (USA).nes')).toBeNull();
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

	it('keeps the default for mednafen_pce (TurboGrafx-16 / PC Engine)', () => {
		expect(controlSchemeFor('mednafen_pce', 'X (USA).pce')).toBeNull();
	});

	it('keeps the default for mednafen_ngp (Neo Geo Pocket)', () => {
		expect(controlSchemeFor('mednafen_ngp', 'X (World).ngc')).toBeNull();
	});
});

describe('hidesCoreChooser', () => {
	it.each(['smsplus', 'fceumm', 'pcsx_rearmed'])('hides it for the pinned %s', (core) => {
		expect(hidesCoreChooser(core)).toBe(true);
	});

	it.each(['gambatte', 'gearcoleco', 'mednafen_pce', 'mednafen_ngp'])('keeps it for %s', (core) => {
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
			'This disc is larger than 2 GiB, too large to play in the browser.'
		);
		expect(discError(409, 'InvalidPlayDisc')).toBe(
			"This disc can't be played in the browser: its cue sheet or files are invalid or don't match."
		);
	});

	it('names a disc set that is too large, and keeps the other messages (multi-disc M6)', () => {
		expect(discError(409, 'PlayDiscTooLarge', 'pcsx_rearmed', true)).toBe(
			'This disc set is larger than 2 GiB, too large to play in the browser. CHD images are much smaller.'
		);
		expect(discError(409, 'InvalidPlayDisc', 'pcsx_rearmed', true)).toBe(
			discError(409, 'InvalidPlayDisc')
		);
		expect(discError(401, 'PlayDiscTooLarge', 'pcsx_rearmed', true)).toBe(
			'Your session has expired. Sign in again.'
		);
		expect(discError(null, null, 'pcsx_rearmed', true)).toBe("Couldn't load the disc.");
	});

	it('has disk wording for PUAE, after the session check (Amiga F7)', () => {
		expect(discError(401, 'Unauthorized', 'puae', true)).toBe(
			'Your session has expired. Sign in again.'
		);
		expect(discError(409, 'InvalidPlayDisc', 'puae', true)).toBe(
			"These disks can't be played in the browser: one of them is empty."
		);
		expect(discError(409, 'PlayDiscTooLarge', 'puae', true)).toBe(
			"Couldn't load the disks (HTTP 409)."
		);
		expect(discError(404, 'NotFound', 'puae', true)).toBe("Couldn't load the disks (HTTP 404).");
		expect(discError(500, 'IoError', 'puae', true)).toBe("Couldn't load the disks (HTTP 500).");
		expect(discError(null, null, 'puae', true)).toBe("Couldn't load the disks.");
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

	it('scopes every DOSBox Pure release to its game, so each keeps its own browser save (MS-DOS P8)', () => {
		expect(contentFileName('dosbox_pure', game, 'A#B.dosz')).toBe(`${game}-A_B.dosz`);
	});
});

describe('PSP page (PSP B3)', () => {
	it('play-psp.html accepts only ppsspp, and play.html rejects it', () => {
		expect(parsePlayParams(query({ core: 'ppsspp', name: 'Umd (USA).iso' }), 'psp')).toEqual({
			game,
			rom: 12,
			core: 'ppsspp',
			name: 'Umd (USA).iso',
			disc: false,
			set: false,
			save: null
		});
		expect(parsePlayParams(query({ core: 'ppsspp' }))).toBeNull();
		expect(parsePlayParams(query({ core: 'ppsspp' }))).toBeNull();
		for (const core of ['gambatte', 'pcsx_rearmed', 'smsplus'])
			expect(parsePlayParams(query({ core }), 'psp'), core).toBeNull();
	});

	it('names the PSP limits for a too-large or invalid UMD image', () => {
		expect(discError(409, 'PlayDiscTooLarge', 'ppsspp')).toBe(
			'This UMD image is larger than 2 GiB, too large to play in the browser.'
		);
		expect(discError(409, 'InvalidPlayDisc', 'ppsspp')).toBe(
			"This UMD image can't be played in the browser: it must be one non-empty .iso, .cso, .pbp or .chd whose name matches the release."
		);
		expect(discError(401, 'PlayDiscTooLarge', 'ppsspp')).toBe(
			'Your session has expired. Sign in again.'
		);
		expect(discError(null, null, 'ppsspp')).toBe("Couldn't load the disc.");
		expect(discError(409, 'PlayDiscTooLarge')).toBe(
			'This disc is larger than 2 GiB, too large to play in the browser.'
		);
	});

	const capable = { secure: true, isolated: true, sharedArrayBuffer: true, webgl2: true };

	it('lets a secure, isolated, WebGL 2 browser go on', () => {
		expect(isolatedBrowserError(capable, 'ppsspp')).toBeNull();
	});

	it.each([
		[{ secure: false }, 'PSP games need a secure page'],
		[{ isolated: false }, "This page isn't cross-origin isolated"],
		[{ sharedArrayBuffer: false }, "This page isn't cross-origin isolated"],
		[{ webgl2: false }, 'PSP games need WebGL 2']
	])('reports %o', (missing, message) => {
		expect(isolatedBrowserError({ ...capable, ...missing }, 'ppsspp')).toMatch(
			new RegExp(`^${message}`)
		);
	});

	it('reports an insecure context first', () => {
		expect(
			isolatedBrowserError(
				{ secure: false, isolated: false, sharedArrayBuffer: false, webgl2: false },
				'ppsspp'
			)
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
		expect(parsePlayParams(query({ core: 'genesis_plus_gx' }), 'psp')).toBeNull();
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
		expect(parsePlayParams(query({ core: 'picodrive' }), 'psp')).toBeNull();
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
		expect(parsePlayParams(query({ core: 'yabause' }), 'psp')).toBeNull();
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
		[409, 'PlayDiscTooLarge', 'This disc is larger than 2 GiB, too large to play in the browser.'],
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
		expect(parsePlayParams(query({ core: 'snes9x' }), 'psp')).toBeNull();
	});

	it.each(['Mode Seven Rally (USA).sfc', 'Legacy (USA).smc', 'X.GG'])(
		'leaves the control scheme to upstream for %s',
		(name) => {
			expect(controlSchemeFor('snes9x', name)).toBeNull();
		}
	);

	it('needs no BIOS and hides the core chooser', () => {
		expect(biosFor('snes9x', 'X (USA).sfc')).toBeNull();
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
		expect(parsePlayParams(query({ core: 'opera' }), 'psp')).toBeNull();
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

describe('Commodore 64 (C64 B7)', () => {
	it('accepts vice_x64sc on the modal page only', () => {
		expect(
			parsePlayParams(query({ core: 'vice_x64sc', name: 'Pixel Pilot (Europe).crt' }))?.core
		).toBe('vice_x64sc');
		expect(parsePlayParams(query({ core: 'vice_x64sc' }), 'psp')).toBeNull();
	});

	it('is an operator core that hides the chooser and keeps upstream controls', () => {
		expect(isOperatorCore('vice_x64sc')).toBe(true);
		expect(hidesCoreChooser('vice_x64sc')).toBe(true);
		for (const name of ['Pixel Pilot (Europe).crt', 'Acrojet (Europe) (Side 2).tap', 'X.d64'])
			expect(controlSchemeFor('vice_x64sc', name)).toBeNull();
	});

	it('maps the three operator files to the API by name', () => {
		expect(operatorCorePaths('vice_x64sc')).toEqual({
			'vice_x64sc-wasm.data': '/api/emulator/cores/vice_x64sc/vice_x64sc-wasm.data',
			'vice_x64sc-legacy-wasm.data': '/api/emulator/cores/vice_x64sc/vice_x64sc-legacy-wasm.data',
			'vice_x64sc.json': '/api/emulator/cores/vice_x64sc/vice_x64sc.json'
		});
	});

	it.each([
		[204, null],
		[401, 'Your session has expired. Sign in again.'],
		[
			404,
			'Commodore 64 games need the VICE core files: ask an Admin to set them up (Emulator:ViceCorePath).'
		],
		[
			409,
			"The VICE core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:ViceCorePath)."
		],
		[500, "Couldn't load the VICE core (HTTP 500)."],
		[null, "Couldn't load the VICE core."]
	])('reports the availability check %s', (status, message) => {
		expect(operatorCoreError('vice_x64sc', status)).toBe(message);
	});
});

describe('Commodore PET', () => {
	it('is an operator core sharing the VICE folder', () => {
		expect(isOperatorCore('vice_xpet')).toBe(true);
		expect(hidesCoreChooser('vice_xpet')).toBe(true);
		expect(controlSchemeFor('vice_xpet', 'X.prg')).toBeNull();
		expect(operatorCorePaths('vice_xpet')).toEqual({
			'vice_xpet-wasm.data': '/api/emulator/cores/vice_xpet/vice_xpet-wasm.data',
			'vice_xpet-legacy-wasm.data': '/api/emulator/cores/vice_xpet/vice_xpet-legacy-wasm.data',
			'vice_xpet.json': '/api/emulator/cores/vice_xpet/vice_xpet.json'
		});
		expect(operatorCoreError('vice_xpet', 404)).toBe(
			'Commodore PET games need the VICE (PET) core files: ask an Admin to set them up (Emulator:ViceCorePath).'
		);
	});
});

describe('Commodore VIC-20', () => {
	it('is an operator core sharing the VICE folder', () => {
		expect(isOperatorCore('vice_xvic')).toBe(true);
		expect(hidesCoreChooser('vice_xvic')).toBe(true);
		expect(controlSchemeFor('vice_xvic', 'X.a0')).toBeNull();
		expect(operatorCorePaths('vice_xvic')).toEqual({
			'vice_xvic-wasm.data': '/api/emulator/cores/vice_xvic/vice_xvic-wasm.data',
			'vice_xvic-legacy-wasm.data': '/api/emulator/cores/vice_xvic/vice_xvic-legacy-wasm.data',
			'vice_xvic.json': '/api/emulator/cores/vice_xvic/vice_xvic.json'
		});
		expect(operatorCoreError('vice_xvic', 404)).toBe(
			'Commodore VIC-20 games need the VICE (VIC-20) core files: ask an Admin to set them up (Emulator:ViceCorePath).'
		);
	});
});

describe('Nintendo 64', () => {
	it('is an operator core with its own folder', () => {
		expect(isOperatorCore('mupen64plus_next')).toBe(true);
		expect(hidesCoreChooser('mupen64plus_next')).toBe(true);
		expect(controlSchemeFor('mupen64plus_next', 'X (USA).z64')).toBeNull();
		expect(operatorCorePaths('mupen64plus_next')).toEqual({
			'mupen64plus_next-wasm.data':
				'/api/emulator/cores/mupen64plus_next/mupen64plus_next-wasm.data',
			'mupen64plus_next-legacy-wasm.data':
				'/api/emulator/cores/mupen64plus_next/mupen64plus_next-legacy-wasm.data',
			'mupen64plus_next.json': '/api/emulator/cores/mupen64plus_next/mupen64plus_next.json'
		});
		expect(operatorCoreError('mupen64plus_next', 404)).toBe(
			'Nintendo 64 games need the Mupen64Plus-Next core files: ask an Admin to set them up (Emulator:Mupen64PlusCorePath).'
		);
		expect(operatorCoreError('mupen64plus_next', 409)).toBe(
			"The Mupen64Plus-Next core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:Mupen64PlusCorePath)."
		);
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
			disc: false,
			set: false,
			save: null
		});
		expect(parsePlayParams(query({ core: 'desmume' }), 'psp')).toBeNull();
	});

	it('needs no BIOS, is vendored and hides the core chooser', () => {
		expect(biosFor('desmume', 'X (USA).nds')).toBeNull();
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

describe('TurboGrafx-CD (B7, B9)', () => {
	it('looks for the System Cards in order, then the optional Games Express card, each up to 1 MiB', () => {
		expect(turboGrafxCdCards).toEqual([
			'syscard3.pce',
			'syscard3u.pce',
			'syscard2.pce',
			'syscard2u.pce',
			'syscard1.pce'
		]);
		expect(turboGrafxCdGamesExpress).toBe('gexpress.pce');
		expect(turboGrafxCdBiosNames).toEqual([...turboGrafxCdCards, 'gexpress.pce']);
		expect(turboGrafxCdBiosMaxBytes).toBe(1048576);
	});

	it.each([
		'syscard3.pce',
		'SYSCARD3U.PCE',
		'syscard2.pce',
		'SysCard2U.pce',
		'syscard1.pce',
		'GEXPRESS.PCE'
	])(
		'renames Beetle PCE content called %s after the game, for a disc and a HuCard alike',
		(name) => {
			expect(parsePlayParams(query({ core: 'mednafen_pce', name, disc: '1' }))?.disc).toBe(true);
			expect(parsePlayParams(query({ core: 'mednafen_pce', name }))?.disc).toBe(false);
			expect(contentFileName('mednafen_pce', game, name)).toBe(`${game}-${name}`);
		}
	);

	it('leaves other Beetle PCE names and other cores alone', () => {
		expect(contentFileName('mednafen_pce', game, 'Disc Ninja (Japan).cue')).toBe(
			'Disc Ninja (Japan).cue'
		);
		expect(contentFileName('mednafen_pce', game, 'syscard4.pce')).toBe('syscard4.pce');
		expect(contentFileName('opera', game, 'syscard3.pce')).toBe('syscard3.pce');
	});

	it('names the file in the size message, and syscard3.pce in the missing message', () => {
		expect(turboGrafxCdBiosSizeError('syscard2.pce')).toBe(
			'The TurboGrafx-CD BIOS syscard2.pce in the BIOS folder (Paths:Bios) is empty or larger than 1 MiB: ask an Admin to replace it.'
		);
		expect(turboGrafxCdBiosMissing).toBe(
			'TurboGrafx-CD games need a System Card BIOS: ask an Admin to put syscard3.pce in the BIOS folder (Paths:Bios).'
		);
	});
});

describe('contentSize (U4)', () => {
	it.each([
		[512, '512 B'],
		[1536, '1.5 KiB'],
		[16 * 1024 * 1024, '16.0 MiB'],
		[8 * 1024 * 1024 * 1024, '8.0 GiB']
	])('%i bytes is %s', (bytes, text) => {
		expect(contentSize(bytes)).toBe(text);
	});

	it('matches formatSize', () => {
		for (const bytes of [
			0, 1, 1023, 1024, 1536, 1048575, 1048576, 1572864, 536870912, 1073741824, 8589934592
		])
			expect(contentSize(bytes)).toBe(formatSize(bytes));
	});
});

describe('DOS page (MS-DOS P5, P6)', () => {
	it('play-dos.html accepts only dosbox_pure; play-psp.html and play.html reject it', () => {
		expect(parsePlayParams(query({ core: 'dosbox_pure', name: 'Doom.dosz' }), 'dos')).toEqual({
			game,
			rom: 12,
			core: 'dosbox_pure',
			name: 'Doom.dosz',
			disc: false,
			set: false,
			save: null
		});
		expect(parsePlayParams(query({ core: 'dosbox_pure' }))).toBeNull();
		expect(parsePlayParams(query({ core: 'dosbox_pure' }), 'psp')).toBeNull();
		expect(parsePlayParams(query({ core: 'ppsspp' }), 'dos')).toBeNull();
		for (const core of ['gambatte', 'pcsx_rearmed', 'desmume'])
			expect(parsePlayParams(query({ core }), 'dos'), core).toBeNull();
	});

	const capable = { secure: true, isolated: true, sharedArrayBuffer: true, webgl2: false };

	it('lets a secure, isolated browser go on without WebGL 2', () => {
		expect(isolatedBrowserError(capable, 'dosbox_pure')).toBeNull();
	});

	it.each([
		[
			{ secure: false, isolated: false },
			"DOS games need a secure page: open RnRomManager over HTTPS or on localhost. A plain-HTTP network address can't run them."
		],
		[
			{ isolated: false },
			"This page isn't cross-origin isolated, so DOS games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers."
		],
		[
			{ sharedArrayBuffer: false },
			"This page isn't cross-origin isolated, so DOS games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers."
		]
	])('reports %o', (missing, message) => {
		expect(isolatedBrowserError({ ...capable, ...missing }, 'dosbox_pure')).toBe(message);
	});

	it('keeps the PSP wording for discError', () => {
		expect(discError(409, 'PlayDiscTooLarge', 'ppsspp')).toBe(
			'This UMD image is larger than 2 GiB, too large to play in the browser.'
		);
	});
});

describe('3DS page (3DS G2, G3)', () => {
	it('play-3ds.html accepts only azahar; play-psp.html, play-dos.html and play.html reject it', () => {
		expect(
			parsePlayParams(query({ core: 'azahar', name: 'Zephyr Gate (Europe).3ds' }), '3ds')
		).toEqual({
			game,
			rom: 12,
			core: 'azahar',
			name: 'Zephyr Gate (Europe).3ds',
			disc: false,
			set: false,
			save: null
		});
		expect(parsePlayParams(query({ core: 'azahar' }))).toBeNull();
		expect(parsePlayParams(query({ core: 'azahar' }), 'psp')).toBeNull();
		expect(parsePlayParams(query({ core: 'azahar' }), 'dos')).toBeNull();
		for (const core of ['ppsspp', 'dosbox_pure', 'gambatte', 'desmume'])
			expect(parsePlayParams(query({ core }), '3ds'), core).toBeNull();
	});

	const capable = { secure: true, isolated: true, sharedArrayBuffer: true, webgl2: true };
	const insecure =
		"3DS games need a secure page: open RnRomManager over HTTPS or on localhost. A plain-HTTP network address can't run them.";
	const notIsolated =
		"This page isn't cross-origin isolated, so 3DS games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers.";
	const noWebgl2 = "3DS games need WebGL 2, which this browser or graphics driver doesn't provide.";

	it('lets a secure, isolated, WebGL 2 browser go on', () => {
		expect(isolatedBrowserError(capable, 'azahar')).toBeNull();
	});

	it.each([
		[{ secure: false, isolated: false, sharedArrayBuffer: false, webgl2: false }, insecure],
		[{ isolated: false, webgl2: false }, notIsolated],
		[{ sharedArrayBuffer: false, webgl2: false }, notIsolated],
		[{ webgl2: false }, noWebgl2]
	])('reports %o, in order', (missing, message) => {
		expect(isolatedBrowserError({ ...capable, ...missing }, 'azahar')).toBe(message);
	});

	it('leaves the PSP and DOS messages unchanged', () => {
		expect(isolatedBrowserError({ ...capable, webgl2: false }, 'ppsspp')).toBe(
			"PSP games need WebGL 2, which this browser or graphics driver doesn't provide."
		);
		expect(isolatedBrowserError({ ...capable, isolated: false }, 'dosbox_pure')).toBe(
			"This page isn't cross-origin isolated, so DOS games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers."
		);
	});
});

describe('Arcade (MAME 2003-Plus)', () => {
	it('is an operator core whose chooser is hidden', () => {
		expect(isOperatorCore('mame2003_plus')).toBe(true);
		expect(hidesCoreChooser('mame2003_plus')).toBe(true);
		expect(controlSchemeFor('mame2003_plus', 'sf2.mamez')).toBeNull();
		expect(operatorCorePaths('mame2003_plus')).toEqual({
			'mame2003_plus-wasm.data': '/api/emulator/cores/mame2003_plus/mame2003_plus-wasm.data',
			'mame2003_plus-legacy-wasm.data':
				'/api/emulator/cores/mame2003_plus/mame2003_plus-legacy-wasm.data',
			'mame2003_plus.json': '/api/emulator/cores/mame2003_plus/mame2003_plus.json'
		});
		expect(operatorCoreError('mame2003_plus', 404)).toBe(
			'Arcade (MAME 2003-Plus) games need the MAME 2003-Plus core files: ask an Admin to set them up (Emulator:Mame2003PlusCorePath).'
		);
		expect(operatorCoreError('mame2003_plus', 409)).toBe(
			"The MAME 2003-Plus core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:Mame2003PlusCorePath)."
		);
	});
});
