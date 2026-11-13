import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { Me } from '../api/types';
import { defaultBindings, type Bindings } from '../hotkeys';
import { toasts } from '../stores/toasts.svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('$app/navigation', () => ({ goto: () => Promise.resolve() }));
vi.mock('$app/paths', () => ({ resolve: (p: string) => p }));

const { default: HotkeysModal } = await import('./HotkeysModal.svelte');
const { hotkeys } = await import('../stores/hotkeys.svelte');

const admin: Me = { id: 1, username: 'a', roles: ['admin'], mustChangePassword: false };
const user: Me = { id: 2, username: 'u', roles: [], mustChangePassword: false };

type Answer = { status: number; body?: unknown };

describe('HotkeysModal (B25)', () => {
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		calls = [];
		answers = {};
		hotkeys.bindings = defaultBindings();
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body ? JSON.parse(init.body as string) : undefined });
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		hotkeys.bindings = null;
	});

	const open = (me: Me, bindings?: Partial<Bindings>) => {
		if (bindings) hotkeys.bindings = { ...defaultBindings(), ...bindings };
		return render(HotkeysModal, { open: true, me });
	};
	const row = (id: string) =>
		within(document.querySelector<HTMLElement>(`tr[data-action="${id}"]`)!);
	const shown = (id: string) =>
		[...document.querySelector(`tr[data-action="${id}"] td`)!.querySelectorAll('kbd')]
			.map((k) => k.textContent)
			.join('+');
	const button = (name: string | RegExp) =>
		screen.getByRole('button', { name, hidden: true }) as HTMLButtonElement;
	const save = () => button('Save');

	it('lists only the actions the role allows, with kbd keys', () => {
		open(user);

		const names = [...document.querySelectorAll('tbody th')].map((th) => th.textContent);
		expect(names).toEqual([
			'Open global search',
			'Go to Library',
			'Go to Collections',
			'Go to Reports'
		]);
		expect(shown('nav.collections')).toBe('Alt+Shift+C');
		expect(shown('nav.library')).toBe('Alt+Shift+L');
		expect(row('search').queryByRole('button', { hidden: true })).toBeNull();
		expect(document.querySelector('.modal-dialog')!.classList.contains('modal-lg')).toBe(true);
	});

	it('an admin sees every action; unassigned shows Not assigned', () => {
		open(admin, { 'review.next': null });

		expect(document.querySelectorAll('tbody tr')).toHaveLength(10);
		expect(row('review.next').getByText('Not assigned')).toBeTruthy();
	});

	it('capture sets the binding from the next non-modifier key', async () => {
		open(user);
		expect(save().disabled).toBe(true);

		await fireEvent.click(button('Edit Go to Library'));
		expect(row('nav.library').getByText('Press keys…').getAttribute('aria-live')).toBe('polite');
		const target = button('Cancel editing Go to Library');
		await fireEvent.keyDown(target, { code: 'ShiftLeft', key: 'Shift', shiftKey: true });
		expect(row('nav.library').getByText('Press keys…')).toBeTruthy();
		await fireEvent.keyDown(target, { code: 'KeyK', key: 'K', altKey: true, shiftKey: true });

		expect(shown('nav.library')).toBe('Alt+Shift+K');
		expect(save().disabled).toBe(false);
	});

	it('Backspace alone unassigns', async () => {
		open(user);

		await fireEvent.click(button('Edit Go to Library'));
		await fireEvent.keyDown(button('Cancel editing Go to Library'), {
			code: 'Backspace',
			key: 'Backspace'
		});

		expect(row('nav.library').getByText('Not assigned')).toBeTruthy();
	});

	it('Escape cancels capture without reaching the modal (so it stays open)', async () => {
		open(user);
		const reachedModal = vi.fn();
		document.querySelector('.modal')!.addEventListener('keydown', reachedModal);

		await fireEvent.click(button('Edit Go to Library'));
		const e = new KeyboardEvent('keydown', {
			code: 'Escape',
			key: 'Escape',
			bubbles: true,
			cancelable: true
		});
		button('Cancel editing Go to Library').dispatchEvent(e);
		await Promise.resolve();

		expect(reachedModal).not.toHaveBeenCalled();
		expect(e.defaultPrevented).toBe(true);
		expect(shown('nav.library')).toBe('Alt+Shift+L');
		expect(button('Edit Go to Library')).toBeTruthy();
	});

	it('a conflicting capture shows an inline error and is not applied', async () => {
		open(admin);

		await fireEvent.click(button('Edit Go to Library'));
		await fireEvent.keyDown(button('Cancel editing Go to Library'), {
			code: 'KeyI',
			key: 'I',
			altKey: true,
			shiftKey: true
		});

		expect(row('nav.library').getByRole('alert', { hidden: true }).textContent).toBe(
			'Already used by Go to Ingestion.'
		);
		expect(row('nav.library').getByText('Press keys…')).toBeTruthy();
		await fireEvent.keyDown(button('Cancel editing Go to Library'), {
			code: 'Escape',
			key: 'Escape'
		});
		expect(shown('nav.library')).toBe('Alt+Shift+L');
	});

	it.each([
		[{ code: 'KeyK', key: 'k', ctrlKey: true }, 'Reserved for global search.'],
		[{ code: 'KeyQ', key: 'q', ctrlKey: true, altKey: true }, 'Ctrl+Alt also needs Shift or Meta'],
		[{ code: 'KeyQ', key: 'Q', shiftKey: true }, 'Include Ctrl, Alt or Meta.'],
		[{ code: 'Space', key: ' ', altKey: true }, 'Use Ctrl, Alt, Shift or Meta plus one key']
	])('an invalid capture %o shows its error', async (init, error) => {
		open(user);

		await fireEvent.click(button('Edit Go to Library'));
		await fireEvent.keyDown(button('Cancel editing Go to Library'), init);

		expect(row('nav.library').getByRole('alert', { hidden: true }).textContent).toContain(error);
	});

	it('a stored duplicate shows the error on the later row and blocks Save', async () => {
		open(admin, { 'review.next': 'Alt+Shift+F' });

		expect(row('review.next').getByRole('alert', { hidden: true }).textContent).toBe(
			'Already used by Go to Review.'
		);
		expect(row('nav.review').queryByRole('alert', { hidden: true })).toBeNull();
		await fireEvent.click(button('Edit Go to Library'));
		await fireEvent.keyDown(button('Cancel editing Go to Library'), {
			code: 'KeyZ',
			key: 'Z',
			altKey: true,
			shiftKey: true
		});
		expect(save().disabled).toBe(true);

		await fireEvent.click(button('Edit Open the first review item'));
		await fireEvent.keyDown(button('Cancel editing Open the first review item'), {
			code: 'Delete',
			key: 'Delete'
		});
		expect(save().disabled).toBe(false);
	});

	it('Reset stages the defaults without a request', async () => {
		open(user, { 'nav.library': 'Alt+Q' });
		expect(shown('nav.library')).toBe('Alt+Q');

		await fireEvent.click(button('Reset to defaults'));

		expect(shown('nav.library')).toBe('Alt+Shift+L');
		expect(calls).toEqual([]);
		expect(save().disabled).toBe(false);
	});

	it('Save sends PUT with the pending edits, updates the live bindings and toasts', async () => {
		const next = { ...defaultBindings(), 'nav.library': 'Alt+Shift+K' };
		answers['PUT /api/auth/me/hotkeys'] = { status: 200, body: { bindings: next } };
		open(user);

		await fireEvent.click(button('Edit Go to Library'));
		await fireEvent.keyDown(button('Cancel editing Go to Library'), {
			code: 'KeyK',
			key: 'K',
			altKey: true,
			shiftKey: true
		});
		await fireEvent.click(save());

		await vi.waitFor(() =>
			expect(calls).toContainEqual({ key: 'PUT /api/auth/me/hotkeys', body: { bindings: next } })
		);
		await vi.waitFor(() => expect(hotkeys.bindings).toEqual(next));
		expect(toasts.items.map((t) => t.text)).toContain('Keyboard shortcuts saved.');
	});

	it('a failed save shows the error and keeps the edits', async () => {
		answers['PUT /api/auth/me/hotkeys'] = {
			status: 400,
			body: {
				code: 'ValidationFailed',
				title: 'Invalid',
				errors: { 'bindings.nav.library': ['Already used by Go to Ingestion.'] }
			}
		};
		open(user);

		await fireEvent.click(button('Edit Go to Library'));
		await fireEvent.keyDown(button('Cancel editing Go to Library'), {
			code: 'KeyI',
			key: 'I',
			altKey: true,
			shiftKey: true
		});
		await fireEvent.click(save());

		await vi.waitFor(() =>
			expect(row('nav.library').getByRole('alert', { hidden: true }).textContent).toBe(
				'Already used by Go to Ingestion.'
			)
		);
		expect(shown('nav.library')).toBe('Alt+Shift+I');
		expect(hotkeys.bindings).toEqual(defaultBindings());
	});
});
