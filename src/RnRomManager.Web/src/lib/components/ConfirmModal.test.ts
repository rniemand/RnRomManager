import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { createRawSnippet, flushSync, tick } from 'svelte';

const options: unknown[] = [];
const calls: string[] = [];
vi.mock('../bootstrap', () => ({
	Modal: class {
		constructor(_element: Element, config: unknown) {
			options.push(config);
		}
		show() {}
		hide() {
			calls.push('hide');
		}
		dispose() {
			calls.push('dispose');
		}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => calls.push('releaseModal') }));

const { default: ConfirmModal } = await import('./ConfirmModal.svelte');

describe('ConfirmModal', () => {
	it('uses a static backdrop with Escape enabled (M1)', () => {
		render(ConfirmModal, { open: false, title: 'T', onconfirm: () => {} });
		expect(options.at(-1)).toEqual({ backdrop: 'static', keyboard: true });
	});

	it('scrollable adds modal-dialog-scrollable; it is off by default (U2)', () => {
		const { unmount } = render(ConfirmModal, { open: false, title: 'T', onconfirm: () => {} });
		expect(
			document.querySelector('.modal-dialog')!.classList.contains('modal-dialog-scrollable')
		).toBe(false);
		unmount();
		render(ConfirmModal, { open: false, title: 'T', scrollable: true, onconfirm: () => {} });
		expect(
			document.querySelector('.modal-dialog')!.classList.contains('modal-dialog-scrollable')
		).toBe(true); // The scrolling body is reachable and scrollable with the keyboard (axe scrollable-region-focusable).
		const body = document.querySelector('.modal-body')!;
		expect(['tabindex', 'role', 'aria-label'].map((n) => body.getAttribute(n))).toEqual([
			'0',
			'region',
			'T'
		]);
	});

	it('shows Cancel and the confirm button when onconfirm is given', () => {
		render(ConfirmModal, { open: false, title: 'T', confirmLabel: 'Delete', onconfirm: () => {} });
		expect(screen.getByRole('button', { name: 'Cancel', hidden: true })).toBeTruthy();
		expect(screen.getByRole('button', { name: 'Delete', hidden: true })).toBeTruthy();
	});

	it('confirmDisabled disables only the confirm button', () => {
		render(ConfirmModal, {
			open: false,
			title: 'T',
			confirmLabel: 'Save',
			confirmDisabled: true,
			onconfirm: () => {}
		});
		const save = screen.getByRole('button', { name: 'Save', hidden: true }) as HTMLButtonElement;
		const cancel = screen.getByRole('button', {
			name: 'Cancel',
			hidden: true
		}) as HTMLButtonElement;
		expect(save.disabled).toBe(true);
		expect(cancel.disabled).toBe(false);
	});

	it('the confirm button is enabled by default', () => {
		render(ConfirmModal, { open: false, title: 'T', confirmLabel: 'Go', onconfirm: () => {} });
		const go = screen.getByRole('button', { name: 'Go', hidden: true }) as HTMLButtonElement;
		expect(go.disabled).toBe(false);
	});

	it('info mode (no onconfirm) shows only Close', () => {
		render(ConfirmModal, { open: false, title: 'T' });
		const footer = document.querySelector('.modal-footer')!;
		expect([...footer.querySelectorAll('button')].map((b) => b.textContent?.trim())).toEqual([
			'Close'
		]);
	});

	describe('footer snippet (U3)', () => {
		const footer = createRawSnippet(() => ({
			render: () => '<button type="button">Extra</button>'
		}));
		const footerButtons = () =>
			[...document.querySelectorAll('.modal-footer button')].map((b) => b.textContent?.trim());

		it('renders first in the footer, in a me-auto wrapper before Close (info mode)', () => {
			render(ConfirmModal, { open: false, title: 'T', footer });
			const extra = screen.getByRole('button', { name: 'Extra', hidden: true });
			const wrapper = extra.parentElement!;
			expect(wrapper.className).toBe('d-flex flex-wrap gap-2 me-auto');
			expect(wrapper.parentElement!.classList).toContain('modal-footer');
			expect(wrapper.nextElementSibling!.textContent!.trim()).toBe('Close');
			expect(footerButtons()).toEqual(['Extra', 'Close']);
		});

		it('sits before Cancel and the confirm button with onconfirm', () => {
			render(ConfirmModal, {
				open: false,
				title: 'T',
				confirmLabel: 'Go',
				onconfirm: () => {},
				footer
			});
			expect(footerButtons()).toEqual(['Extra', 'Cancel', 'Go']);
			expect(
				document.querySelector('.modal-footer > .me-auto')!.nextElementSibling!.textContent!.trim()
			).toBe('Cancel');
		});

		it('without it the footer holds only its own buttons', () => {
			render(ConfirmModal, { open: false, title: 'T', confirmLabel: 'Go', onconfirm: () => {} });
			expect(document.querySelector('.modal-footer .me-auto')).toBeNull();
			expect(footerButtons()).toEqual(['Cancel', 'Go']);
		});
	});

	it('releases <body> before disposing on unmount (G1)', () => {
		const { unmount } = render(ConfirmModal, { open: true, title: 'T' });
		calls.length = 0;
		unmount();
		expect(calls).toEqual(['releaseModal', 'dispose']);
	});

	it('calls onshown each time Bootstrap reports the modal shown (B6)', () => {
		const onshown = vi.fn();
		const { container } = render(ConfirmModal, { open: true, title: 'T', onshown });
		const modal = container.querySelector('.modal')!;
		expect(onshown).not.toHaveBeenCalled();
		modal.dispatchEvent(new Event('shown.bs.modal'));
		modal.dispatchEvent(new Event('shown.bs.modal'));
		expect(onshown).toHaveBeenCalledTimes(2);
	});

	it('hides once shown when closed during the fade-in (Bootstrap drops that hide)', async () => {
		const onshown = vi.fn();
		const { container, rerender } = render(ConfirmModal, { open: true, title: 'T', onshown });
		await rerender({ open: false, title: 'T', onshown });
		calls.length = 0;
		container.querySelector('.modal')!.dispatchEvent(new Event('shown.bs.modal'));
		expect(calls).toEqual(['hide']);
		expect(onshown).not.toHaveBeenCalled();
	});

	it('size xl adds modal-xl', () => {
		const { container } = render(ConfirmModal, { open: false, title: 'T', size: 'xl' });
		const dialog = container.querySelector('.modal-dialog')!;
		expect(dialog.classList).toContain('modal-xl');
		expect(dialog.classList).not.toContain('modal-lg');
	});
});

describe('ConfirmModal beforeclose (3DO B11)', () => {
	function deferred() {
		let resolve!: () => void;
		let reject!: (e: unknown) => void;
		const promise = new Promise<void>((res, rej) => {
			resolve = res;
			reject = rej;
		});
		return { promise, resolve, reject };
	}

	const buttons = () => [
		document.querySelector('.btn-close') as HTMLButtonElement,
		...document.querySelectorAll<HTMLButtonElement>('.modal-footer button')
	];

	async function settle() {
		await Promise.resolve();
		await Promise.resolve();
		await tick();
		flushSync();
	}

	it.each([
		['×', () => buttons()[0].click()],
		['Close', () => buttons()[1].click()]
	])('%s waits for it with the buttons disabled, then closes', async (_, press) => {
		const pending = deferred();
		const beforeclose = vi.fn(() => pending.promise);
		render(ConfirmModal, { open: true, title: 'T', beforeclose });
		calls.length = 0;

		press();
		flushSync();
		press();
		flushSync();

		expect(beforeclose).toHaveBeenCalledTimes(1);
		expect(buttons().map((b) => b.disabled)).toEqual([true, true]);
		expect(calls).not.toContain('hide');

		pending.resolve();
		await settle();
		expect(calls).toContain('hide');
		expect(buttons().map((b) => b.disabled)).toEqual([false, false]);
	});

	it('closes when it rejects', async () => {
		const pending = deferred();
		render(ConfirmModal, { open: true, title: 'T', beforeclose: () => pending.promise });
		calls.length = 0;

		buttons()[0].click();
		pending.reject(new Error('no reply'));
		await settle();

		expect(calls).toContain('hide');
	});

	it('holds Escape (Bootstrap hide) until it settles', async () => {
		const pending = deferred();
		const beforeclose = vi.fn(() => pending.promise);
		const { container } = render(ConfirmModal, { open: true, title: 'T', beforeclose });
		calls.length = 0;
		const hide = new Event('hide.bs.modal', { cancelable: true });

		container.querySelector('.modal')!.dispatchEvent(hide);
		flushSync();

		expect(hide.defaultPrevented).toBe(true);
		expect(beforeclose).toHaveBeenCalledTimes(1);
		expect(calls).not.toContain('hide');
		pending.resolve();
		await settle();
		expect(calls).toContain('hide');
	});

	it('without it, × closes at once and Escape is not held', () => {
		const { container } = render(ConfirmModal, { open: true, title: 'T' });
		calls.length = 0;
		const hide = new Event('hide.bs.modal', { cancelable: true });

		container.querySelector('.modal')!.dispatchEvent(hide);
		expect(hide.defaultPrevented).toBe(false);
		buttons()[0].click();
		flushSync();
		expect(calls).toContain('hide');
	});
});
