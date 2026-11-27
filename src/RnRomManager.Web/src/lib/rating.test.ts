import { describe, expect, it } from 'vitest';
import { igdbScore } from './rating.js';

describe('igdbScore (GS2)', () => {
	it.each([
		[81.4, 81],
		[81.5, 82],
		[81.6, 82],
		[0, 0],
		[100, 100],
		[-2.5, -3],
		[-2.4, -2]
	])('%d → %d, rounding half away from zero', (score, expected) => {
		expect(igdbScore(score)).toBe(expected);
	});
});
