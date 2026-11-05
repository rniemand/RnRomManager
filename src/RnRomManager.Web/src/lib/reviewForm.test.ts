import { describe, expect, it } from 'vitest';
import {
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
});
