import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clearLibraryFilter, readSavedLibraryFilter, saveLibraryFilter } from './libraryFilter';

describe('libraryFilter (L7)', () => {
	let store: Map<string, string>;

	beforeEach(() => {
		store = new Map();
		vi.stubGlobal('localStorage', {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value),
			removeItem: (key: string) => void store.delete(key)
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	it('saves, reads and clears under rnrm.library.filter', () => {
		expect(readSavedLibraryFilter()).toBe('');

		saveLibraryFilter('?system=gb&page=2');
		expect(store.get('rnrm.library.filter')).toBe('?system=gb&page=2');
		expect(readSavedLibraryFilter()).toBe('?system=gb&page=2');

		clearLibraryFilter();
		expect(store.has('rnrm.library.filter')).toBe(false);
	});

	it('an empty search removes the key', () => {
		saveLibraryFilter('?q=zelda');
		saveLibraryFilter('');
		expect(store.has('rnrm.library.filter')).toBe(false);
	});

	it('storage that throws is ignored', () => {
		const fail = () => {
			throw new Error('denied');
		};
		vi.stubGlobal('localStorage', { getItem: fail, setItem: fail, removeItem: fail });

		expect(readSavedLibraryFilter()).toBe('');
		expect(() => saveLibraryFilter('?system=gb')).not.toThrow();
		expect(() => clearLibraryFilter()).not.toThrow();
	});
});
