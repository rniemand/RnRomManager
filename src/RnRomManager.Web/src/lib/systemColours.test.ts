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
	});

	it('keeps an unknown system neutral', () => {
		expect(systemColour('sg1000')).toBe(fallbackColour);
	});
});
