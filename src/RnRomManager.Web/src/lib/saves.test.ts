import { describe, expect, it } from 'vitest';
import type { GameDetail, SaveItem } from '#lib/types/index.js';
import { descriptionError, fileError, maxSaveBytes, nameError, playableRom } from './saves';

describe('field rules (B8)', () => {
	it('a name is trimmed, 1–100 characters, without control characters', () => {
		expect(nameError('  Before boss  ')).toBeNull();
		expect(nameError('   ')).toContain('1–100');
		expect(nameError('n'.repeat(100))).toBeNull();
		expect(nameError('n'.repeat(101))).toContain('1–100');
		expect(nameError('a\u0007b')).toContain('control');
	});

	it('a description is at most 1,000 characters, with line breaks the only control characters', () => {
		expect(descriptionError('')).toBeNull();
		expect(descriptionError('line\nline')).toBeNull();
		expect(descriptionError('d'.repeat(1000))).toBeNull();
		expect(descriptionError('d'.repeat(1001))).toContain('1000');
		expect(descriptionError('a\tb')).toContain('control');
		expect(descriptionError('a\r\nb')).toContain('control');
	});

	it('a file is 1 byte to 256 MiB', () => {
		expect(fileError(null)).toBe('Choose a save file.');
		expect(fileError(new File([], 'x'))).toBe('The save file is empty.');
		expect(fileError(new File([new Uint8Array(1)], 'x'))).toBeNull();
		const big = new File([], 'big');
		Object.defineProperty(big, 'size', { value: maxSaveBytes + 1 });
		expect(fileError(big)).toBe('The save is larger than 256 MiB.');
	});
});

describe('playableRom (S2, B16, B18)', () => {
	const game = (browserCore: string | null) =>
		({
			browserCore,
			roms: [
				{ id: 1, fileName: 'a.gb', externalFileName: null },
				{ id: 2, fileName: 'b.nds', externalFileName: null }
			]
		}) as unknown as GameDetail;
	const save = (romId: number | null, kind: 'state' | 'battery' = 'state') =>
		({ romId, kind }) as SaveItem;

	it('is the save release when the core plays it in the Play dialog', () => {
		expect(playableRom(game('gambatte'), save(1))?.id).toBe(1);
		expect(playableRom(game('gambatte'), save(1, 'battery'))?.id).toBe(1);
	});

	it('is null without a release, a core, or for a core that opens in a new tab', () => {
		expect(playableRom(game('gambatte'), save(99))).toBeNull();
		expect(playableRom(game('gambatte'), save(null))).toBeNull();
		expect(playableRom(game(null), save(1))).toBeNull();
		expect(playableRom(game('ppsspp'), save(1))).toBeNull();
	});

	it('a DeSmuME or Opera battery save is null; their state saves play', () => {
		expect(playableRom(game('desmume'), save(2, 'battery'))).toBeNull();
		expect(playableRom(game('opera'), save(1, 'battery'))).toBeNull();
		expect(playableRom(game('desmume'), save(2))?.id).toBe(2);
	});
});
