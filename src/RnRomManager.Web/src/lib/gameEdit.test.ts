import { describe, expect, it } from 'vitest';
import type { GameDetail, IgdbMatch } from '#lib/types/index.js';
import {
	applyMatch,
	defaultCopyFields,
	titleError,
	toForm,
	toList,
	toPatch,
	validateGameEdit,
	type GameEditForm
} from './gameEdit';

const form = (changes: Partial<GameEditForm> = {}): GameEditForm => ({
	title: 'Tetris',
	releaseYear: '1989',
	developers: 'Nintendo',
	publishers: '',
	genres: '',
	description: '',
	igdbId: null,
	...changes
});

const match = (changes: Partial<IgdbMatch> = {}): IgdbMatch => ({
	igdbId: 1234,
	name: 'Tetris',
	releaseYear: 1989,
	developers: ['Nintendo'],
	publishers: ['Nintendo'],
	genres: ['Puzzle'],
	description: 'Falling blocks.',
	coverUrl: null,
	coverImageUrl: null,
	url: null,
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
			description: null,
			igdbId: null
		});
	});

	it('carries igdbId from the detail to the PATCH body', () => {
		for (const igdbId of [null, 0, 1234]) {
			const edited = toForm({
				title: 'Tetris',
				releaseYear: null,
				developers: [],
				publishers: [],
				genres: [],
				description: null,
				igdbId
			} as unknown as GameDetail);
			expect(edited.igdbId).toBe(igdbId);
			expect(toPatch(edited).igdbId).toBe(igdbId);
		}
	});
});

describe('IGDB lookup helpers', () => {
	it('disables rows whose IGDB value is empty or equal, ticks every enabled row', () => {
		const state = defaultCopyFields(
			form({ title: 'Tetris DX', developers: ' Nintendo \n', publishers: '', genres: 'Action' }),
			match({ description: null })
		);
		expect(state).toEqual({
			title: { disabled: false, checked: true },
			releaseYear: { disabled: true, checked: false },
			developers: { disabled: true, checked: false },
			publishers: { disabled: false, checked: true },
			genres: { disabled: false, checked: true },
			description: { disabled: true, checked: false }
		});
	});

	it('Title and Description compare trimmed and case-sensitive', () => {
		expect(defaultCopyFields(form({ title: ' Tetris ' }), match()).title.disabled).toBe(true);
		expect(defaultCopyFields(form({ title: 'TETRIS' }), match()).title).toEqual({
			disabled: false,
			checked: true
		});
		expect(defaultCopyFields(form({ title: '' }), match()).title.checked).toBe(true);
		expect(
			defaultCopyFields(form({ description: 'Falling blocks.  ' }), match()).description.disabled
		).toBe(true);
		expect(defaultCopyFields(form({ description: '  ' }), match()).description).toEqual({
			disabled: false,
			checked: true
		});
	});

	it('the year compares the trimmed text with the number as a string', () => {
		expect(defaultCopyFields(form({ releaseYear: ' 1989 ' }), match()).releaseYear.disabled).toBe(
			true
		);
		expect(defaultCopyFields(form({ releaseYear: '1990' }), match()).releaseYear).toEqual({
			disabled: false,
			checked: true
		});
		expect(defaultCopyFields(form({ releaseYear: '' }), match()).releaseYear.checked).toBe(true);
		expect(defaultCopyFields(form(), match({ releaseYear: null })).releaseYear.disabled).toBe(true);
	});

	it('lists compare as the form would save them, in order', () => {
		const order = defaultCopyFields(
			form({ publishers: 'Nintendo\nCapcom' }),
			match({ publishers: ['Capcom', 'Nintendo'] })
		);
		expect(order.publishers).toEqual({ disabled: false, checked: true });
		const same = defaultCopyFields(
			form({ publishers: 'Nintendo\n\nNintendo\nCapcom' }),
			match({ publishers: ['Nintendo', 'Capcom'] })
		);
		expect(same.publishers.disabled).toBe(true);
		expect(defaultCopyFields(form(), match({ genres: [] })).genres.disabled).toBe(true);
	});

	it('applyMatch copies the given fields, lists one per line, and links the id', () => {
		const m = match({ name: 'Tetris DX', publishers: ['Nintendo', 'Capcom'], releaseYear: 1998 });
		const applied = applyMatch(form({ description: 'Mine.' }), m, [
			'title',
			'releaseYear',
			'publishers'
		]);
		expect(applied).toEqual(
			form({
				title: 'Tetris DX',
				releaseYear: '1998',
				publishers: 'Nintendo\nCapcom',
				description: 'Mine.',
				igdbId: 1234
			})
		);
		expect(applyMatch(form(), m, ['description', 'genres'])).toEqual(
			form({ description: 'Falling blocks.', genres: 'Puzzle', igdbId: 1234 })
		);
	});

	it('applyMatch with nothing ticked only links', () => {
		expect(applyMatch(form(), match(), [])).toEqual(form({ igdbId: 1234 }));
	});
});
