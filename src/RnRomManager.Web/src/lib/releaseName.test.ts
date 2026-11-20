import { describe, expect, it } from 'vitest';
import { editReleaseFileName, type ReleaseTags } from './releaseName';

const none: ReleaseTags = {
	regions: [],
	languages: [],
	revision: null,
	hack: false,
	translated: false,
	version: null
};
const edit = (
	name: string,
	title: string,
	before: Partial<ReleaseTags>,
	after: Partial<ReleaseTags>
) => editReleaseFileName(name, title, { ...none, ...before }, { ...none, ...before, ...after });

describe('editReleaseFileName (release editing B7, mirrors ReleaseFileNameTests)', () => {
	it('gives the spec examples', () => {
		const tetris = { regions: ['World'], revision: 'Rev 1' };
		expect(edit('Tetris (World) (Rev 1).gb', 'Tetris', tetris, { hack: true })).toBe(
			'Tetris (World) (Rev 1) (Hack).gb'
		);
		expect(edit('Puzzle Land (Hack).gb', 'Puzzle Land', { hack: true }, { hack: false })).toBe(
			'Puzzle Land.gb'
		);
		const orbit = 'Orbit [0100000000003800][v65536][1.0.1].nsp';
		expect(edit(orbit, 'Orbit', { version: '1.0.1' }, { version: '1.0.2' })).toBe(
			'Orbit [0100000000003800][v65536][1.0.2].nsp'
		);
		expect(edit(orbit, 'Orbit', { version: '1.0.1' }, { version: null })).toBe(
			'Orbit [0100000000003800][v65536].nsp'
		);
		expect(edit('Orbit [0100000000003800][v0].nsp', 'Orbit', {}, { version: '1.0.1' })).toBe(
			'Orbit [0100000000003800][v0][1.0.1].nsp'
		);
	});

	it('leaves unchanged fields alone', () => {
		expect(edit('weird  name..(Hack).gb', 'X', { regions: ['USA'], hack: true }, {})).toBe(
			'weird  name..(Hack).gb'
		);
		expect(edit('my source (Hack).gb', 'Game', { hack: true }, { translated: true })).toBe(
			'my source (Hack) (Translated).gb'
		);
	});

	it('replaces a found group ignoring case, and removes one with its space', () => {
		const tags = { regions: ['USA', 'Europe'], languages: ['En', 'Fr'], revision: 'Rev 1' };
		const name = 'Game (usa, europe) (En,Fr) (Rev 1).gb';
		expect(edit(name, 'Game', tags, { regions: ['Japan'] })).toBe(
			'Game (Japan) (En,Fr) (Rev 1).gb'
		);
		expect(edit(name, 'Game', tags, { languages: [] })).toBe('Game (usa, europe) (Rev 1).gb');
		expect(edit(name, 'Game', tags, { revision: null })).toBe('Game (usa, europe) (En,Fr).gb');
	});

	it('inserts before the first [ group, or at the end', () => {
		expect(edit('Orbit [0100000000003800][v0].nsp', 'Orbit', {}, { hack: true })).toBe(
			'Orbit (Hack) [0100000000003800][v0].nsp'
		);
		expect(edit('Orbit[0100000000003800].nsp', 'Orbit', {}, { languages: ['En'] })).toBe(
			'Orbit (En)[0100000000003800].nsp'
		);
		expect(edit('Game (USA).gb', 'Game', { regions: ['USA'] }, { revision: 'Rev A' })).toBe(
			'Game (USA) (Rev A).gb'
		);
	});

	it('puts a new region group after the base title, else at the default position', () => {
		expect(
			edit(
				'game (Rev 1) (Hack).gb',
				'Game',
				{ revision: 'Rev 1', hack: true },
				{ regions: ['USA'] }
			)
		).toBe('game (USA) (Rev 1) (Hack).gb');
		expect(edit('source dump [x].gb', 'Game', {}, { regions: ['USA'] })).toBe(
			'source dump (USA) [x].gb'
		);
		expect(edit('source dump.gb', 'Game', {}, { regions: ['USA'] })).toBe('source dump (USA).gb');
	});
});
