import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { flushSync, tick } from 'svelte';

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

	it('releases <body> before disposing on unmount (G1)', () => {
		const { unmount } = render(ConfirmModal, { open: true, title: 'T' });
		calls.length = 0;
		unmount();
		expect(calls).toEqual(['releaseModal', 'dispose']);
	});

	it('calls onshown each time Bootstrap reports the modal shown (B6)', () => {
		const onshown = vi.fn();
		const { container } = render(ConfirmModal, { open: false, title: 'T', onshown });
		const modal = container.querySelector('.modal')!;
		expect(onshown).not.toHaveBeenCalled();
		modal.dispatchEvent(new Event('shown.bs.modal'));
		modal.dispatchEvent(new Event('shown.bs.modal'));
		expect(onshown).toHaveBeenCalledTimes(2);
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
