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

	it('is true for an external PlayStation disc', () => {
		expect(
			canPlay({ browserCore: 'pcsx_rearmed' }, { externalFileName: 'Disc (USA) (Track 1).bin' })
		).toBe(true);
	});

	it('is true for an external PSP UMD image (PSP B1)', () => {
		expect(canPlay({ browserCore: 'ppsspp' }, { externalFileName: 'Umd (USA).iso' })).toBe(true);
	});

	it('is false for an external release of another core, and without a core', () => {
		expect(canPlay({ browserCore: 'fceumm' }, { externalFileName: 'x.nes' })).toBe(false);
		expect(canPlay({ browserCore: null }, { externalFileName: 'Saturn (Track 1).bin' })).toBe(
			false
		);
	});
});

describe('playUrl', () => {
	it('encodes the file name', () => {
		expect(playUrl('g-1', { id: 7, fileName: 'A & B #1 (World).gb' }, 'gambatte')).toBe(
			'/emulator/play.html?game=g-1&rom=7&core=gambatte&name=A%20%26%20B%20%231%20(World).gb'
		);
	});

	it('points PPSSPP at the isolated PSP page (PSP B3)', () => {
		expect(playUrl('g-2', { id: 9, fileName: 'Umd & Co (USA).iso' }, 'ppsspp')).toBe(
			'/emulator/play-psp.html?game=g-2&rom=9&core=ppsspp&name=Umd%20%26%20Co%20(USA).iso'
		);
		for (const core of ['pcsx_rearmed', 'fceumm', 'smsplus'])
			expect(playUrl('g', { id: 1, fileName: 'x' }, core)).toMatch(/^\/emulator\/play\.html\?/);
	});
});

describe('opensInNewTab', () => {
	it('is true only for PPSSPP', () => {
		expect(opensInNewTab('ppsspp')).toBe(true);
		for (const core of ['pcsx_rearmed', 'gambatte', 'smsplus', null])
			expect(opensInNewTab(core)).toBe(false);
	});
});
