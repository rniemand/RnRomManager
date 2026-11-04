import { afterEach, describe, expect, it, vi } from 'vitest';
import script from '../../static/theme.js?raw';

// Stub storage instead of using jsdom's: Node 25's own localStorage global shadows it.
function run(getItem: (key: string) => string | null, prefersDark: boolean): string | null {
	vi.stubGlobal('localStorage', { getItem });
	vi.stubGlobal('matchMedia', (query: string) => ({
		matches: query === '(prefers-color-scheme: dark)' && prefersDark
	}));
	new Function(script)();
	return document.documentElement.getAttribute('data-bs-theme');
}

describe('theme.js', () => {
	afterEach(() => {
		vi.unstubAllGlobals();
		document.documentElement.removeAttribute('data-bs-theme');
	});

	it.each([
		['light', true, 'light'],
		['dark', false, 'dark'],
		['auto', true, 'dark'],
		['auto', false, 'light'],
		[null, true, 'dark'],
		[null, false, 'light'],
		['bogus', false, 'light']
	])('stored %s, prefers dark %s -> %s', (stored, prefersDark, expected) => {
		const getItem = (key: string) => (key === 'theme' ? stored : null);
		expect(run(getItem, prefersDark)).toBe(expected);
	});

	it('falls back to auto when localStorage throws', () => {
		const getItem = () => {
			throw new Error('blocked');
		};
		expect(run(getItem, true)).toBe('dark');
	});
});
