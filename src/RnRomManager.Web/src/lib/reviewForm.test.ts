import { describe, expect, it } from 'vitest';
import {
	discError,
	emptyManualForm,
	manualFileStem,
	splitList,
	toManualRequest,
	validateManual
} from './reviewForm';

const form = (changes = {}) => ({ ...emptyManualForm(), title: 'Tetris', ...changes });

describe('manual review form', () => {
	it('needs a valid title', () => {
		expect(validateManual(form())).toEqual({});
		expect(validateManual(form({ title: '  ' })).title).toBeDefined();
		expect(validateManual(form({ title: '..' })).title).toBeDefined();
		expect(validateManual(form({ title: 'x'.repeat(256) })).title).toBeDefined();
	});

	it('revision at most 32 characters', () => {
		expect(validateManual(form({ revision: 'x'.repeat(33) })).revision).toBeDefined();
		expect(validateManual(form({ revision: 'Rev 1' })).revision).toBeUndefined();
	});

	it('lists drop blanks, so they can never send empty strings', () => {
		expect(splitList(' USA, ,Europe, ')).toEqual(['USA', 'Europe']);
		expect(splitList('')).toEqual([]);
	});

	it('builds the request and the file-name preview', () => {
		const f = form({ regions: 'USA, Europe', revision: ' Rev 1 ', flags: 'Proto' });
		expect(toManualRequest(f)).toEqual({
			action: 'Manual',
			title: 'Tetris',
			regions: ['USA', 'Europe'],
			languages: [],
			revision: 'Rev 1',
			flags: ['Proto']
		});
		expect(manualFileStem(f)).toBe('Tetris (USA, Europe) (Rev 1) (Proto)');
		expect(manualFileStem(form())).toBe('Tetris');
		expect(toManualRequest(form()).revision).toBeNull();
	});

	it('a disc number is optional on Manual, 1–99 when given, and goes after the flags (B9, B10, B17)', () => {
		expect(validateManual(form({ disc: null })).disc).toBeUndefined();
		expect(validateManual(form({ disc: 0 })).disc).toBe('Must be 1–99.');
		expect(validateManual(form({ disc: 100 })).disc).toBe('Must be 1–99.');
		expect(validateManual(form({ disc: 1.5 })).disc).toBe('Must be 1–99.');
		expect(validateManual(form({ disc: 99 })).disc).toBeUndefined();
		expect(toManualRequest(form({ disc: 2 }))).toMatchObject({ disc: 2 });
		expect(toManualRequest(form())).not.toHaveProperty('disc');
		expect(manualFileStem(form({ flags: 'Disc 3, Beta', disc: 2 }))).toBe('Tetris (Beta) (Disc 2)');
	});

	it('Attach as a disc requires the number (B16)', () => {
		expect(discError(null, true)).toBe('A disc number is required.');
		expect(discError(Number.NaN, true)).toBe('A disc number is required.');
		expect(discError(2, true)).toBeUndefined();
	});
});
