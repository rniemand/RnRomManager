import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

const options: unknown[] = [];
const calls: string[] = [];
vi.mock('../bootstrap', () => ({
	Modal: class {
		constructor(_element: Element, config: unknown) {
			options.push(config);
		}
		show() {}
		hide() {}
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
});
