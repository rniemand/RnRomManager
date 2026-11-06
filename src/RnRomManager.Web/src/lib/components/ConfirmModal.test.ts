import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

const options: unknown[] = [];
vi.mock('../bootstrap', () => ({
	Modal: class {
		constructor(_element: Element, config: unknown) {
			options.push(config);
		}
		show() {}
		hide() {}
		dispose() {}
	}
}));

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

	it('info mode (no onconfirm) shows only Close', () => {
		render(ConfirmModal, { open: false, title: 'T' });
		const footer = document.querySelector('.modal-footer')!;
		expect([...footer.querySelectorAll('button')].map((b) => b.textContent?.trim())).toEqual([
			'Close'
		]);
	});
});
