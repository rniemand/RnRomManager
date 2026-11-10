import { describe, expect, it } from 'vitest';
import { fallbackColour, systemColour } from './systemColours';

describe('systemColour', () => {
	it('gives NES warning with black text (N6)', () => {
		expect(systemColour('nes')).toEqual({ name: 'warning', foreground: '#000' });
	});

	it('gives SNES its own purple with white text (S6)', () => {
		expect(systemColour('snes')).toEqual({ name: 'snes', foreground: '#fff' });
		expect(systemColour('a2600')).toEqual({ name: 'a2600', foreground: '#fff' });
		expect(systemColour('coleco')).toEqual({ name: 'coleco', foreground: '#fff' });
		expect(systemColour('gg')).toEqual({ name: 'gg', foreground: '#fff' });
	});

	it('keeps an unknown system neutral', () => {
		expect(systemColour('md')).toBe(fallbackColour);
	});
});
