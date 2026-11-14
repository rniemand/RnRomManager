import { describe, expect, it } from 'vitest';
import { canPlay, playUrl } from './emulator';

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
});
