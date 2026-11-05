import { describe, expect, it } from 'vitest';
import { titleError, toList, toPatch, validateGameEdit, type GameEditForm } from './gameEdit';

const form = (changes: Partial<GameEditForm> = {}): GameEditForm => ({
	title: 'Tetris',
	releaseYear: '1989',
	developers: 'Nintendo',
	publishers: '',
	genres: '',
	description: '',
	...changes
});

describe('edit metadata form', () => {
	it('accepts a valid form', () => {
		expect(validateGameEdit(form())).toEqual({});
		expect(validateGameEdit(form({ releaseYear: '' }))).toEqual({});
	});

	it('title: 1–255 chars after trim, not only dots/spaces', () => {
		expect(titleError('  ')).toBeDefined();
		expect(titleError('x'.repeat(256))).toBeDefined();
		expect(titleError('...')).toBeDefined();
		expect(titleError(' Maus, Die ')).toBeUndefined();
	});

	it('release year: empty or 1970–2100', () => {
		for (const year of ['1969', '2101', '89', '19x9', '1989.5'])
			expect(validateGameEdit(form({ releaseYear: year })).releaseYear).toBeDefined();
		expect(validateGameEdit(form({ releaseYear: ' 2100 ' })).releaseYear).toBeUndefined();
	});

	it('lists: ≤ 20 distinct trimmed items of ≤ 255 chars', () => {
		const many = Array.from({ length: 21 }, (_, i) => `Dev ${i}`).join('\n');
		expect(validateGameEdit(form({ developers: many })).developers).toBeDefined();
		expect(validateGameEdit(form({ genres: 'x'.repeat(256) })).genres).toBeDefined();
		// duplicates and blank lines don't count towards the 20
		const dupes = Array.from({ length: 30 }, () => 'Same').join('\n\n');
		expect(validateGameEdit(form({ publishers: dupes })).publishers).toBeUndefined();
		expect(toList(' a \n\na\n b ')).toEqual(['a', 'b']);
	});

	it('description: ≤ 4000 chars', () => {
		expect(validateGameEdit(form({ description: 'x'.repeat(4001) })).description).toBeDefined();
	});

	it('builds the PATCH body', () => {
		expect(toPatch(form({ title: ' Tetris ', releaseYear: '', description: '  ' }))).toEqual({
			title: 'Tetris',
			releaseYear: null,
			developers: ['Nintendo'],
			publishers: [],
			genres: [],
			description: null
		});
	});
});
