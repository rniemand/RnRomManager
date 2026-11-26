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

const { default: DismissReviewModal } = await import('./DismissReviewModal.svelte');

/** `extra` plus a bindable `open` (a spread would copy the getter's value and drop the setter). */
function bound<T extends object>(extra: T) {
	let open = true;
	const props = Object.defineProperty({ ...extra }, 'open', {
		get: () => open,
		set: (v: boolean) => (open = v),
		enumerable: true,
		configurable: true
	}) as T & { open: boolean };
	return { props, isOpen: () => open };
}

const body = () => document.querySelector('.modal-body')!;

describe('DismissReviewModal', () => {
	it('says where the file goes', () => {
		const { unmount } = render(DismissReviewModal, {
			open: true,
			archiveEntry: false,
			onconfirm: () => {}
		});
		expect(body().textContent).toContain("The file moves to the system's _failed folder.");
		unmount();
		render(DismissReviewModal, { open: true, archiveEntry: true, onconfirm: () => {} });
		expect(body().textContent?.trim()).toBe('The archive itself is left untouched.');
	});

	it('Dismiss calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		const { props, isOpen } = bound({ ...{ archiveEntry: false }, onconfirm });
		render(DismissReviewModal, props);
		const button = screen.getByRole('button', { name: 'Dismiss', hidden: true });
		expect(button.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(button);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
