import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushSync } from 'svelte';
import { resetClient } from '#lib/api/client.js';
import type { Me } from '#lib/types/index.js';
import { defaultBindings } from '#lib/hotkeys.js';
import { auth } from './auth.svelte';
import { toasts } from './toasts.svelte';

const goto = vi.fn<(url: string) => Promise<void>>(() => Promise.resolve());
vi.mock('$app/navigation', () => ({ goto: (url: string) => goto(url) }));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { hotkeys, missingIgdbIdMessages, missingImageMessages, missingMetadataMessages } =
	await import('./hotkeys.svelte');

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
		toasts.items = [];
		window.history.replaceState({}, '', '/');
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

	describe('reports.missingIgdbId.next', () => {
		const gameA = '11111111-1111-1111-1111-111111111111';
		const gameB = '22222222-2222-2222-2222-222222222222';
		const next = (after?: string) =>
			`GET /api/reports/missing-igdb-ids/next${after ? `?afterGameId=${after}` : ''}`;
		const toastTexts = () => toasts.items.map((t) => [t.text, t.kind]);

		it('Alt+Shift+U off a game page opens the first unlinked game with from=missing-igdb', async () => {
			window.history.replaceState({}, '', '/reports/missing-igdb-ids');
			answers[next()] = { status: 200, body: { id: gameA, currentIsOnly: false } };
			await signIn(admin);

			expect(hotkeys.handle(altShift('KeyU'))).toBe(true);
			await vi.waitFor(() =>
				expect(goto).toHaveBeenCalledWith(`/games/${gameA}?from=missing-igdb`)
			);
		});

		it('on a game page asks for the game after it', async () => {
			window.history.replaceState({}, '', `/games/${gameA}`);
			answers[next(gameA)] = { status: 200, body: { id: gameB, currentIsOnly: false } };
			await signIn(admin);

			await hotkeys.run('reports.missingIgdbId.next');

			expect(calls).toContain(next(gameA));
			expect(goto).toHaveBeenCalledWith(`/games/${gameB}?from=missing-igdb`);
		});

		it('shows the three toasts', async () => {
			expect(missingIgdbIdMessages).toEqual({
				onlyCurrent: 'This game is the only one still not linked to IGDB.',
				none: 'No more games are missing an IGDB ID.',
				failed: "Couldn't find the next game missing an IGDB ID."
			});
			window.history.replaceState({}, '', `/games/${gameA}`);
			await signIn(admin);
			answers[next(gameA)] = { status: 200, body: { id: null, currentIsOnly: true } };
			await hotkeys.nextMissingIgdbId();
			answers[next(gameA)] = { status: 200, body: { id: null, currentIsOnly: false } };
			await hotkeys.nextMissingIgdbId();
			answers[next(gameA)] = { status: 500, body: { status: 500, code: 'Boom', title: 'Boom' } };
			await hotkeys.nextMissingIgdbId();

			expect(toastTexts()).toEqual([
				[missingIgdbIdMessages.onlyCurrent, 'info'],
				[missingIgdbIdMessages.none, 'info'],
				[missingIgdbIdMessages.failed, 'danger']
			]);
			expect(goto).not.toHaveBeenCalled();
		});

		it('does nothing for a Site user', async () => {
			await signIn(user);

			expect(hotkeys.handle(altShift('KeyU'))).toBe(false);
		});
	});

	describe('reports.missingMetadata.next (Missing Metadata M5)', () => {
		const gameA = '11111111-1111-1111-1111-111111111111';
		const gameB = '22222222-2222-2222-2222-222222222222';
		const next = (after?: string) =>
			`GET /api/reports/missing-metadata/next${after ? `?afterGameId=${after}` : ''}`;
		const toastTexts = () => toasts.items.map((t) => [t.text, t.kind]);

		it('off a game page opens the first row with from=missing-metadata', async () => {
			window.history.replaceState({}, '', '/reports/missing-metadata');
			answers[next()] = { status: 200, body: { id: gameA, currentIsOnly: false } };
			await signIn(admin);

			await hotkeys.run('reports.missingMetadata.next');

			expect(goto).toHaveBeenCalledWith(`/games/${gameA}?from=missing-metadata`);
		});

		it('on a game page asks for the game after it', async () => {
			window.history.replaceState({}, '', `/games/${gameA}`);
			answers[next(gameA)] = { status: 200, body: { id: gameB, currentIsOnly: false } };
			await signIn(admin);

			await hotkeys.nextMissingMetadata();

			expect(calls).toContain(next(gameA));
			expect(goto).toHaveBeenCalledWith(`/games/${gameB}?from=missing-metadata`);
		});

		it('a bound key runs it', async () => {
			answers['GET /api/auth/me/hotkeys'] = {
				status: 200,
				body: { bindings: { ...defaultBindings(), 'reports.missingMetadata.next': 'Alt+Shift+N' } }
			};
			answers[next()] = { status: 200, body: { id: gameA, currentIsOnly: false } };
			await signIn(admin);

			expect(hotkeys.handle(altShift('KeyN'))).toBe(true);
			await vi.waitFor(() =>
				expect(goto).toHaveBeenCalledWith(`/games/${gameA}?from=missing-metadata`)
			);
		});

		it('shows the three toasts', async () => {
			expect(missingMetadataMessages).toEqual({
				onlyCurrent: 'This game is the only one still missing metadata.',
				none: 'No more games are missing metadata.',
				failed: "Couldn't find the next game missing metadata."
			});
			window.history.replaceState({}, '', `/games/${gameA}`);
			answers[next(gameA)] = { status: 200, body: { id: null, currentIsOnly: true } };
			await signIn(admin);
			await hotkeys.nextMissingMetadata();

			window.history.replaceState({}, '', '/');
			answers[next()] = { status: 200, body: { id: null, currentIsOnly: false } };
			await hotkeys.nextMissingMetadata();

			answers[next()] = { status: 500 };
			await hotkeys.nextMissingMetadata();

			expect(goto).not.toHaveBeenCalled();
			expect(toastTexts()).toEqual([
				[missingMetadataMessages.onlyCurrent, 'info'],
				[missingMetadataMessages.none, 'info'],
				[missingMetadataMessages.failed, 'danger']
			]);
		});

		it('overlapping presses share one request', async () => {
			let release!: () => void;
			const real = globalThis.fetch;
			await signIn(admin);
			vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
				if (!url.startsWith('/api/reports/missing-metadata/next')) return real(url, init);
				calls.push(`GET ${url}`);
				return new Promise<Response>((resolve) => {
					release = () =>
						resolve(
							new Response(JSON.stringify({ id: gameA, currentIsOnly: false }), { status: 200 })
						);
				});
			});

			const first = hotkeys.nextMissingMetadata();
			const second = hotkeys.nextMissingMetadata();
			expect(second).toBe(first);
			await vi.waitFor(() => expect(release).toBeTypeOf('function'));
			release();
			await first;

			expect(calls.filter((c) => c.startsWith(next()))).toHaveLength(1);
			expect(goto).toHaveBeenCalledTimes(1);
		});

		it('a stale answer is dropped', async () => {
			let release!: () => void;
			const real = globalThis.fetch;
			await signIn(admin);
			vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
				if (!url.startsWith('/api/reports/missing-metadata/next')) return real(url, init);
				return new Promise<Response>((resolve) => {
					release = () =>
						resolve(
							new Response(JSON.stringify({ id: gameA, currentIsOnly: false }), { status: 200 })
						);
				});
			});

			const pending = hotkeys.nextMissingMetadata();
			await vi.waitFor(() => expect(release).toBeTypeOf('function'));
			window.history.replaceState({}, '', '/elsewhere');
			release();
			await pending;

			expect(goto).not.toHaveBeenCalled();
		});
	});

	describe('reports.missingImage.next (library-reset-missing-images B3)', () => {
		const gameA = '11111111-1111-1111-1111-111111111111';
		const gameB = '22222222-2222-2222-2222-222222222222';
		const next = (after?: string) =>
			`GET /api/reports/missing-images/next${after ? `?afterGameId=${after}` : ''}`;
		const press = () => hotkeys.handle(altShift('KeyM'));
		const toastTexts = () => toasts.items.map((t) => [t.text, t.kind]);

		it('from the report (or any non-game page) opens the first row', async () => {
			window.history.replaceState({}, '', '/reports/missing-images');
			answers[next()] = { status: 200, body: { id: gameA, currentIsOnly: false } };
			await signIn(user);

			expect(press()).toBe(true);

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(`/games/${gameA}?tab=images`));
			expect(calls).toContain(next());
		});

		it('from a game page asks for the row after the current game', async () => {
			window.history.replaceState({}, '', `/games/${gameA}`);
			answers[next(gameA)] = { status: 200, body: { id: gameB, currentIsOnly: false } };
			await signIn(user);

			press();

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(`/games/${gameB}?tab=images`));
		});

		it('wraps: whatever the server picks is opened, even an earlier game', async () => {
			window.history.replaceState({}, '', `/games/${gameB}`);
			answers[next(gameB)] = { status: 200, body: { id: gameA, currentIsOnly: false } };
			await signIn(user);

			press();

			await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(`/games/${gameA}?tab=images`));
		});

		it('stays on the only remaining game with a message', async () => {
			window.history.replaceState({}, '', `/games/${gameA}`);
			answers[next(gameA)] = { status: 200, body: { id: null, currentIsOnly: true } };
			await signIn(user);

			await hotkeys.nextMissingImage();

			expect(goto).not.toHaveBeenCalled();
			expect(toastTexts()).toEqual([[missingImageMessages.onlyCurrent, 'info']]);
			expect(missingImageMessages.onlyCurrent).toBe(
				'This game is the only one still missing images.'
			);
		});

		it('an empty report says so', async () => {
			answers[next()] = { status: 200, body: { id: null, currentIsOnly: false } };
			await signIn(user);

			await hotkeys.nextMissingImage();

			expect(goto).not.toHaveBeenCalled();
			expect(toastTexts()).toEqual([['No more games are missing images.', 'info']]);
		});

		it('a failure shows a separate error toast', async () => {
			answers[next()] = { status: 500 };
			await signIn(user);

			await hotkeys.nextMissingImage();

			expect(goto).not.toHaveBeenCalled();
			expect(toastTexts()).toEqual([[missingImageMessages.failed, 'danger']]);
		});

		describe('with a held response', () => {
			let release: () => void;

			beforeEach(() => {
				const real = globalThis.fetch;
				vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
					if (!url.startsWith('/api/reports/missing-images/next')) return real(url, init);
					calls.push(`GET ${url}`);
					return new Promise<Response>((done) => {
						release = () => done(new Response(JSON.stringify({ id: gameB, currentIsOnly: false })));
					});
				});
			});

			it('overlapping presses share one request', async () => {
				await signIn(user);

				press();
				press();
				press();
				release();

				await vi.waitFor(() => expect(goto).toHaveBeenCalledWith(`/games/${gameB}?tab=images`));
				expect(calls.filter((c) => c === next())).toHaveLength(1);
				expect(goto).toHaveBeenCalledTimes(1);
			});

			it('drops the answer after navigation', async () => {
				await signIn(user);

				const pending = hotkeys.nextMissingImage();
				window.history.replaceState({}, '', '/collections');
				release();
				await pending;

				expect(goto).not.toHaveBeenCalled();
				expect(toasts.items).toEqual([]);
			});

			it('drops the answer after the session changes', async () => {
				await signIn(user);

				const pending = hotkeys.nextMissingImage();
				hotkeys.follow(null);
				release();
				await pending;

				expect(goto).not.toHaveBeenCalled();
			});
		});
	});

	it('the root layout follows auth and listens on window', async () => {
		flushSync();
		const source = (await import('../../routes/+layout.svelte?raw')).default as string;
		expect(source).toContain('$effect(() => hotkeys.follow(auth.me));');
		expect(source).toContain('<svelte:window onkeydown={(e) => hotkeys.handle(e)} />');
	});
});
