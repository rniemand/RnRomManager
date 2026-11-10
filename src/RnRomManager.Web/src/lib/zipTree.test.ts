import { describe, expect, it } from 'vitest';
import { ancestors, buildTree } from './zipTree';

const entry = (path: string, sizeBytes = 10, compressedSizeBytes = 4) => ({
	path,
	sizeBytes,
	compressedSizeBytes,
	modifiedUtc: '2026-11-01T10:00:00Z'
});

describe('buildTree (Z4)', () => {
	it('nests folders from the paths, keeps root files, and totals the whole ZIP', () => {
		const tree = buildTree([
			entry('info.json', 100, 50),
			entry('roms/a/deep.gb', 1, 1),
			entry('roms/Tetris.gb', 32768, 20000),
			entry('images/boxart-01.png')
		]);

		expect(tree.root.files.map((f) => f.name)).toEqual(['info.json']);
		expect(tree.root.folders.map((f) => f.path)).toEqual(['images', 'roms']);
		const roms = tree.folders.get('roms')!;
		expect(roms.files.map((f) => f.entry.path)).toEqual(['roms/Tetris.gb']);
		expect(roms.folders.map((f) => [f.name, f.path])).toEqual([['a', 'roms/a']]);
		expect(tree.folders.get('roms/a')!.files[0].name).toBe('deep.gb');
		expect([...tree.folders.keys()].sort()).toEqual(['', 'images', 'roms', 'roms/a']);
		expect([tree.fileCount, tree.totalSize, tree.totalCompressed]).toEqual([4, 32879, 20055]);
	});

	it('sorts folders and files by name ordinally (uppercase before lowercase)', () => {
		const tree = buildTree([
			entry('b.txt'),
			entry('B.txt'),
			entry('a.txt'),
			entry('z/x'),
			entry('Z/x')
		]);

		expect(tree.root.files.map((f) => f.name)).toEqual(['B.txt', 'a.txt', 'b.txt']);
		expect(tree.root.folders.map((f) => f.name)).toEqual(['Z', 'z']);
	});

	it('an empty listing is an empty root', () => {
		const tree = buildTree([]);
		expect([tree.root.folders, tree.root.files, tree.fileCount, tree.totalSize]).toEqual([
			[],
			[],
			0,
			0
		]);
	});

	it('ancestors lists the root, every parent and the folder itself', () => {
		expect(ancestors('')).toEqual(['']);
		expect(ancestors('a/b')).toEqual(['', 'a', 'a/b']);
	});
});
