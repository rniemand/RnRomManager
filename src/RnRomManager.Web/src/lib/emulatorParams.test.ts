import { describe, expect, it } from 'vitest';
import {
	biosError,
	biosFor,
	contentFileName,
	controlSchemeFor,
	discError,
	hidesCoreChooser,
	optionalBiosError,
	parsePlayParams,
	pspBrowserError,
	psxBiosBytes
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
			name: 'A #1 & B.gba'
		});
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
		'pcsx_rearmed'
	])('accepts the %s core', (core) => {
		expect(parsePlayParams(query({ core }))?.core).toBe(core);
	});

	it.each([
		['a bad GUID', { game: 'not-a-guid' }],
		['rom 0', { rom: '0' }],
		['rom -1', { rom: '-1' }],
		['rom 1.5', { rom: '1.5' }],
		['an unknown core', { core: 'snes9x' }],
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
			name: 'Umd (USA).iso'
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
