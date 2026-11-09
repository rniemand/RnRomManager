import { afterEach, describe, expect, it, vi } from 'vitest';
import { collectionsApi, maxNameLength, nameError, notesError } from './collections';

describe('collection rules (B2)', () => {
	it('name: trimmed, 1–100 code points', () => {
		expect(maxNameLength).toBe(100);
		expect(nameError('')).toBe('A name is required.');
		expect(nameError('   ')).toBe('A name is required.');
		expect(nameError('a')).toBeNull();
		expect(nameError(' ' + 'é'.repeat(100) + ' ')).toBeNull();
		expect(nameError('😀'.repeat(100))).toBeNull();
		expect(nameError('x'.repeat(101))).toBe('The name must be at most 100 characters.');
	});

	it('notes: at most 65,536 UTF-8 bytes after line-ending normalisation', () => {
		expect(notesError('é'.repeat(32768))).toBeNull();
		expect(notesError('é'.repeat(32768) + 'x')).toBe(
			'The notes must be at most 65,536 bytes (UTF-8).'
		);
		expect(notesError('x'.repeat(65535) + '\r\n')).toBeNull();
	});
});

describe('collectionsApi', () => {
	afterEach(() => vi.unstubAllGlobals());

	it('list sends only the given, trimmed parameters', async () => {
		const fetchMock = vi.fn(() =>
			Promise.resolve(new Response('{"items":[],"page":1,"pageSize":5,"total":0}'))
		);
		vi.stubGlobal('fetch', fetchMock);

		await collectionsApi.list({ q: ' poke ', pageSize: 5 });
		await collectionsApi.list();

		const urls = fetchMock.mock.calls.map((c) => (c as unknown as [string])[0]);
		expect(urls).toEqual(['/api/collections?q=poke&pageSize=5', '/api/collections']);
	});
});
