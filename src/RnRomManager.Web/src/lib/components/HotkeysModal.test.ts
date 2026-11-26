import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { Me } from '#lib/types/index.js';
import { defaultBindings, type Bindings } from '../hotkeys';
import { toasts } from '../stores/toasts.svelte';

const modal = vi.hoisted(() => ({ hides: 0 }));
vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {
			modal.hides++;
		}
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
		hotkeys.implicitUnassigned = [];
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
			'Go to Reports',
			'Next missing image'
		]);
		expect(shown('nav.collections')).toBe('Alt+Shift+C');
		expect(shown('nav.library')).toBe('Alt+Shift+L');
		// U7: search isn't editable; its only button is Run.
		expect(
			row('search')
				.queryAllByRole('button', { hidden: true })
				.map((b) => b.getAttribute('aria-label'))
		).toEqual(['Run Open global search']);
		expect(document.querySelector('.modal-dialog')!.classList.contains('modal-lg')).toBe(true);
	});

	it('an admin sees every action; unassigned shows Not assigned', () => {
		open(admin, { 'review.next': null });

		expect(document.querySelectorAll('tbody tr')).toHaveLength(13);
		expect(row('review.next').getByText('Not assigned')).toBeTruthy();
		// Missing Metadata M5: no default, so Not assigned and no "Its default" hint.
		expect(row('reports.missingMetadata.next').getByText('Not assigned')).toBeTruthy();
		expect(row('reports.missingMetadata.next').queryByText(/Its default/)).toBeNull();
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

	describe('Next missing image yielding to an older override (library-reset-missing-images B2)', () => {
		const next = 'reports.missingImage.next';
		const putBody = () =>
			(calls.find((c) => c.key === 'PUT /api/auth/me/hotkeys')?.body as { bindings: Bindings })
				?.bindings;

		beforeEach(() => {
			answers['PUT /api/auth/me/hotkeys'] = {
				status: 200,
				body: { bindings: defaultBindings(), implicitUnassigned: [] }
			};
			hotkeys.implicitUnassigned = [next];
		});

		const openImplicit = () => open(user, { 'nav.library': 'Alt+Shift+M', [next]: null });

		it('shows it as not assigned with the reason', () => {
			openImplicit();

			expect(row(next).getByText('Not assigned')).toBeTruthy();
			expect(row(next).getByText(/Alt\+Shift\+M, is used by another shortcut/)).toBeTruthy();
		});

		it('an unrelated save leaves it out of the PUT', async () => {
			openImplicit();

			await fireEvent.click(button('Edit Go to Collections'));
			await fireEvent.keyDown(button('Cancel editing Go to Collections'), {
				code: 'KeyQ',
				key: 'Q',
				altKey: true,
				shiftKey: true
			});
			await fireEvent.click(save());

			await vi.waitFor(() => expect(putBody()).toBeDefined());
			expect(next in putBody()!).toBe(false);
			expect(putBody()!['nav.library']).toBe('Alt+Shift+M');
			expect(putBody()!['nav.collections']).toBe('Alt+Shift+Q');
		});

		it('a deliberate disable can be saved and sends null', async () => {
			openImplicit();
			expect(save().disabled).toBe(true);

			await fireEvent.click(button('Edit Next missing image'));
			await fireEvent.keyDown(button('Cancel editing Next missing image'), {
				code: 'Backspace',
				key: 'Backspace'
			});
			expect(save().disabled).toBe(false);
			await fireEvent.click(save());

			await vi.waitFor(() => expect(putBody()).toBeDefined());
			expect(putBody()![next]).toBeNull();
		});

		it('Reset all sends the default and frees it from the older binding', async () => {
			openImplicit();

			await fireEvent.click(button('Reset to defaults'));
			expect(shown(next)).toBe('Alt+Shift+M');
			await fireEvent.click(save());

			await vi.waitFor(() => expect(putBody()).toBeDefined());
			expect(putBody()![next]).toBe('Alt+Shift+M');
			expect(putBody()!['nav.library']).toBe('Alt+Shift+L');
			await vi.waitFor(() => expect(hotkeys.implicitUnassigned).toEqual([]));
		});
	});

	describe('Keyboard Shortcuts title and Run buttons (U7)', () => {
		it('the title and hidden caption read Keyboard Shortcuts; the last column is Actions', () => {
			open(user);
			expect(
				screen.getByRole('heading', { name: 'Keyboard Shortcuts', hidden: true })
			).toBeTruthy();
			expect(document.querySelector('caption')!.textContent).toBe('Keyboard Shortcuts');
			expect(document.querySelector('thead th:last-child')!.textContent!.trim()).toBe('Actions');
		});

		it('every row has Run before Edit, with the play icon', () => {
			open(admin);
			for (const tr of document.querySelectorAll('tbody tr')) {
				const buttons = [...tr.querySelectorAll('td:last-child button')];
				const name = tr.querySelector('th')!.textContent;
				expect(buttons[0].getAttribute('aria-label')).toBe(`Run ${name}`);
				expect(buttons[0].className).toContain('btn btn-sm btn-outline-primary');
				expect(buttons[0].textContent!.trim()).toBe('Run');
				expect(buttons[0].querySelector('i')!.className.trim()).toBe('bi bi-play-fill');
				const editable = tr.getAttribute('data-action') !== 'search';
				expect(buttons.length).toBe(editable ? 2 : 1);
				expect(buttons[0].classList.contains('me-1')).toBe(editable);
				if (editable) expect(buttons[1].getAttribute('aria-label')).toBe(`Edit ${name}`);
			}
		});

		it('Run closes the modal and runs the action', async () => {
			const run = vi.spyOn(hotkeys, 'run').mockResolvedValue();
			render(HotkeysModal, { open: true, me: admin });
			modal.hides = 0;
			await fireEvent.click(button('Run Go to Reports'));

			await vi.waitFor(() => expect(run).toHaveBeenCalledWith('nav.reports'));
			expect(modal.hides).toBeGreaterThan(0);
			run.mockRestore();
		});

		it('Run on search closes the modal and calls onsearch', async () => {
			const onsearch = vi.fn();
			const run = vi.spyOn(hotkeys, 'run').mockResolvedValue();
			render(HotkeysModal, { open: true, me: user, onsearch });
			modal.hides = 0;
			await fireEvent.click(button('Run Open global search'));

			await vi.waitFor(() => expect(onsearch).toHaveBeenCalledOnce());
			expect(modal.hides).toBeGreaterThan(0);
			expect(run).not.toHaveBeenCalled();
			run.mockRestore();
		});

		it('Run is disabled with the hint while capturing or with unsaved changes', async () => {
			const hint = () => screen.queryByText('Save or cancel your changes to run a shortcut');
			open(user);
			expect(button('Run Go to Library').disabled).toBe(false);
			expect(hint()).toBeNull();

			await fireEvent.click(button('Edit Go to Library'));
			expect(button('Run Go to Library').disabled).toBe(true);
			expect(hint()!.className).toBe('small text-body-secondary');

			await fireEvent.keyDown(button(/Cancel editing Go to Library/), {
				code: 'KeyQ',
				key: 'q',
				altKey: true,
				shiftKey: true
			});
			expect(button('Run Go to Collections').disabled).toBe(true);
			expect(hint()).not.toBeNull();

			await fireEvent.click(button('Reset to defaults'));
			expect(button('Run Go to Collections').disabled).toBe(false);
			expect(hint()).toBeNull();
		});
	});
});
