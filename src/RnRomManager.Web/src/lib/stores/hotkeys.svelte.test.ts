import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushSync } from 'svelte';
import { resetClient } from '../api/client';
import type { Me } from '../api/types';
import { defaultBindings } from '../hotkeys';
import { auth } from './auth.svelte';

const goto = vi.fn<(url: string) => Promise<void>>(() => Promise.resolve());
vi.mock('$app/navigation', () => ({ goto: (url: string) => goto(url) }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { hotkeys } = await import('./hotkeys.svelte');

const admin: Me = { id: 1, username: 'a', roles: ['admin'], mustChangePassword: false };
const user: Me = { id: 2, username: 'u', roles: [], mustChangePassword: false };

type Answer = { status: number; body?: unknown };

function keydown(code: string, init: KeyboardEventInit = {}, altGraph = false): KeyboardEvent {
	const e = new KeyboardEvent('keydown', { code, cancelable: true, ...init });
	if (altGraph)
		Object.defineProperty(e, 'getModifierState', { value: (k: string) => k === 'AltGraph' });
	return e;
}

const altShift = (code: string, init: KeyboardEventInit = {}) =>
	keydown(code, { altKey: true, shiftKey: true, ...init });

describe('hotkeys store (B24)', () => {
	let calls: string[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		goto.mockClear();
		calls = [];
		answers = {
			'GET /api/auth/me/hotkeys': { status: 200, body: { bindings: defaultBindings() } }
		};
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
		hotkeys.follow(null);
		auth.set(null);
		document.body.innerHTML = '';
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		hotkeys.follow(null);
		auth.set(null);
	});

	async function signIn(me: Me): Promise<void> {
		auth.set(me);
		hotkeys.follow(me);
		await vi.waitFor(() => expect(hotkeys.bindings).not.toBeNull());
	}

	describe('loading', () => {
		it('loads once signed in with the password changed', async () => {
			await signIn(admin);
			hotkeys.follow({ ...admin });

			expect(calls.filter((c) => c === 'GET /api/auth/me/hotkeys')).toHaveLength(1);
			expect(hotkeys.bindings).toEqual(defaultBindings());
		});

		it('waits while mustChangePassword, then loads after the password change', async () => {
			hotkeys.follow({ ...admin, mustChangePassword: true });
			await Promise.resolve();
			expect(calls).toEqual([]);
			expect(hotkeys.bindings).toBeNull();

			hotkeys.follow(admin);
			await vi.waitFor(() => expect(hotkeys.bindings).toEqual(defaultBindings()));
			expect(calls).toEqual(['GET /api/auth/me/hotkeys']);
		});

		it('signing out clears the bindings', async () => {
			await signIn(admin);
			hotkeys.follow(null);
			expect(hotkeys.bindings).toBeNull();
		});

		it('a failed load leaves no bindings and no error', async () => {
			answers['GET /api/auth/me/hotkeys'] = { status: 500 };
			hotkeys.follow(admin);
			await vi.waitFor(() => expect(calls).toHaveLength(1));
			expect(hotkeys.bindings).toBeNull();
		});

		it('save PUTs and updates the live bindings', async () => {
			await signIn(admin);
			const next = { ...defaultBindings(), 'nav.library': 'Alt+Shift+K' };
			answers['PUT /api/auth/me/hotkeys'] = { status: 200, body: { bindings: next } };

			await hotkeys.save(next);

			expect(calls).toContain('PUT /api/auth/me/hotkeys');
			expect(hotkeys.bindings).toEqual(next);
		});
	});

	describe('handling keydown', () => {
		it('runs a matching action and prevents the default', async () => {
			await signIn(admin);
			const e = altShift('KeyI');

			expect(hotkeys.handle(e)).toBe(true);

			expect(e.defaultPrevented).toBe(true);
			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/ingestion'));
		});

		it('Alt+Shift+C goes to Collections for a plain user (B21)', async () => {
			await signIn(user);

			expect(hotkeys.handle(altShift('KeyC'))).toBe(true);
			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/collections'));
		});

		it('fires even when focus is in a text field', async () => {
			await signIn(user);
			const input = document.body.appendChild(document.createElement('input'));
			input.focus();

			expect(hotkeys.handle(altShift('KeyL'))).toBe(true);
			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/'));
		});

		it('ignores repeats', async () => {
			await signIn(admin);
			const e = altShift('KeyL', { repeat: true });
			expect(hotkeys.handle(e)).toBe(false);
			expect(e.defaultPrevented).toBe(false);
		});

		it('ignores AltGraph', async () => {
			await signIn(admin);
			expect(hotkeys.handle(keydown('KeyL', { altKey: true, shiftKey: true }, true))).toBe(false);
		});

		it('ignores keys while a modal is open', async () => {
			await signIn(admin);
			document.body.innerHTML = '<div class="modal show"></div>';
			expect(hotkeys.handle(altShift('KeyL'))).toBe(false);
		});

		it('ignores everything while mustChangePassword', async () => {
			await signIn(admin);
			auth.set({ ...admin, mustChangePassword: true });
			expect(hotkeys.handle(altShift('KeyL'))).toBe(false);
		});

		it.each([
			['KeyR', '/reports'],
			['KeyT', '/integrity'],
			['KeyA', '/admin/users'],
			['KeyD', '/deleted']
		])('K1: Alt+Shift+%s goes to %s for an admin', async (code, url) => {
			await signIn(admin);

			expect(hotkeys.handle(altShift(code))).toBe(true);
			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(url));
		});

		it('K1: a plain user gets Reports but not the admin-only shortcuts', async () => {
			await signIn(user);

			for (const code of ['KeyT', 'KeyA', 'KeyD'])
				expect(hotkeys.handle(altShift(code))).toBe(false);
			expect(hotkeys.handle(altShift('KeyR'))).toBe(true);
			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/reports'));
			expect(goto).toHaveBeenCalledTimes(1);
		});

		it("ignores actions the user's role doesn't allow", async () => {
			await signIn(user);
			const e = altShift('KeyI');
			expect(hotkeys.handle(e)).toBe(false);
			expect(e.defaultPrevented).toBe(false);
		});

		it('ignores unbound keys and modifiers alone', async () => {
			await signIn(admin);
			expect(hotkeys.handle(altShift('KeyQ'))).toBe(false);
			expect(hotkeys.handle(keydown('ShiftLeft', { shiftKey: true }))).toBe(false);
		});

		it('a duplicated binding fires only the first action in table order', async () => {
			answers['GET /api/auth/me/hotkeys'] = {
				status: 200,
				body: { bindings: { ...defaultBindings(), 'review.next': 'Alt+Shift+F' } }
			};
			await signIn(admin);

			hotkeys.handle(altShift('KeyF'));

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
			expect(calls.some((c) => c.startsWith('GET /api/review'))).toBe(false);
		});
	});

	describe('review.next', () => {
		const queue = 'GET /api/review?status=Pending&page=1&pageSize=1';

		it('goes to the first pending item', async () => {
			answers[queue] = {
				status: 200,
				body: { items: [{ id: 42 }], page: 1, pageSize: 1, total: 3 }
			};
			await signIn(admin);

			hotkeys.handle(altShift('KeyG'));

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review/42'));
			expect(calls).toContain(queue);
		});

		it('goes to the Review page when the queue is empty', async () => {
			answers[queue] = { status: 200, body: { items: [], page: 1, pageSize: 1, total: 0 } };
			await signIn(admin);

			hotkeys.handle(altShift('KeyG'));

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
		});

		it('goes to the Review page on an error', async () => {
			answers[queue] = { status: 500 };
			await signIn(admin);

			hotkeys.handle(altShift('KeyG'));

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/review'));
		});
	});

	it('the root layout follows auth and listens on window', async () => {
		flushSync();
		const source = (await import('../../routes/+layout.svelte?raw')).default as string;
		expect(source).toContain('$effect(() => hotkeys.follow(auth.me));');
		expect(source).toContain('<svelte:window onkeydown={(e) => hotkeys.handle(e)} />');
	});
});
