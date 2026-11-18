import { describe, expect, it } from 'vitest';
import { descriptionError, fileError, maxSaveBytes, nameError } from './saves';

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
