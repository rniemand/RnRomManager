import { describe, expect, it } from 'vitest';
import { fallbackColour, systemColour } from './systemColours';

describe('systemColour', () => {
	it('gives NES warning with black text (N6)', () => {
		expect(systemColour('nes')).toEqual({ name: 'warning', foreground: '#000' });
	});

	it('keeps an unknown system neutral', () => {
		expect(systemColour('snes')).toBe(fallbackColour);
	});
});
