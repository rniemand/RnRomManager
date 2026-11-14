import { describe, expect, it } from 'vitest';
import { canPlay, opensInNewTab, playUrl } from './emulator';

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

describe('playUrl', () => {
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
});

describe('opensInNewTab', () => {
	it('is true only for PPSSPP', () => {
		expect(opensInNewTab('ppsspp')).toBe(true);
		for (const core of ['pcsx_rearmed', 'gambatte', 'smsplus', null])
			expect(opensInNewTab(core)).toBe(false);
	});
});
