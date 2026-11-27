import { describe, expect, it } from 'vitest';
import { fallbackColour, systemColour } from './systemColours';

describe('systemColour', () => {
	it('gives NES warning with black text (N6)', () => {
		expect(systemColour('nes')).toEqual({ name: 'warning', foreground: '#000' });
	});

	it('gives SNES its own purple with white text (S6)', () => {
		expect(systemColour('snes')).toEqual({ name: 'snes', foreground: '#fff' });
		expect(systemColour('a2600')).toEqual({ name: 'a2600', foreground: '#fff' });
		expect(systemColour('lynx')).toEqual({ name: 'lynx', foreground: '#000' });
		expect(systemColour('jaguar')).toEqual({ name: 'jaguar', foreground: '#fff' });
		expect(systemColour('coleco')).toEqual({ name: 'coleco', foreground: '#fff' });
		expect(systemColour('gg')).toEqual({ name: 'gg', foreground: '#fff' });
		expect(systemColour('sms')).toEqual({ name: 'sms', foreground: '#fff' });
		expect(systemColour('md')).toEqual({ name: 'md', foreground: '#fff' });
	});

	it('gives 32X its own orange with black text (X5)', () => {
		expect(systemColour('s32x')).toEqual({ name: 's32x', foreground: '#000' });
		expect(systemColour('tg16')).toEqual({ name: 'tg16', foreground: '#fff' });
	});

	it('gives Switch its own near-black with white text (N3)', () => {
		expect(systemColour('switch')).toEqual({ name: 'switch', foreground: '#fff' });
	});

	it('gives the six disc systems their own colours (B22)', () => {
		expect(systemColour('psx')).toEqual({ name: 'psx', foreground: '#fff' });
		expect(systemColour('psp')).toEqual({ name: 'psp', foreground: '#fff' });
		expect(systemColour('gc')).toEqual({ name: 'gc', foreground: '#000' });
		expect(systemColour('wii')).toEqual({ name: 'wii', foreground: '#000' });
		expect(systemColour('x360')).toEqual({ name: 'x360', foreground: '#000' });
		expect(systemColour('saturn')).toEqual({ name: 'saturn', foreground: '#fff' });
		expect(systemColour('nds')).toEqual({ name: 'nds', foreground: '#000' });
	});

	it('gives Sega CD its own maroon with white text (Sega CD B2)', () => {
		expect(systemColour('segacd')).toEqual({ name: 'segacd', foreground: '#fff' });
		expect(systemColour('tgcd')).toEqual({ name: 'tgcd', foreground: '#fff' });
	});

	it('gives PC-FX plum with white text (PC-FX X3)', () => {
		expect(systemColour('pcfx')).toEqual({ name: 'pcfx', foreground: '#fff' });
	});

	it('gives MS-DOS its own slate with white text (MS-DOS S4)', () => {
		expect(systemColour('dos')).toEqual({ name: 'dos', foreground: '#fff' });
		expect(systemColour('mame2003')).toEqual({ name: 'mame2003', foreground: '#fff' });
		expect(systemColour('fbneo')).toEqual({ name: 'fbneo', foreground: '#fff' });
	});

	it('gives Neo Geo Pocket slate blue with white text and Neo Geo Pocket Color gold with black text', () => {
		expect(systemColour('ngp')).toEqual({ name: 'ngp', foreground: '#fff' });
		expect(systemColour('ngpc')).toEqual({ name: 'ngpc', foreground: '#000' });
	});

	it('gives WonderSwan silver and WonderSwan Color light cyan, both with black text', () => {
		expect(systemColour('ws')).toEqual({ name: 'ws', foreground: '#000' });
		expect(systemColour('wsc')).toEqual({ name: 'wsc', foreground: '#000' });
	});

	it('gives PC Engine SuperGrafx violet with white text and Pocket Challenge V2 lavender with black text', () => {
		expect(systemColour('sgx')).toEqual({ name: 'sgx', foreground: '#fff' });
		expect(systemColour('pc2')).toEqual({ name: 'pc2', foreground: '#000' });
	});

	it('gives Sega Pico pale lime with black text (Sega Pico P8)', () => {
		expect(systemColour('pico')).toEqual({ name: 'pico', foreground: '#000' });
	});

	it('gives 3DO its own olive with black text (3DO B2)', () => {
		expect(systemColour('3do')).toEqual({ name: '3do', foreground: '#000' });
	});

	it('gives Commodore 64 its palette light blue with white text (C64 B5)', () => {
		expect(systemColour('c64')).toEqual({ name: 'c64', foreground: '#fff' });
		expect(systemColour('pet')).toEqual({ name: 'pet', foreground: '#fff' });
		expect(systemColour('vic20')).toEqual({ name: 'vic20', foreground: '#fff' });
		expect(systemColour('c128')).toEqual({ name: 'c128', foreground: '#fff' });
		expect(systemColour('plus4')).toEqual({ name: 'plus4', foreground: '#fff' });
		expect(systemColour('amiga')).toEqual({ name: 'amiga', foreground: '#fff' });
		expect(systemColour('fds')).toEqual({ name: 'fds', foreground: '#fff' });
		expect(systemColour('3ds')).toEqual({ name: '3ds', foreground: '#000' });
		expect(systemColour('satellaview')).toEqual({ name: 'satellaview', foreground: '#fff' });
		expect(systemColour('sufami')).toEqual({ name: 'sufami', foreground: '#fff' });
	});

	it('gives Virtual Boy, Atari 5200 and Atari 7800 their own colours (B5)', () => {
		expect(systemColour('vb')).toEqual({ name: 'vb', foreground: '#000' });
		expect(systemColour('a5200')).toEqual({ name: 'a5200', foreground: '#fff' });
		expect(systemColour('a7800')).toEqual({ name: 'a7800', foreground: '#000' });
	});

	it('keeps an unknown system neutral', () => {
		expect(systemColour('sg1000')).toBe(fallbackColour);
	});
});
