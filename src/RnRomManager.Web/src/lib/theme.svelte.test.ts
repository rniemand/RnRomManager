import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';

vi.mock('$app/state', () => ({ page: { url: new URL('http://localhost/') } }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
vi.mock('$app/navigation', () => ({ afterNavigate: () => {} }));
vi.mock('./bootstrap', () => ({ Collapse: { getOrCreateInstance: () => ({ hide() {} }) } }));
const { default: Navbar } = await import('#lib/components/layout/Navbar.svelte');
import { theme } from './theme.svelte';

/** A fake `matchMedia` whose dark preference the test flips, firing `change` like the browser. */
function fakeMedia(prefersDark: boolean) {
	const listeners = new Set<() => void>();
	const media = {
		get matches() {
			return prefersDark;
		},
		addEventListener: (_: string, listener: () => void) => listeners.add(listener),
		removeEventListener: (_: string, listener: () => void) => listeners.delete(listener)
	};
	vi.stubGlobal('matchMedia', () => media);
	return {
		set(dark: boolean) {
			prefersDark = dark;
			for (const listener of listeners) listener();
		}
	};
}

// Stub storage instead of using jsdom's: Node 25's own localStorage global shadows it.
function fakeStorage(initial: Record<string, string> = {}) {
	const data = new Map(Object.entries(initial));
	vi.stubGlobal('localStorage', {
		getItem: (key: string) => data.get(key) ?? null,
		setItem: (key: string, value: string) => data.set(key, value),
		removeItem: (key: string) => data.delete(key)
	});
	return data;
}

const current = () => document.documentElement.getAttribute('data-bs-theme');

describe('theme toggle', () => {
	let stop: (() => void) | undefined;

	beforeEach(() => document.documentElement.removeAttribute('data-bs-theme'));

	afterEach(() => {
		stop?.();
		vi.unstubAllGlobals();
	});

	it('init reads the stored choice', () => {
		fakeStorage({ theme: 'dark' });
		fakeMedia(false);

		stop = theme.init();

		expect(theme.choice).toBe('dark');
		expect(current()).toBe('dark');
	});

	it('light/dark are stored and applied; auto removes the stored value', () => {
		const storage = fakeStorage();
		fakeMedia(true);
		stop = theme.init();

		theme.set('light');
		expect([current(), storage.get('theme')]).toEqual(['light', 'light']);

		theme.set('dark');
		expect([current(), storage.get('theme')]).toEqual(['dark', 'dark']);

		theme.set('auto');
		expect([current(), storage.has('theme')]).toEqual(['dark', false]);
	});

	it('auto follows prefers-color-scheme live; an explicit choice ignores it', () => {
		fakeStorage();
		const media = fakeMedia(false);
		stop = theme.init();
		expect(current()).toBe('light');

		media.set(true);
		expect(current()).toBe('dark');

		theme.set('light');
		media.set(false);
		media.set(true);
		expect(current()).toBe('light');
	});

	it('the user menu switches theme and marks the active choice (B6)', async () => {
		fakeStorage();
		fakeMedia(false);
		stop = theme.init();
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const choice = (name: string) => screen.getByRole('button', { name });
		expect(choice('Auto').getAttribute('aria-pressed')).toBe('true');

		await fireEvent.click(choice('Dark'));

		expect(current()).toBe('dark');
		expect(choice('Dark').getAttribute('aria-pressed')).toBe('true');
		expect(choice('Dark').classList.contains('active')).toBe(true);
		expect(choice('Light').getAttribute('aria-pressed')).toBe('false');
		expect(choice('Dark').closest('.dropdown-menu')).not.toBeNull();
	});
});
