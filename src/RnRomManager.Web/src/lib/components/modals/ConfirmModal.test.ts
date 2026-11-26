import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: ConfirmModal } = await import('./ConfirmModal.svelte');

describe('ConfirmModal', () => {
	it('shows the title and the message as one paragraph', () => {
		render(ConfirmModal, {
			open: false,
			title: 'Clear the bundle?',
			message: 'Every game leaves the bundle.',
			onconfirm: () => {}
		});
		expect(screen.getByRole('heading', { name: 'Clear the bundle?', hidden: true })).toBeTruthy();
		const body = document.querySelector('.modal-body p.mb-0');
		expect(body?.textContent).toBe('Every game leaves the bundle.');
	});

	it('calls onconfirm from the confirm button, with its label and class', async () => {
		const onconfirm = vi.fn();
		render(ConfirmModal, {
			open: false,
			title: 'T',
			message: 'M',
			confirmLabel: 'Clear',
			confirmClass: 'btn-danger',
			onconfirm
		});
		const button = screen.getByRole('button', { name: 'Clear', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
	});

	it('defaults to Confirm with btn-primary', () => {
		render(ConfirmModal, { open: false, title: 'T', message: 'M', onconfirm: () => {} });
		const button = screen.getByRole('button', { name: 'Confirm', hidden: true });
		expect(button.classList.contains('btn-primary')).toBe(true);
	});

	it('busy disables both buttons', () => {
		render(ConfirmModal, {
			open: false,
			title: 'T',
			message: 'M',
			busy: true,
			onconfirm: () => {}
		});
		expect(
			(screen.getByRole('button', { name: 'Cancel', hidden: true }) as HTMLButtonElement).disabled
		).toBe(true);
		expect(
			(screen.getByRole('button', { name: 'Confirm', hidden: true }) as HTMLButtonElement).disabled
		).toBe(true);
	});
});
